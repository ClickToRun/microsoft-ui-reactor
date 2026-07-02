using System.Collections.Immutable;
using System.Composition;
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
                        var collection = SyntaxFactory
                            .ParseExpression($"[.. {itemsText}, {valueText}]")
                            .WithTriviaFrom(itemsExpr);
                        editor.ReplaceNode(itemsExpr, collection);

                        // Drop the now-redundant `items.Add(v);` statement.
                        editor.RemoveNode(mutatorStatement, SyntaxRemoveOptions.KeepNoTrivia);

                        return Task.FromResult(context.Document.WithSyntaxRoot(editor.GetChangedRoot()));
                    },
                    equivalenceKey: HookRulesAnalyzer.MutateThenSetId),
                diagnostic);
        }
    }
}
