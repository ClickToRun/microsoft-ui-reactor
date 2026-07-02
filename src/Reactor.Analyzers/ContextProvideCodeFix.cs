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
/// Code fix for <see cref="ContextProvideAnalyzer"/> (<c>REACTOR_CTX_001</c>) — wraps a
/// freshly-allocated context value in <c>UseMemo(() =&gt; …, [])</c> so the same instance is reused
/// across renders and consumers stop thrashing.
/// </summary>
/// <remarks>
/// The deps default to empty (<c>[]</c>, "allocate once"); when the value closes over render state
/// the author widens them. The fix is only offered when a Reactor <c>UseMemo</c> is in scope at the
/// call site so the emitted code always compiles.
/// </remarks>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(ContextProvideCodeFix))]
[Shared]
public sealed class ContextProvideCodeFix : CodeFixProvider
{
    public override ImmutableArray<string> FixableDiagnosticIds =>
        ImmutableArray.Create(ContextProvideAnalyzer.Id);

    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        if (root is null) return;

        SemanticModel? semanticModel = null;

        foreach (var diagnostic in context.Diagnostics)
        {
            var node = root.FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie: true);
            var valueExpr = node.FirstAncestorOrSelf<ExpressionSyntax>();
            if (valueExpr is null) continue;

            // UseMemo is a Component/RenderContext hook; only offer the wrap when a Reactor UseMemo
            // is actually in scope here (otherwise the emitted call would not compile).
            semanticModel ??= await context.Document
                .GetSemanticModelAsync(context.CancellationToken).ConfigureAwait(false);
            if (semanticModel is null) continue;
            if (!semanticModel.LookupSymbols(valueExpr.SpanStart, name: "UseMemo").Any(static s =>
                    s is IMethodSymbol m && CommandDebounceAnalyzer.IsReactorNamespace(m.ContainingNamespace?.ToDisplayString())))
                continue;

            var captured = valueExpr;
            context.RegisterCodeFix(
                CodeAction.Create(
                    "Memoize the context value with UseMemo(() => …, [])",
                    ct =>
                    {
                        var wrapped = SyntaxFactory
                            .ParseExpression($"UseMemo(() => {captured.WithoutTrivia().ToFullString()}, [])")
                            .WithTriviaFrom(captured);
                        var newRoot = root.ReplaceNode(captured, wrapped);
                        return Task.FromResult(context.Document.WithSyntaxRoot(newRoot));
                    },
                    equivalenceKey: ContextProvideAnalyzer.Id),
                diagnostic);
        }
    }
}
