using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Microsoft.UI.Reactor.Analyzers;

/// <summary>
/// <c>REACTOR_ITEMS_002</c> — the <c>viewBuilder</c> lambda passed to <c>ItemsView&lt;T&gt;(...)</c>
/// returns something whose static type can never be an <c>ItemContainerElement</c>.
/// </summary>
/// <remarks>
/// <para>
/// Grounding: <c>ItemsViewElement&lt;T&gt;.GuardedViewBuilder</c> (<c>src/Reactor/Core/Element.cs</c>)
/// already throws <c>InvalidOperationException</c> for exactly this shape — but only at <em>mount</em>
/// time, once the page is actually opened. The guard exists because WinUI's inner
/// <c>ItemsRepeater</c> enters an infinite measure cycle when the item template produces non-container
/// roots, so a plain crash is the friendlier of the two outcomes. This analyzer promotes the same
/// check to build time; the message is deliberately worded like the runtime one so both teach the
/// same fix.
/// </para>
/// <para>
/// <b>Soundness.</b> A value whose static type is <c>T</c> can be an <c>ItemContainerElement</c> at
/// run time if and only if <c>T</c> is (or derives from) <c>ItemContainerElement</c>, or
/// <c>ItemContainerElement</c> derives from <c>T</c> — and under single inheritance the latter means
/// <c>T</c> is <c>Element</c> or <c>object</c>. So the rule fires only when the returned expression's
/// type is a class that <em>strictly</em> derives from <c>Element</c> and is neither
/// <c>ItemContainerElement</c> nor derived from it. Everything else stays silent: <c>Element</c>
/// itself (a helper typed to the base), interfaces, type parameters, <c>dynamic</c>, error types, and
/// <c>null</c> / target-typed expressions — the last of which is also what a conditional or
/// <c>switch</c> expression with mixed branch types produces, so the "mixed branches" bail-out falls
/// out of the same test. A false positive on valid code would be worse than the runtime throw we
/// already have.
/// </para>
/// <para>
/// The <c>viewBuilder</c> must be a lambda / anonymous method for the rule to see a return
/// expression at all; a method group (<c>ItemsView(items, key, BuildRow)</c>) is skipped. Matching is
/// confirmed semantically against <c>Microsoft.UI.Reactor.Factories</c> and the <c>viewBuilder</c>
/// parameter's <c>Func&lt;T, int, Element&gt;</c> shape, so the sibling collection factories
/// (<c>ListView</c>/<c>GridView</c>/<c>LazyVStack</c>/…), which carry no container requirement, and
/// any same-named method on an unrelated type never trip it.
/// </para>
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ItemsViewContainerRootAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "REACTOR_ITEMS_002";

    /// <summary>The Reactor element type ItemsView requires at the root of every realized item.</summary>
    internal const string ItemContainerMetadataName = "Microsoft.UI.Reactor.Core.ItemContainerElement";

    /// <summary>The <c>Factories</c> method this rule guards.</summary>
    internal const string ItemsViewFactoryName = "ItemsView";

    /// <summary>The parameter that supplies the per-item view builder.</summary>
    internal const string ViewBuilderParameterName = "viewBuilder";

    /// <summary>Positional index of <c>viewBuilder</c> on <c>ItemsView(items, keySelector, viewBuilder)</c>.</summary>
    private const int ViewBuilderPositionalIndex = 2;

    private static readonly LocalizableString Title =
        "ItemsView viewBuilder must return an ItemContainer root";

    private static readonly LocalizableString MessageFormat =
        "The ItemsView viewBuilder returns {0}. ItemsView requires an ItemContainer root — wrap it with ItemContainer(...).";

    private static readonly LocalizableString Description =
        "ItemsView realizes each item through its viewBuilder and hands the result to WinUI's inner " +
        "ItemsRepeater, whose selection, focus, and animation infrastructure assumes an ItemContainer " +
        "root; a non-container root sends it into an infinite measure cycle. Reactor turns that into " +
        "an InvalidOperationException at mount time, so a viewBuilder that returns anything but an " +
        "ItemContainer is never valid — wrap the returned element with ItemContainer(...).";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        Title,
        MessageFormat,
        "Reactor.Collections",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: Description);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(OnCompilationStart);
    }

    private static void OnCompilationStart(CompilationStartAnalysisContext context)
    {
        // Compilation-level gate: resolve the three anchor symbols once. If Reactor isn't
        // referenced there is nothing this rule can match, so we register no node action at all.
        // FactoriesMetadataName / ElementMetadataName are shared with the argument-shape analyzers
        // via ArgumentShapeGate so the fully-qualified names cannot drift apart.
        var factoriesType = context.Compilation.GetTypeByMetadataName(ArgumentShapeGate.FactoriesMetadataName);
        if (factoriesType is null)
            return;
        var elementType = context.Compilation.GetTypeByMetadataName(ArgumentShapeGate.ElementMetadataName);
        if (elementType is null)
            return;
        var containerType = context.Compilation.GetTypeByMetadataName(ItemContainerMetadataName);
        if (containerType is null)
            return;

        context.RegisterSyntaxNodeAction(
            ctx => AnalyzeInvocation(ctx, factoriesType, elementType, containerType),
            SyntaxKind.InvocationExpression);
    }

    private static void AnalyzeInvocation(
        SyntaxNodeAnalysisContext context,
        INamedTypeSymbol factoriesType,
        INamedTypeSymbol elementType,
        INamedTypeSymbol containerType)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;

        // ── Cheap name gate ───────────────────────────────────────────────
        // One string comparison skips the argument walk and the semantic lookup for every
        // invocation that isn't named ItemsView. The real match is confirmed semantically below.
        if (GetInvokedMethodName(invocation) != ItemsViewFactoryName)
            return;

        var args = invocation.ArgumentList.Arguments;
        if (args.Count <= ViewBuilderPositionalIndex)
            return;

        if (!TryGetViewBuilderArgument(args, out var argument, out var positionalIndex))
            return;

        // ── Cheap syntactic pre-gate ──────────────────────────────────────
        // Only a lambda / anonymous method exposes a return expression to type-check. A method
        // group (samples pass `BuildItem`) is deliberately out of scope.
        if (!TryGetAnonymousFunctionBody(argument.Expression, out var body))
            return;

        // ── Semantic confirmation ─────────────────────────────────────────
        // The invocation must bind to Microsoft.UI.Reactor.Factories.ItemsView and the argument must
        // land on its `Func<T, int, Element> viewBuilder` parameter. This rejects the sibling
        // collection factories (no container requirement) and same-named methods elsewhere.
        if (context.SemanticModel.GetSymbolInfo(invocation, context.CancellationToken).Symbol is not IMethodSymbol method)
            return;
        if (method.IsExtensionMethod)
            return;
        if (!SymbolEqualityComparer.Default.Equals(method.ContainingType, factoriesType))
            return;

        var parameter = ResolveBoundParameter(method, argument, positionalIndex);
        if (parameter is null || parameter.Name != ViewBuilderParameterName)
            return;
        if (!IsItemViewBuilderDelegate(parameter.Type, elementType))
            return;

        // Every return path is checked: one bad branch is enough to break the page at run time.
        foreach (var returned in ReturnExpressions(body!))
        {
            var type = context.SemanticModel.GetTypeInfo(returned, context.CancellationToken).Type;
            if (!IsProvablyNotItemContainer(type, elementType, containerType))
                continue;

            context.ReportDiagnostic(Diagnostic.Create(Rule, returned.GetLocation(), type!.Name));
        }
    }

    /// <summary>
    /// Finds the argument that supplies the <c>viewBuilder</c> — either explicitly named
    /// <c>viewBuilder:</c> (how the selftest fixtures write it), or the third positional argument.
    /// A positional argument always binds to the parameter at its own index in any call that
    /// compiles (C#'s non-trailing named arguments must sit in their own position), and the caller
    /// still confirms the resolved parameter is actually named <c>viewBuilder</c>.
    /// </summary>
    private static bool TryGetViewBuilderArgument(
        SeparatedSyntaxList<ArgumentSyntax> args,
        out ArgumentSyntax argument,
        out int positionalIndex)
    {
        foreach (var candidate in args)
        {
            if (candidate.NameColon?.Name.Identifier.ValueText == ViewBuilderParameterName)
            {
                argument = candidate;
                positionalIndex = -1; // bound by name
                return true;
            }
        }

        if (args[ViewBuilderPositionalIndex].NameColon is not null)
        {
            argument = null!;
            positionalIndex = -1;
            return false;
        }

        argument = args[ViewBuilderPositionalIndex];
        positionalIndex = ViewBuilderPositionalIndex;
        return true;
    }

    /// <summary>
    /// Matches the two-parameter lambda / anonymous method shapes a
    /// <c>Func&lt;T, int, Element&gt;</c> argument can take and yields its body. A parameterless
    /// <c>delegate { … }</c> is convertible to any delegate type, so it is accepted too. Method
    /// groups and single-parameter lambdas are rejected.
    /// </summary>
    private static bool TryGetAnonymousFunctionBody(ExpressionSyntax expression, out SyntaxNode? body)
    {
        switch (expression)
        {
            case ParenthesizedLambdaExpressionSyntax { ParameterList.Parameters.Count: 2 } lambda:
                body = lambda.Body;
                return body is not null;
            case AnonymousMethodExpressionSyntax anonymous
                when anonymous.ParameterList is null || anonymous.ParameterList.Parameters.Count == 2:
                body = anonymous.Body;
                return body is not null;
            default:
                body = null;
                return false;
        }
    }

    /// <summary>Maps the viewBuilder argument back to its bound parameter symbol.</summary>
    private static IParameterSymbol? ResolveBoundParameter(
        IMethodSymbol method, ArgumentSyntax argument, int positionalIndex)
    {
        if (argument.NameColon is { } nameColon)
        {
            var name = nameColon.Name.Identifier.ValueText;
            return method.Parameters.FirstOrDefault(p => p.Name == name);
        }

        return positionalIndex >= 0 && positionalIndex < method.Parameters.Length
            ? method.Parameters[positionalIndex]
            : null;
    }

    /// <summary>
    /// True for <c>System.Func&lt;T, int, Element&gt;</c> — the per-item view-builder shape. Pins
    /// the arity, the <c>int</c> index parameter, and the <c>Element</c> return so an unrelated
    /// three-argument <c>Func</c> parameter that happens to be named <c>viewBuilder</c> can't match.
    /// </summary>
    private static bool IsItemViewBuilderDelegate(ITypeSymbol type, INamedTypeSymbol elementType) =>
        type is INamedTypeSymbol { Name: "Func", TypeArguments.Length: 3 } func
        && func.ContainingNamespace?.ToDisplayString() == "System"
        && func.TypeArguments[1].SpecialType == SpecialType.System_Int32
        && SymbolEqualityComparer.Default.Equals(func.TypeArguments[2].OriginalDefinition, elementType);

    /// <summary>
    /// True when a value of this static type can <em>never</em> be an <c>ItemContainerElement</c> at
    /// run time — i.e. it is a class that strictly derives from <c>Element</c> and is neither
    /// <c>ItemContainerElement</c> nor derived from it. See the class remarks for why that test is
    /// exactly the sound one. A <see langword="null"/> type (a <c>null</c> literal, or a conditional
    /// / <c>switch</c> expression whose branches have no common type) yields <see langword="false"/>.
    /// </summary>
    private static bool IsProvablyNotItemContainer(
        ITypeSymbol? type, INamedTypeSymbol elementType, INamedTypeSymbol containerType)
    {
        if (type is null)
            return false;
        if (type is not INamedTypeSymbol named || named.TypeKind != TypeKind.Class)
            return false;

        var derivesFromElement = false;
        for (var candidate = named.BaseType; candidate is not null; candidate = candidate.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(candidate.OriginalDefinition, elementType))
            {
                derivesFromElement = true;
                break;
            }
        }

        if (!derivesFromElement)
            return false;

        for (INamedTypeSymbol? candidate = named; candidate is not null; candidate = candidate.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(candidate.OriginalDefinition, containerType))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Every expression the anonymous function can return: the expression body, or the operand of
    /// each <c>return</c> in a block body. Nested lambdas, anonymous methods, and local functions
    /// are not descended into — their <c>return</c>s belong to a different (unconstrained) delegate.
    /// </summary>
    private static IEnumerable<ExpressionSyntax> ReturnExpressions(SyntaxNode body)
    {
        if (body is ExpressionSyntax expression)
        {
            yield return expression;
            yield break;
        }

        var returns = body
            .DescendantNodes(descendIntoChildren: node => node == body || !IsAnonymousFunctionOrLocalFunction(node))
            .OfType<ReturnStatementSyntax>();

        foreach (var statement in returns)
        {
            if (statement.Expression is { } returned)
                yield return returned;
        }
    }

    private static bool IsAnonymousFunctionOrLocalFunction(SyntaxNode node) =>
        node is SimpleLambdaExpressionSyntax
            or ParenthesizedLambdaExpressionSyntax
            or AnonymousMethodExpressionSyntax
            or LocalFunctionStatementSyntax;

    private static string? GetInvokedMethodName(InvocationExpressionSyntax invocation) =>
        invocation.Expression switch
        {
            MemberAccessExpressionSyntax memberAccess => memberAccess.Name.Identifier.ValueText,
            SimpleNameSyntax simpleName => simpleName.Identifier.ValueText, // IdentifierName or GenericName
            _ => null,
        };
}
