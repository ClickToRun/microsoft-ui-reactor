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
/// Code fix for <see cref="NoOpModifierAnalyzer"/> (REACTOR_MOD_003): rewrites the dropped modifier
/// to the one that carries the same intent on this element, so
/// <c>Rectangle().Background("#FF6B6B")</c> becomes
/// <c>Rectangle().Fill(BrushHelper.Parse("#FF6B6B"))</c>, <c>Line().Background(brush)</c> becomes
/// <c>Line().Stroke(brush)</c>, and <c>Flex().Padding(16)</c> becomes <c>Flex().FlexPadding(16)</c>.
/// </summary>
/// <remarks>
/// <para>
/// The analyzer only emits <see cref="NoOpModifierAnalyzer.ReplacementKey"/> and
/// <see cref="NoOpModifierAnalyzer.ArgumentKindKey"/> after confirming a specific overload of the
/// replacement accepts the call's arguments, so the rewrite cannot produce a call that does not
/// exist or does not bind. Everything else reports without a fix.
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
/// No fix is offered for the <c>ThemeRef</c> overload (no <c>Fill(ThemeRef)</c> counterpart exists),
/// for named or partially-applied arguments (the parameter names and optionality differ between the
/// two modifiers), or for receivers with no equivalent modifier at all — there the remedy is a
/// structural change, such as hosting the element in a <c>Border</c>. The diagnostic still reports
/// in every one of those cases.
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
                || argumentKind is not (NoOpModifierAnalyzer.RenameArgument or NoOpModifierAnalyzer.StringArgument))
                continue;

            var node = root.FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie: true);
            if (node.FirstAncestorOrSelf<InvocationExpressionSyntax>() is not
                {
                    Expression: MemberAccessExpressionSyntax memberAccess,
                } invocation)
                continue;

            // The analyzer only emits StringArgument for a single-argument call.
            if (argumentKind == NoOpModifierAnalyzer.StringArgument
                && invocation.ArgumentList.Arguments.Count != 1)
                continue;

            context.RegisterCodeFix(
                CodeAction.Create(
                    $"Use '.{replacement}(...)'",
                    _ => Task.FromResult(context.Document.WithSyntaxRoot(
                        root.ReplaceNode(
                            invocation,
                            Rewrite(invocation, memberAccess, replacement!, argumentKind!)))),
                    equivalenceKey: EquivalenceKey),
                diagnostic);
        }
    }

    private static InvocationExpressionSyntax Rewrite(
        InvocationExpressionSyntax invocation,
        MemberAccessExpressionSyntax memberAccess,
        string replacement,
        string argumentKind)
    {
        var renamed = memberAccess.WithName(
            SyntaxFactory.IdentifierName(replacement).WithTriviaFrom(memberAccess.Name));

        var rewritten = invocation.WithExpression(renamed);

        if (argumentKind != NoOpModifierAnalyzer.StringArgument)
            return rewritten;

        var argument = invocation.ArgumentList.Arguments[0];
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
