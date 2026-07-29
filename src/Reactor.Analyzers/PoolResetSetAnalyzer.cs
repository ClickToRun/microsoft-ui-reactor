using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
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

    /// <summary>
    /// Diagnostic-property key carrying the comma-separated names of <em>every</em> WinUI
    /// property reported on this <c>.Set(...)</c>, so <see cref="PoolResetSetCodeFix"/> knows
    /// exactly which assignments passed the gates.
    /// <para>
    /// Load-bearing for multi-statement bodies. A block can mix an assignment that was
    /// reported with one the analyzer deliberately skipped (a gated property on the wrong
    /// control type, or one with no modifier at all). Without this the fix would re-derive
    /// candidates from the table alone and could rewrite an assignment that was gated out —
    /// producing exactly the silent no-op the gating exists to prevent.
    /// </para>
    /// <para>
    /// Every diagnostic on the invocation carries the <em>whole</em> set rather than just its
    /// own property, because a code fix provider is not guaranteed to be handed all the
    /// diagnostics sharing a span — Roslyn's <c>CodeFixService</c> groups them, but
    /// <c>Microsoft.CodeAnalysis.Testing</c> invokes the provider once per diagnostic. Making
    /// each diagnostic self-sufficient keeps the fix correct under both.
    /// </para>
    /// </summary>
    internal const string ReportedPropertiesKey = "ReactorReportedProperties";

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

        // Explicit filter (CodeQL cs/linq/missed-where): only simple assignments are
        // candidates here. '+=' on an event is REACTOR_EVENT_001's job, and a numeric
        // compound assignment has no modifier equivalent.
        var simpleAssignments = assignments
            .Where(assignment => assignment.IsKind(SyntaxKind.SimpleAssignmentExpression));

        // Two passes. The first classifies every assignment; the second reports, stamping each
        // diagnostic with the complete reported set. The code fix needs the whole set to decide
        // whether a block body is convertible in full, and cannot rely on being handed its
        // siblings (see ReportedPropertiesKey).
        var reportable = new List<(MemberAccessExpressionSyntax Left, ModifierInfo Info, string PropName)>();

        foreach (var assignment in simpleAssignments)
        {
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

            var classified = ClassifyAssignment(context, invocation, memberAccess, leftAccess, assignment);
            if (classified is { } hit)
                reportable.Add((leftAccess, hit.Info, hit.PropName));
        }

        if (reportable.Count == 0)
            return;

        var reportedProperties = ImmutableDictionary<string, string?>.Empty.Add(
            ReportedPropertiesKey,
            string.Join(",", reportable.Select(r => r.PropName)));

        foreach (var (_, info, propName) in reportable)
        {
            context.ReportDiagnostic(Diagnostic.Create(
                info.PoolReset ? Rule : ModifierAvailableRule,
                invocation.GetLocation(),
                properties: reportedProperties,
                propName,
                info.Modifier));
        }
    }

    /// <summary>
    /// Decide whether one assignment inside a <c>.Set(...)</c> body should be reported as
    /// having a usable modifier. Returns <c>null</c> when it should stay on <c>.Set</c>.
    /// </summary>
    /// <remarks>
    /// REACTOR_VIS_001 is reported inline here and returns <c>null</c>: it has its own
    /// descriptor and its own code fix, so it must not join a REACTOR_POOL_001/MOD_002
    /// modifier chain.
    /// </remarks>
    private static (string PropName, ModifierInfo Info)? ClassifyAssignment(
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
            return null;
        }

        if (!ModifierTable.Properties.TryGetValue(propName, out var info))
            return null;

        // A null / default right-hand side is not expressible through the modifier.
        // ApplyModifiers treats a null modifier value as "no modifier supplied" and only
        // clears the property when the PREVIOUS render had one, so `.Background(null)` does
        // not reliably write null the way `.Set(x => x.Background = null)` does. Suggesting
        // the rewrite here would change behaviour — the precise failure this analyzer exists
        // to prevent. Real site: samples/ReactorGallery/ControlPages/Media/ParallaxViewPage.cs.
        if (IsNullOrDefault(assignment.Right))
            return null;

        // Receiver gates. Both are checked against the semantic model rather than inferred:
        // for `.Set(x => …)` the lambda parameter's type IS the runtime WinUI control type
        // (the overload is Action<WinUI.Grid> and friends), and the `.Set` receiver's type
        // is the concrete Reactor element type.
        //
        // The two gates are OR'd when both are present: they are independent routes to a
        // sound rewrite — the generic modifier reaching this control at runtime, or a
        // type-specific overload existing for this element type. Fonts need both.
        var gated = info.ControlGate is not null || info.ElementTypes is not null;
        if (gated && !PassesControlGate(context, info, leftAccess) && !PassesElementGate(context, info, memberAccess))
            return null;

        return (propName, info);
    }

    /// <summary>
    /// True when <c>ApplyModifiers</c> would actually write the generic modifier to this
    /// runtime control type. False when no control gate is declared — the caller OR-combines
    /// this with <see cref="PassesElementGate"/>, so "not applicable" must not count as a pass.
    /// </summary>
    private static bool PassesControlGate(
        SyntaxNodeAnalysisContext context,
        ModifierInfo info,
        MemberAccessExpressionSyntax leftAccess)
    {
        if (info.ControlGate is not { } gate)
            return false;

        // ApplyModifiers writes this modifier only to certain control types; on anything
        // else it compiles and silently does nothing, so staying on .Set is correct.
        var controlType = context.SemanticModel
            .GetTypeInfo(leftAccess.Expression, context.CancellationToken).Type;

        return gate.Any(allowed =>
            SetLambdaHelpers.InheritsFrom(controlType, allowed, "Microsoft.UI.Xaml.Controls"));
    }

    /// <summary>
    /// True when the receiver element type declares a type-specific overload of the modifier.
    /// False when no element types are declared, for the same reason as
    /// <see cref="PassesControlGate"/>.
    /// </summary>
    private static bool PassesElementGate(
        SyntaxNodeAnalysisContext context,
        ModifierInfo info,
        MemberAccessExpressionSyntax memberAccess)
    {
        if (info.ElementTypes is not { } elementTypes)
            return false;

        // No generic overload exists (or it does not reach this control), so the rewrite
        // only compiles when the receiver element type declares one.
        var elementType = context.SemanticModel
            .GetTypeInfo(memberAccess.Expression, context.CancellationToken).Type;
        if (elementType is null)
            return false;

        return elementTypes.Any(candidate =>
            string.Equals(elementType.Name, candidate, System.StringComparison.Ordinal));
    }

    /// <summary>
    /// True for a right-hand side that assigns null, seeing through the wrappers that do not
    /// change that: parentheses, casts, and the null-forgiving operator.
    /// </summary>
    /// <remarks>
    /// A bare-literal test is not enough. <c>(Brush)null!</c>, <c>(Brush?)null</c> and
    /// <c>((Brush)null)</c> all assign null while none of them is a
    /// <see cref="SyntaxKind.NullLiteralExpression"/> at the top. Letting one through would
    /// suggest <c>.Background((Brush)null!)</c>, and <c>ApplyModifiers</c> skips a null modifier
    /// value — so the explicit null write silently stops happening, which is exactly the
    /// class of silent behaviour change this gate exists to prevent.
    /// </remarks>
    private static bool IsNullOrDefault(ExpressionSyntax expression)
    {
        while (true)
        {
            switch (expression)
            {
                case ParenthesizedExpressionSyntax parenthesized:
                    expression = parenthesized.Expression;
                    continue;
                case CastExpressionSyntax cast:
                    expression = cast.Expression;
                    continue;
                case PostfixUnaryExpressionSyntax suppression
                    when suppression.IsKind(SyntaxKind.SuppressNullableWarningExpression):
                    expression = suppression.Operand;
                    continue;
                default:
                    return expression.IsKind(SyntaxKind.NullLiteralExpression)
                        || expression.IsKind(SyntaxKind.DefaultLiteralExpression)
                        || expression is DefaultExpressionSyntax;
            }
        }
    }
}
