using System.Collections.Immutable;
using System.Composition;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Simplification;

namespace Microsoft.UI.Reactor.Analyzers;

/// <summary>
/// Code fix for <see cref="NoOpModifierAnalyzer"/> (REACTOR_MOD_003) on <b>shape</b> receivers:
/// rewrites the dropped modifier to the shape modifier that carries the same intent, so
/// <c>Rectangle().Background("#FF6B6B")</c> becomes <c>Rectangle().Fill(BrushHelper.Parse("#FF6B6B"))</c>
/// and <c>Line().Background(brush)</c> becomes <c>Line().Stroke(brush)</c>.
/// </summary>
/// <remarks>
/// <para>
/// The analyzer only emits <see cref="NoOpModifierAnalyzer.ReplacementKey"/> after confirming the
/// replacement resolves as an invocable member on that element type, so the rewrite cannot produce
/// a call that does not exist.
/// </para>
/// <para>
/// The colour-string arm needs the <c>BrushHelper.Parse</c> wrap because the shape modifiers take a
/// <c>Brush</c> while the common modifier has a <c>string</c> overload. That is exactly what
/// <c>ElementExtensions.Background(this T, string)</c> does internally, so the rewrite is
/// behaviour-preserving. <c>Microsoft.UI.Reactor.BrushHelper</c> is public and lives in the same
/// namespace as <c>ElementExtensions</c> — necessarily imported for the original call to have bound
/// — but it is emitted fully qualified with a <see cref="Simplifier"/> annotation so a local type of
/// the same name cannot capture it.
/// </para>
/// <para>
/// No fix is offered for the <c>ThemeRef</c> overload (no <c>Fill(ThemeRef)</c> counterpart exists)
/// or for non-shape receivers, where the remedy is a structural change — hosting the element in a
/// <c>Border</c> — rather than a rename. The diagnostic still reports in both cases.
/// </para>
/// </remarks>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(NoOpModifierCodeFix))]
[Shared]
public sealed class NoOpModifierCodeFix : CodeFixProvider
{
    private const string EquivalenceKey = "Reactor_NoOpModifier";
    private const string BrushHelperParse = "global::Microsoft.UI.Reactor.BrushHelper.Parse";

    public override ImmutableArray<string> FixableDiagnosticIds =>
        ImmutableArray.Create(NoOpModifierAnalyzer.DiagnosticId);

    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        if (root is null)
            return;

        foreach (var diagnostic in context.Diagnostics)
        {
            if (!diagnostic.Properties.TryGetValue(NoOpModifierAnalyzer.ReplacementKey, out var replacement)
                || string.IsNullOrEmpty(replacement))
                continue;

            if (!diagnostic.Properties.TryGetValue(NoOpModifierAnalyzer.ArgumentKindKey, out var argumentKind)
                || argumentKind is not (NoOpModifierAnalyzer.BrushArgument or NoOpModifierAnalyzer.StringArgument))
                continue;

            var node = root.FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie: true);
            if (node.FirstAncestorOrSelf<InvocationExpressionSyntax>() is not
                {
                    Expression: MemberAccessExpressionSyntax memberAccess,
                    ArgumentList.Arguments.Count: 1,
                } invocation)
                continue;

            var argument = invocation.ArgumentList.Arguments[0];

            context.RegisterCodeFix(
                CodeAction.Create(
                    $"Use '.{replacement}(...)'",
                    _ => Task.FromResult(context.Document.WithSyntaxRoot(
                        root.ReplaceNode(
                            invocation,
                            Rewrite(invocation, memberAccess, argument, replacement!, argumentKind!)))),
                    equivalenceKey: EquivalenceKey),
                diagnostic);
        }
    }

    private static InvocationExpressionSyntax Rewrite(
        InvocationExpressionSyntax invocation,
        MemberAccessExpressionSyntax memberAccess,
        ArgumentSyntax argument,
        string replacement,
        string argumentKind)
    {
        var renamed = memberAccess.WithName(
            SyntaxFactory.IdentifierName(replacement).WithTriviaFrom(memberAccess.Name));

        var rewritten = invocation.WithExpression(renamed);

        if (argumentKind != NoOpModifierAnalyzer.StringArgument)
            return rewritten;

        var parsed = SyntaxFactory.InvocationExpression(
            SyntaxFactory.ParseExpression(BrushHelperParse)
                .WithAdditionalAnnotations(Simplifier.Annotation),
            SyntaxFactory.ArgumentList(
                SyntaxFactory.SingletonSeparatedList(argument.WithoutTrivia())));

        return rewritten.WithArgumentList(
            invocation.ArgumentList.WithArguments(
                SyntaxFactory.SingletonSeparatedList(
                    argument.WithExpression(parsed))));
    }
}
