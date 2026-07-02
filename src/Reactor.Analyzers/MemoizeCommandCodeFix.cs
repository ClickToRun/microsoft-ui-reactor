using System.Collections.Generic;
using System.Collections.Immutable;
using System.Composition;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Microsoft.UI.Reactor.Analyzers;

/// <summary>
/// Code fix for <see cref="MemoizeCommandAnalyzer"/> (<c>REACTOR_PERF_FUNCREF</c>) — wraps the
/// offending <c>new Command { … }</c> in <c>UseMemo(() =&gt; new Command { … }, deps)</c> so the
/// command keeps a stable instance across renders.
/// </summary>
/// <remarks>
/// <para>
/// The dependency list is computed from a data-flow analysis of the creation expression: every
/// local / parameter read inside it (directly or captured by a nested lambda such as
/// <c>Execute = () =&gt; setCount(count + 1)</c>) that is declared outside becomes a
/// <c>UseMemo</c> dependency, so the memo re-computes exactly when a captured value changes and
/// never serves a stale closure. When nothing is captured the deps list is empty
/// (<c>UseMemo(() =&gt; new Command { … })</c>) — a compute-once memo, which is safe precisely
/// because there is nothing to go stale.
/// </para>
/// <para>
/// A Reactor <c>UseMemo</c> must be in scope for the wrap to compile, so the fix is only offered
/// inside a <c>Component</c> / <c>RenderContext</c> body. A target-typed <c>new() { … }</c> is
/// rewritten to an explicit <c>new Command&lt;T&gt; { … }</c> first (its target type is lost once it
/// becomes the body of the memo lambda). Mirrors <see cref="CommandDebounceCodeFix"/>.
/// </para>
/// </remarks>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(MemoizeCommandCodeFix))]
[Shared]
public sealed class MemoizeCommandCodeFix : CodeFixProvider
{
    public override ImmutableArray<string> FixableDiagnosticIds =>
        ImmutableArray.Create(MemoizeCommandAnalyzer.Id);

    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        if (root is null) return;

        SemanticModel? semanticModel = null;

        foreach (var diagnostic in context.Diagnostics)
        {
            var node = root.FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie: true);
            var creation = node.FirstAncestorOrSelf<ExpressionSyntax>(static e =>
                e is ObjectCreationExpressionSyntax or ImplicitObjectCreationExpressionSyntax);
            if (creation is null) continue;

            semanticModel ??= await context.Document
                .GetSemanticModelAsync(context.CancellationToken).ConfigureAwait(false);
            if (semanticModel is null) continue;

            // UseMemo is a Reactor Component / RenderContext hook, so it must be a *Reactor* UseMemo
            // in scope here for the wrap to compile and actually memoize. If none is (e.g. a static
            // helper, or a same-named unrelated method), skip the fix — the Info diagnostic still
            // fires and the author lifts the command by hand. Never emit broken or no-op code.
            if (!semanticModel.LookupSymbols(creation.SpanStart, name: "UseMemo").Any(static s =>
                    s is IMethodSymbol m && CommandDebounceAnalyzer.IsReactorNamespace(m.ContainingNamespace?.ToDisplayString())))
                continue;

            // A target-typed `new() { … }` is typed by its surrounding context; once it becomes the
            // body of the memo lambda that context is gone. Rewrite it to an explicit
            // `new Command<T> { … }` using the resolved type, or skip if it can't be resolved.
            ExpressionSyntax inner = creation;
            if (creation is ImplicitObjectCreationExpressionSyntax implicitNew)
            {
                if (implicitNew.Initializer is null) continue;
                var type = semanticModel.GetTypeInfo(implicitNew, context.CancellationToken).Type;
                if (type is null || type.TypeKind == TypeKind.Error) continue;
                inner = MakeExplicit(implicitNew, type, semanticModel);
            }

            var deps = ComputeDependencies(semanticModel, creation, context.CancellationToken);

