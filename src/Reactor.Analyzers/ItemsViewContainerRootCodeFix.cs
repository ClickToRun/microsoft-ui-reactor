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
/// Only diagnostics carrying <see cref="ItemsViewContainerRootAnalyzer.WrappableProperty"/> are
/// fixed. The method-group form (<c>ItemsView(items, key, BuildRow)</c>) reports on the method group
/// itself, where there is no returned expression to wrap, so it is a nudge only.
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
            // The method-group form of the diagnostic has no returned expression at the call site,
            // so there is nothing to wrap — it is a nudge to fix the helper's own return.
            if (!diagnostic.Properties.ContainsKey(ItemsViewContainerRootAnalyzer.WrappableProperty))
                continue;

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
    /// The <c>Factories.ItemContainer(Element? child)</c> factory, or <see langword="null"/> when
    /// this compilation has no overload that could actually wrap an element (in which case no fix is
    /// offered).
    /// </summary>
    /// <remarks>
    /// The match is pinned to the <em>shape</em> the emitted code depends on — static, one
    /// <c>Element</c>-typed parameter, returning an <c>ItemContainerElement</c> — not just to a
    /// single-parameter method of the right name. A name-and-arity match would happily accept a
    /// hypothetical <c>ItemContainer(string)</c> convenience overload and then offer a wrap that
    /// could not compile. C# forbids duplicate signatures on one type, so at most one overload can
    /// satisfy this, which is also what makes the choice deterministic rather than
    /// metadata-order-dependent.
    /// </remarks>
    private static IMethodSymbol? ResolveItemContainerFactory(Compilation compilation)
    {
        var factoriesType = compilation.GetTypeByMetadataName(ArgumentShapeGate.FactoriesMetadataName);
        if (factoriesType is null)
            return null;

        var elementType = compilation.GetTypeByMetadataName(ArgumentShapeGate.ElementMetadataName);
        var containerType = compilation.GetTypeByMetadataName(
            ItemsViewContainerRootAnalyzer.ItemContainerMetadataName);
        if (elementType is null || containerType is null)
            return null;

        foreach (var member in factoriesType.GetMembers(ItemContainerFactoryName))
        {
            if (member is not IMethodSymbol { IsStatic: true } candidate || candidate.Parameters.Length != 1)
                continue;
            if (!SymbolEqualityComparer.Default.Equals(
                    candidate.Parameters[0].Type.OriginalDefinition, elementType))
                continue;
            if (!IsContainerOrDerived(candidate.ReturnType, containerType))
                continue;

            return candidate;
        }

        return null;
    }

    /// <summary>True when <paramref name="type"/> is <c>ItemContainerElement</c> or derives from it.</summary>
    private static bool IsContainerOrDerived(ITypeSymbol type, INamedTypeSymbol containerType)
    {
        for (var candidate = type as INamedTypeSymbol; candidate is not null; candidate = candidate.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(candidate.OriginalDefinition, containerType))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Builds the callee expression for the emitted wrap: the bare name when every
    /// <c>ItemContainer</c> visible at this position is the Reactor factory, otherwise the factory
    /// qualified by its containing type (rendered minimally for the call site, so it reads
    /// <c>Factories.ItemContainer</c> where <c>Microsoft.UI.Reactor</c> is imported and
    /// <c>Microsoft.UI.Reactor.Factories.ItemContainer</c> where it is not).
    /// </summary>
    /// <remarks>
    /// The qualified form is built with <see cref="SyntaxFactory.ParseExpression"/> over the whole
    /// dotted string rather than <c>MemberAccessExpression(ParseTypeName(type), name)</c>. When the
    /// minimal name needs namespace qualification, <c>ParseTypeName</c> yields a
    /// <c>QualifiedNameSyntax</c>; wrapping that in a member access emits the right <em>text</em> but
    /// a tree shape the parser never produces for it (C# models <c>A.B.C.M</c> in expression position
    /// as nested <c>MemberAccessExpressionSyntax</c>), so the fixed document fails to round-trip.
    /// Parsing as an expression yields the correct chain. Mirrors <see cref="UseThemeRefCodeFix"/>.
    /// </remarks>
    private static ExpressionSyntax BuildFactoryReference(
        IMethodSymbol factory, SemanticModel semanticModel, ExpressionSyntax position)
    {
        var visible = semanticModel.LookupSymbols(position.SpanStart, name: ItemContainerFactoryName);
        if (visible.Length > 0
            && visible.All(symbol => SymbolEqualityComparer.Default.Equals(symbol.ContainingType, factory.ContainingType)))
        {
            return SyntaxFactory.IdentifierName(ItemContainerFactoryName);
        }

        var containingType = factory.ContainingType.ToMinimalDisplayString(semanticModel, position.SpanStart);
        return SyntaxFactory.ParseExpression($"{containingType}.{ItemContainerFactoryName}");
    }
}
