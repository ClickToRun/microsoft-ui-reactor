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
/// Code fix for <see cref="ItemsViewContainerRootAnalyzer"/> (<c>REACTOR_ITEMS_002</c>) — wraps the
/// expression the <c>viewBuilder</c> returns in <c>ItemContainer(...)</c>, which is exactly the fix
/// the runtime guard's message asks for.
/// </summary>
/// <remarks>
/// <para>
/// The <em>whole</em> returned expression is wrapped, trailing fluent modifiers included:
/// <c>(p, i) =&gt; Border(…).Margin(4)</c> becomes
/// <c>(p, i) =&gt; ItemContainer(Border(…).Margin(4))</c>. Wrapping only the inner factory call
/// (<c>ItemContainer(Border(…)).Margin(4)</c>) would silently re-target every modifier in the chain
/// from the border onto the container.
/// </para>
/// <para>
/// The emitted call is a bare <c>ItemContainer(...)</c> when — and only when — every symbol of that
/// name in scope is the Reactor factory (the usual <c>using static Microsoft.UI.Reactor.Factories;</c>
/// case). Otherwise it is qualified with the factory's containing type, so a fully-qualified
/// <c>Factories.ItemsView(...)</c> call site or a user-defined <c>ItemContainer</c> shadow still gets
/// code that compiles and binds to the right method. If the factory cannot be resolved at all the fix
/// is withheld rather than emitting something that would not build.
/// </para>
/// </remarks>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(ItemsViewContainerRootCodeFix))]
[Shared]
public sealed class ItemsViewContainerRootCodeFix : CodeFixProvider
{
    private const string ItemContainerFactoryName = "ItemContainer";

    public override ImmutableArray<string> FixableDiagnosticIds =>
        ImmutableArray.Create(ItemsViewContainerRootAnalyzer.DiagnosticId);

    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        if (root is null) return;

        SemanticModel? semanticModel = null;

        foreach (var diagnostic in context.Diagnostics)
        {
            var node = root.FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie: true);
            if (node.FirstAncestorOrSelf<ExpressionSyntax>() is not { } returned)
                continue;

            semanticModel ??= await context.Document
                .GetSemanticModelAsync(context.CancellationToken).ConfigureAwait(false);
            if (semanticModel is null) continue;

            var factory = ResolveItemContainerFactory(semanticModel.Compilation);
            if (factory is null) continue;

            var target = BuildFactoryReference(factory, semanticModel, returned);

            var returnedForClosure = returned;
            context.RegisterCodeFix(
                CodeAction.Create(
                    "Wrap in ItemContainer(...)",
                    ct =>
                    {
                        // Clear only the returned expression's outer edge trivia (the wrapped node
                        // re-applies it below), leaving any interior comments / newlines intact.
                        var inner = returnedForClosure.WithLeadingTrivia().WithTrailingTrivia();

                        var wrapped = SyntaxFactory.InvocationExpression(
                            target,
                            SyntaxFactory.ArgumentList(
                                SyntaxFactory.SingletonSeparatedList(SyntaxFactory.Argument(inner))))
                            .WithTriviaFrom(returnedForClosure);

                        var newRoot = root.ReplaceNode(returnedForClosure, wrapped);
                        return Task.FromResult(context.Document.WithSyntaxRoot(newRoot));
                    },
                    equivalenceKey: ItemsViewContainerRootAnalyzer.DiagnosticId),
                diagnostic);
        }
    }

    /// <summary>
    /// The single-argument <c>Factories.ItemContainer(Element? child)</c> factory, or
    /// <see langword="null"/> when it isn't present in this compilation (in which case no fix is
    /// offered).
    /// </summary>
    private static IMethodSymbol? ResolveItemContainerFactory(Compilation compilation) =>
        compilation.GetTypeByMetadataName(ArgumentShapeGate.FactoriesMetadataName)
            ?.GetMembers(ItemContainerFactoryName)
            .OfType<IMethodSymbol>()
            .FirstOrDefault(m => m.IsStatic && m.Parameters.Length == 1);

    /// <summary>
    /// Builds the callee expression for the emitted wrap: the bare name when every
    /// <c>ItemContainer</c> visible at this position is the Reactor factory, otherwise the factory
    /// qualified by its containing type (rendered minimally for the call site, so it reads
    /// <c>Factories.ItemContainer</c> where <c>Microsoft.UI.Reactor</c> is imported).
    /// </summary>
    private static ExpressionSyntax BuildFactoryReference(
        IMethodSymbol factory, SemanticModel semanticModel, ExpressionSyntax position)
    {
        var visible = semanticModel.LookupSymbols(position.SpanStart, name: ItemContainerFactoryName);
        if (visible.Length > 0
            && visible.All(symbol => SymbolEqualityComparer.Default.Equals(symbol.ContainingType, factory.ContainingType)))
        {
            return SyntaxFactory.IdentifierName(ItemContainerFactoryName);
        }

        var containingType = SyntaxFactory.ParseTypeName(
            factory.ContainingType.ToMinimalDisplayString(semanticModel, position.SpanStart));

        return SyntaxFactory.MemberAccessExpression(
            SyntaxKind.SimpleMemberAccessExpression,
            containingType,
            SyntaxFactory.IdentifierName(ItemContainerFactoryName));
    }
}