            var creationForClosure = creation;
            var innerForClosure = inner;
            context.RegisterCodeFix(
                CodeAction.Create(
                    "Wrap command in UseMemo(...)",
                    ct =>
                    {
                        // `() => <command>` with explicit single-space arrow trivia so the emitted
                        // fix reads `() => new Command { … }` regardless of factory defaults.
                        var lambda = SyntaxFactory.ParenthesizedLambdaExpression(
                            SyntaxFactory.ParameterList(),
                            innerForClosure.WithoutTrivia())
                            .WithArrowToken(SyntaxFactory.Token(SyntaxKind.EqualsGreaterThanToken)
                                .WithLeadingTrivia(SyntaxFactory.Space)
                                .WithTrailingTrivia(SyntaxFactory.Space));

                        // Build the argument list with explicit `, ` separators (a comma with a
                        // trailing space) so `UseMemo(() => …, count, setCount)` is formatted normally.
                        var nodesAndTokens = new List<SyntaxNodeOrToken> { SyntaxFactory.Argument(lambda) };
                        foreach (var dep in deps)
                        {
                            nodesAndTokens.Add(SyntaxFactory.Token(SyntaxKind.CommaToken)
                                .WithTrailingTrivia(SyntaxFactory.Space));
                            nodesAndTokens.Add(SyntaxFactory.Argument(SyntaxFactory.IdentifierName(dep)));
                        }

                        var wrapped = SyntaxFactory.InvocationExpression(
                            SyntaxFactory.IdentifierName("UseMemo"),
                            SyntaxFactory.ArgumentList(
                                SyntaxFactory.SeparatedList<ArgumentSyntax>(nodesAndTokens)))
                            .WithTriviaFrom(creationForClosure);

                        var newRoot = root.ReplaceNode(creationForClosure, wrapped);
                        return Task.FromResult(context.Document.WithSyntaxRoot(newRoot));
                    },
                    equivalenceKey: MemoizeCommandAnalyzer.Id),
                diagnostic);
        }
    }

    // The captured dependencies of the creation expression: locals / parameters that are read inside
    // it — directly or captured by a nested lambda — and declared outside. These become the UseMemo
    // deps so the memo re-computes exactly when a captured value changes (no stale closure). The union
    // of DataFlowsIn / ReadInside / CapturedInside covers both direct reads and nested-lambda captures;
    // VariablesDeclared removes anything local to the expression itself. Ordered for deterministic output.
    private static ImmutableArray<string> ComputeDependencies(
        SemanticModel model, ExpressionSyntax creation, System.Threading.CancellationToken ct)
    {
        var flow = model.AnalyzeDataFlow(creation);
        if (flow is null || !flow.Succeeded) return ImmutableArray<string>.Empty;

        var declared = new HashSet<ISymbol>(flow.VariablesDeclared, SymbolEqualityComparer.Default);
        var seen = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        var names = new List<string>();

        foreach (var symbol in flow.DataFlowsIn.Concat(flow.ReadInside).Concat(flow.CapturedInside))
        {
            if (symbol is not (ILocalSymbol or IParameterSymbol)) continue;
            // `this` (an implicit parameter, captured whenever the command references an instance
            // member such as `Execute = Save`) is stable across renders — never a memo dependency.
            if (symbol is IParameterSymbol { IsThis: true }) continue;
            if (declared.Contains(symbol)) continue;
            if (!seen.Add(symbol)) continue;
            names.Add(symbol.Name);
        }

        names.Sort(System.StringComparer.Ordinal);
        return names.ToImmutableArray();
    }

    /// <summary>
    /// Rebuilds a target-typed <c>new() { … }</c> as an explicit <c>new Command&lt;T&gt; { … }</c>
    /// using the resolved <paramref name="type"/>, preserving the initializer (and any constructor
    /// arguments) verbatim. The type name is rendered with
    /// <see cref="SymbolDisplayExtensions.ToMinimalDisplayString"/> so it stays short yet unambiguous
    /// at this position. Mirrors <see cref="CommandDebounceCodeFix"/>.
    /// </summary>
    private static ObjectCreationExpressionSyntax MakeExplicit(
        ImplicitObjectCreationExpressionSyntax implicitNew, ITypeSymbol type, SemanticModel semanticModel)
    {
        var typeSyntax = SyntaxFactory.ParseTypeName(
            type.ToMinimalDisplayString(semanticModel, implicitNew.SpanStart));

        ArgumentListSyntax? argumentList = implicitNew.ArgumentList;
        if (argumentList is null || argumentList.Arguments.Count == 0)
        {
            argumentList = null;
            typeSyntax = typeSyntax.WithTrailingTrivia(SyntaxFactory.Space);
        }

        return SyntaxFactory.ObjectCreationExpression(
            SyntaxFactory.Token(SyntaxKind.NewKeyword).WithTrailingTrivia(SyntaxFactory.Space),
            typeSyntax,
            argumentList,
            implicitNew.Initializer);
    }
}
