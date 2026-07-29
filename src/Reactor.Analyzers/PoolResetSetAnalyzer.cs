using System.Collections.Generic;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Microsoft.UI.Reactor.Analyzers;

/// <summary>
/// REACTOR_POOL_001: Detects <c>.Set(fe =&gt; fe.PROP = ...)</c> patterns where
/// <c>PROP</c> is a FrameworkElement property that <c>ElementPool.CleanElement</c>
/// resets on pool return (or that the reconciler clears between renders), and a
/// Reactor modifier exists that survives the reset. Suggests the fluent modifier.
/// Also reports REACTOR_VIS_001 for the closely-related imperative
/// <c>.Set(c =&gt; c.Visibility = ...)</c> case (see <see cref="VisibilityDiagnosticId"/>).
/// </summary>
/// <remarks>
/// The pool reset is intentional — it's how Reactor guarantees a clean rental.
/// But it makes <c>.Set(...)</c> writes to these properties silently disappear
/// on re-render. The modifier path (stored on <c>Element.Modifiers</c>) is
/// re-applied by the reconciler every render and so survives pool reuse.
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class PoolResetSetAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "REACTOR_POOL_001";

    /// <summary>
    /// REACTOR_VIS_001: an imperative <c>.Set(c =&gt; c.Visibility = Visibility.X)</c>
    /// that should be the declarative <c>.IsVisible(bool)</c> modifier. This is the same
    /// failure mode as POOL_001 — an un-reconciled imperative write lost on re-render /
    /// pool reuse — but <c>Visibility</c> is deliberately kept out of
    /// <see cref="TrappedProperties"/> because its modifier has a different signature
    /// (enum property vs. <c>bool</c> modifier), so it needs its own descriptor and a
    /// dedicated bool-translating code fix (<c>SetVisibilityCodeFix</c>).
    /// </summary>
    public const string VisibilityDiagnosticId = "REACTOR_VIS_001";

    /// <summary>
    /// REACTOR_MOD_002: a fluent modifier exists for this property, but it is not
    /// pool-reset — the value is written correctly, it just costs the element its
    /// structural skip (<c>Element.SettersEqual</c>) and is never unwound when a later
    /// render drops it. A preference rather than a bug, hence Info rather than Warning.
    /// </summary>
    public const string ModifierAvailableDiagnosticId = "REACTOR_MOD_002";

    /// <summary>
    /// Property → modifier name for the pool-reset subset, preserved as a public surface
    /// for callers that only care about that group. The authoritative table, including the
    /// non-pool-reset properties and the receiver gating, is <see cref="ModifierTable"/>.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> TrappedProperties =
        BuildTrappedProperties();

    private static IReadOnlyDictionary<string, string> BuildTrappedProperties()
    {
        var map = new Dictionary<string, string>(System.StringComparer.Ordinal);
        foreach (var pair in ModifierTable.Properties)
        {
            if (pair.Value.PoolReset)
                map[pair.Key] = pair.Value.Modifier;
        }
        return map;
    }

    private static readonly LocalizableString Title =
        "Use modifier instead of .Set for pool-reset property";

    private static readonly LocalizableString MessageFormat =
        "'{0}' is reset on pool return; '.Set(...)' writes to it are lost on re-render. Use '.{1}(...)' modifier instead.";

    private static readonly LocalizableString Description =
        "The element pool clears these FrameworkElement properties when a control is " +
        "returned for reuse, and the reconciler re-applies the modifier chain on every " +
        "render. Imperative '.Set(...)' assignments to these properties survive the " +
        "first render but disappear on the next reconcile. Use the corresponding " +
        "fluent modifier (stored on Element.Modifiers) so the value survives pool reuse.";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        Title,
        MessageFormat,
        "Reactor.Pool",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: Description);

    private static readonly LocalizableString VisibilityTitle =
        "Use .IsVisible(...) modifier instead of imperative .Set(Visibility = ...)";

    private static readonly LocalizableString VisibilityMessageFormat =
        "'.Set(c => c.Visibility = ...)' is imperative and not reconciled; the value is lost on the next render or on pool reuse. Use the '.IsVisible(bool)' modifier instead.";

    private static readonly LocalizableString VisibilityDescription =
        "Setting Visibility through '.Set(...)' bypasses the declarative modifier chain " +
        "the reconciler re-applies each render, so — like the pool-reset properties — the " +
        "value survives the first render but disappears on the next reconcile or when the " +
        "pooled control is reused. Use the '.IsVisible(bool)' modifier (or conditional " +
        "inclusion) so visibility is reconciled every render.";

    private static readonly DiagnosticDescriptor VisibilityRule = new(
        VisibilityDiagnosticId,
        VisibilityTitle,
        VisibilityMessageFormat,
        "Reactor.Layout",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: VisibilityDescription);

    private static readonly LocalizableString ModifierAvailableTitle =
        "Use the Reactor modifier instead of .Set";

    private static readonly LocalizableString ModifierAvailableMessageFormat =
        "A '.{1}(...)' modifier exists for '{0}'. Prefer it over '.Set(...)', which re-runs every render, is never unwound, and keeps the element on the reconciler's update path.";

    private static readonly LocalizableString ModifierAvailableDescription =
        "Reactor exposes a fluent modifier for this property. Modifier values are stored on " +
        "Element.Modifiers, structurally diffed, and cleared when removed, whereas '.Set(...)' " +
        "setters are imperative writes the reconciler cannot diff — Element.SettersEqual only " +
        "treats setter arrays as equal when they are the same instance or both empty, so any " +
        "element carrying setters re-runs them on every reconcile. Unlike the pool-reset " +
        "properties this is a preference rather than a correctness bug, so it reports as Info.";

    private static readonly DiagnosticDescriptor ModifierAvailableRule = new(
        ModifierAvailableDiagnosticId,
        ModifierAvailableTitle,
        ModifierAvailableMessageFormat,
        "Reactor.Modifier",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: ModifierAvailableDescription);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(Rule, VisibilityRule, ModifierAvailableRule);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeInvocation, SyntaxKind.InvocationExpression);
    }

    private static void AnalyzeInvocation(SyntaxNodeAnalysisContext context)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;

        if (!SetLambdaHelpers.IsSetInvocation(invocation, out var memberAccess))
            return;

        var lambdaExpr = invocation.ArgumentList.Arguments[0].Expression;

        // Detection considers every assignment in the body, not just a lone one: a
        // modifier-backed write is no less wrong for sharing a block with other statements.
        // PoolResetSetCodeFix independently re-checks for a single assignment, so
        // multi-statement bodies are reported but not auto-rewritten.
        var assignments = SetLambdaHelpers.GetLambdaAssignments(lambdaExpr);
        if (assignments.IsDefaultOrEmpty)
            return;

        // Both arms require the assignment target to be the .Set lambda's own parameter
        // ('fe.X = v', not 'captured.X = v') so the modifier rewrite applies to the pooled
        // control the .Set configures rather than some other captured object.
        var lambdaParam = SetLambdaHelpers.GetSingleLambdaParameter(lambdaExpr);
        if (lambdaParam is null)
            return;

        var isReactorSet = false;
        var reactorSetChecked = false;

        foreach (var assignment in assignments)
        {
            if (assignment.Kind() != SyntaxKind.SimpleAssignmentExpression)
                continue;

            var leftAccess = SetLambdaHelpers.GetAssignedMemberAccess(assignment, lambdaParam.Identifier.Text);
            if (leftAccess is null)
                continue;

            // Guard against an unrelated user-defined '.Set' helper with the same shape: only
            // Reactor's own .Set setters map to the Reactor modifiers these diagnostics/fixes
            // assume. Resolved lazily and once — it is the most expensive check here.
            if (!reactorSetChecked)
            {
                isReactorSet = SetLambdaHelpers.IsReactorSetInvocation(
                    invocation, context.SemanticModel, context.CancellationToken);
                reactorSetChecked = true;
            }
            if (!isReactorSet)
                return;

            AnalyzeAssignment(context, invocation, memberAccess, leftAccess, assignment);
        }
    }

    private static void AnalyzeAssignment(
        SyntaxNodeAnalysisContext context,
        InvocationExpressionSyntax invocation,
        MemberAccessExpressionSyntax memberAccess,
        MemberAccessExpressionSyntax leftAccess,
        AssignmentExpressionSyntax assignment)
    {
        var propName = leftAccess.Name.Identifier.Text;

        // REACTOR_VIS_001 — imperative Visibility toggling. Handled here as a POOL_001
        // extension: 'Visibility' is intentionally NOT in the modifier table (its modifier,
        // .IsVisible(bool), has a different signature than the enum property), so it gets a
        // distinct descriptor and its own bool-translating code fix. The receiver must derive
        // from UIElement so the '.IsVisible(...)' rewrite is always sound.
        if (propName == "Visibility")
        {
            var visibilityReceiver = context.SemanticModel
                .GetTypeInfo(leftAccess.Expression, context.CancellationToken).Type;
            if (SetLambdaHelpers.InheritsFrom(visibilityReceiver, "UIElement", "Microsoft.UI.Xaml"))
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    VisibilityRule,
                    invocation.GetLocation()));
            }
            return;
        }

        if (!ModifierTable.Properties.TryGetValue(propName, out var info))
            return;

        // A null / default right-hand side is not expressible through the modifier.
        // ApplyModifiers treats a null modifier value as "no modifier supplied" and only
        // clears the property when the PREVIOUS render had one, so `.Background(null)` does
        // not reliably write null the way `.Set(x => x.Background = null)` does. Suggesting
        // the rewrite here would change behaviour — the precise failure this analyzer exists
        // to prevent. Real site: samples/ReactorGallery/ControlPages/Media/ParallaxViewPage.cs.
        if (assignment.Right.IsKind(SyntaxKind.NullLiteralExpression)
            || assignment.Right.IsKind(SyntaxKind.DefaultLiteralExpression)
            || assignment.Right is DefaultExpressionSyntax)
        {
            return;
        }

        // Receiver gates. Both are checked against the semantic model rather than inferred:
        // for `.Set(x => …)` the lambda parameter's type IS the runtime WinUI control type
        // (the overload is Action<WinUI.Grid> and friends), and the `.Set` receiver's type
        // is the concrete Reactor element type.
        if (info.ControlGate is { } gate)
        {
            // ApplyModifiers writes this modifier only to certain control types; on anything
            // else it compiles and silently does nothing, so staying on .Set is correct.
            var controlType = context.SemanticModel
                .GetTypeInfo(leftAccess.Expression, context.CancellationToken).Type;

            var applies = false;
            foreach (var allowed in gate)
            {
                if (SetLambdaHelpers.InheritsFrom(controlType, allowed, "Microsoft.UI.Xaml.Controls"))
                {
                    applies = true;
                    break;
                }
            }
            if (!applies)
                return;
        }

        if (info.ElementTypes is { } elementTypes)
        {
            // No generic overload exists, so the rewrite only compiles when the receiver
            // element type declares one.
            var elementType = context.SemanticModel
                .GetTypeInfo(memberAccess.Expression, context.CancellationToken).Type;
            if (elementType is null)
                return;

            var declared = false;
            foreach (var candidate in elementTypes)
            {
                if (string.Equals(elementType.Name, candidate, System.StringComparison.Ordinal))
                {
                    declared = true;
                    break;
                }
            }
            if (!declared)
                return;
        }

        context.ReportDiagnostic(Diagnostic.Create(
            info.PoolReset ? Rule : ModifierAvailableRule,
            invocation.GetLocation(),
            propName,
            info.Modifier));
    }
}
