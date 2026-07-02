using System.Collections.Immutable;
using System.Composition;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Editing;

namespace Microsoft.UI.Reactor.Analyzers;

/// <summary>
/// Code fix for <see cref="HookRulesAnalyzer.MutateThenSetId"/> (<c>REACTOR_HOOKS_010</c>) for the
/// common <c>items.Add(v); setItems(items);</c> shape: drops the in-place mutation and passes a
/// NEW value to the setter — <c>setItems([.. items, v]);</c>.
/// </summary>
/// <remarks>
/// The rewrite emits a <b>value</b> (a collection expression), never a functional updater
/// (<c>setItems(prev =&gt; …)</c>) — the UseState/UsePersisted setter is <c>Action&lt;T&gt;</c>, not
/// <c>Action&lt;Func&lt;T,T&gt;&gt;</c>. Only the single-argument <c>.Add(v)</c> mutation is fixable
/// (flagged via the diagnostic's <c>canFix</c> property); other mutators (<c>Remove</c>,
/// <c>Clear</c>, indexer set, …) keep the warning with no auto-fix.
/// </remarks>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(MutateThenSetCodeFix))]
[Shared]
public sealed class MutateThenSetCodeFix : CodeFixProvider
{
    public override ImmutableArray<string> FixableDiagnosticIds =>
        ImmutableArray.Create(HookRulesAnalyzer.MutateThenSetId);

    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        if (root is null) return;

        foreach (var diagnostic in context.Diagnostics)
        {
            // The analyzer only marks the single-arg .Add(v) shape as fixable.
            if (!diagnostic.Properties.TryGetValue("canFix", out var canFix) || canFix != "true") continue;
            if (diagnostic.AdditionalLocations.Count == 0) continue;

            // Setter call: setItems(items).
            var setterCall = root.FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie: true)
                .FirstAncestorOrSelf<InvocationExpressionSyntax>();
            if (setterCall is null) continue;
            var setterArgs = setterCall.ArgumentList.Arguments;
            if (setterArgs.Count != 1) continue;
            var itemsExpr = setterArgs[0].Expression;

            // Mutator call: items.Add(v).
            var mutatorCall = root.FindNode(diagnostic.AdditionalLocations[0].SourceSpan, getInnermostNodeForTie: true)
                .FirstAncestorOrSelf<InvocationExpressionSyntax>();
            if (mutatorCall is null) continue;
            if (mutatorCall.ArgumentList.Arguments.Count != 1) continue;
            var valueExpr = mutatorCall.ArgumentList.Arguments[0].Expression;

            var setterStatement = setterCall.FirstAncestorOrSelf<ExpressionStatementSyntax>();
            if (setterStatement is null) continue;

            var mutatorStatement = mutatorCall.FirstAncestorOrSelf<ExpressionStatementSyntax>();
            if (mutatorStatement is null) continue;

            var itemsText = itemsExpr.ToString();
            var valueText = valueExpr.ToString();

            context.RegisterCodeFix(
                CodeAction.Create(
                    "Set a new value with a collection expression",
                    ct =>
                    {
                        var editor = new SyntaxEditor(root, context.Document.Project.Solution.Workspace.Services);

                        // setItems(items) → setItems([.. items, v])
                        var collection = SyntaxFactory.ParseExpression($"[.. {itemsText}, {valueText}]");
                        var newSetterCall = setterCall.WithArgumentList(
                            SyntaxFactory.ArgumentList(SyntaxFactory.SingletonSeparatedList(
                                SyntaxFactory.Argument(collection))));
                        var newSetterStatement = setterStatement.ReplaceNode(setterCall, newSetterCall);

                        // Preserve the mutator line's comments / #directives so user content is
                        // never silently dropped.
                        if (mutatorStatement.ContainsDirectives)
                        {
                            // Directives (#if/#endif …) must keep their balance and stay on their own
                            // lines — let Roslyn's directive-aware removal handle them (whitespace may
                            // be slightly imperfect, but nothing is lost and the region stays valid).
                            editor.ReplaceNode(setterStatement, newSetterStatement);
                            editor.RemoveNode(mutatorStatement,
                                SyntaxRemoveOptions.KeepLeadingTrivia
                                | SyntaxRemoveOptions.KeepTrailingTrivia
                                | SyntaxRemoveOptions.KeepDirectives);
                        }
                        else
                        {
                            // Comment-only: move leading trivia onto the setter's leading (correct
                            // indentation) and an inline trailing comment onto the setter's trailing
                            // (kept inline), then remove the mutator cleanly.
                            if (mutatorStatement.GetLeadingTrivia().Any(IsComment))
                                newSetterStatement = newSetterStatement.WithLeadingTrivia(mutatorStatement.GetLeadingTrivia());

                            var trailingComments = mutatorStatement.GetTrailingTrivia().Where(IsComment).ToList();
                            if (trailingComments.Count > 0)
                                newSetterStatement = newSetterStatement.WithTrailingTrivia(
                                    MergeTrailingComment(newSetterStatement.GetTrailingTrivia(), trailingComments));

                            editor.ReplaceNode(setterStatement, newSetterStatement);
                            editor.RemoveNode(mutatorStatement, SyntaxRemoveOptions.KeepNoTrivia);
                        }

                        return Task.FromResult(context.Document.WithSyntaxRoot(editor.GetChangedRoot()));
                    },
                    equivalenceKey: HookRulesAnalyzer.MutateThenSetId),
                diagnostic);
        }
    }

    private static bool IsComment(SyntaxTrivia t) =>
        t.IsKind(SyntaxKind.SingleLineCommentTrivia) || t.IsKind(SyntaxKind.MultiLineCommentTrivia);

    /// <summary>
    /// Inserts <paramref name="comments"/> (with a leading space) just before the setter statement's
    /// end-of-line, keeping the transferred comment inline (e.g. <c>setItems(…); // keep</c>).
    /// </summary>
    private static SyntaxTriviaList MergeTrailingComment(SyntaxTriviaList setterTrailing, System.Collections.Generic.List<SyntaxTrivia> comments)
    {
        var rebuilt = new System.Collections.Generic.List<SyntaxTrivia>();
        var inserted = false;
        foreach (var t in setterTrailing)
        {
            if (!inserted && t.IsKind(SyntaxKind.EndOfLineTrivia))
            {
                rebuilt.Add(SyntaxFactory.Space);
                rebuilt.AddRange(comments);
                inserted = true;
            }
            rebuilt.Add(t);
        }
        if (!inserted)
        {
            rebuilt.Add(SyntaxFactory.Space);
            rebuilt.AddRange(comments);
        }
        return SyntaxFactory.TriviaList(rebuilt);
    }
}
