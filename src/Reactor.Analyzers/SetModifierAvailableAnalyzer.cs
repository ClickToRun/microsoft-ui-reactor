using System.Collections.Generic;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Microsoft.UI.Reactor.Analyzers;

/// <summary>
/// REACTOR_MOD_002: Detects <c>.Set(fe =&gt; fe.PROP = ...)</c> where a first-class Reactor
/// modifier exists for <c>PROP</c>, and suggests the modifier.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately a sibling of <see cref="PoolResetSetAnalyzer"/> (<c>REACTOR_POOL_001</c>)
/// rather than an extension of it. POOL_001's message asserts the value is "reset on pool
/// return", and its property list is documented as "reset in <c>ElementPool.CleanElement</c>
/// or otherwise cleared between renders". The properties here are not pool-reset — the
/// reason to prefer a modifier is different and applies to every setter:
/// </para>
/// <list type="bullet">
/// <item><description><c>Element.SettersEqual</c> is <c>ReferenceEquals(a,b) || both-empty</c>,
/// so any element carrying setters is forced onto the reconciler's Update path every render,
/// losing the structural skip.</description></item>
/// <item><description>A <c>.Set</c> write is never unwound. The modifier path clears the
/// dependency property when the value is dropped from a later render.</description></item>
/// </list>
/// <para>
/// <b>Receiver gating is mandatory for some properties.</b> <c>ApplyModifiers</c> applies
/// several modifiers only to specific runtime control types; on anything else the modifier
/// compiles and silently does nothing. Suggesting an ungated rewrite for those would produce
/// a code fix that breaks the UI with no compiler error — exactly the regression this
/// analyzer exists to prevent. <see cref="GatedProperties"/> encodes the allow-lists, which
/// are per-property: <c>StackPanel</c> accepts <c>Padding</c> but not <c>CornerRadius</c>.
/// </para>
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class SetModifierAvailableAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "REACTOR_MOD_002";

    /// <summary>
    /// Property → modifier name for properties whose modifier is a generic
    /// <c>T Foo&lt;T&gt;(this T el, …) where T : Element</c> that <c>ApplyModifiers</c>
    /// applies without a runtime type check, or whose dependency property exists only on
    /// types already inside the reconciler's allow-list (safe by construction).
    /// <para>
    /// <c>IsEnabled</c> / <c>HorizontalContentAlignment</c> / <c>VerticalContentAlignment</c>
    /// are gated to <c>Control</c> in <c>ApplyModifiers</c>, but WinUI declares those DPs
    /// only on <c>Control</c> — if the <c>.Set</c> lambda compiles, the receiver already
    /// qualifies, so no predicate is needed.
    /// </para>
    /// <para>
    /// <c>RequestedTheme</c> is deliberately absent: <c>RequestedThemeSetAnalyzer</c>
    /// (<c>REACTOR_THEME_003</c>) already owns it and would double-report.
    /// </para>
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> UngatedProperties =
        new Dictionary<string, string>(System.StringComparer.Ordinal)
        {
            { "IsEnabled",                  "IsEnabled" },
            { "IsHitTestVisible",           "IsHitTestVisible" },
            { "HorizontalContentAlignment", "HorizontalContentAlignment" },
            { "VerticalContentAlignment",   "VerticalContentAlignment" },
        };

    /// <summary>
    /// Property → (modifier name, WinUI types the reconciler actually applies it to).
    /// WinUI declares each of these dependency properties on <em>more</em> types than
    /// <c>ApplyModifiers</c> handles, so the receiver must be checked before suggesting the
    /// modifier. Keep in sync with <c>ApplyModifiers</c> in <c>src/Reactor/Core/Reconciler.cs</c>.
    /// <para>
    /// The allow-lists differ per property on purpose — <c>Padding</c> reaches
    /// <c>StackPanel</c> while <c>CornerRadius</c> and the border properties do not, and
    /// <c>Background</c> reaches any <c>Panel</c> (so <c>Grid</c> is fine there but not for
    /// <c>Padding</c>).
    /// </para>
    /// </summary>
    public static readonly IReadOnlyDictionary<string, (string Modifier, string[] AppliesTo)> GatedProperties =
        new Dictionary<string, (string, string[])>(System.StringComparer.Ordinal)
        {
            { "Padding",         ("Padding",         new[] { "Control", "Border", "StackPanel" }) },
            { "CornerRadius",    ("CornerRadius",    new[] { "Control", "Border" }) },
            { "BorderThickness", ("BorderThickness", new[] { "Control", "Border" }) },
            { "BorderBrush",     ("BorderBrush",     new[] { "Control", "Border" }) },
            { "Background",      ("Background",      new[] { "Panel", "Control", "Border" }) },
        };

    private static readonly LocalizableString Title =
        "Use the Reactor modifier instead of .Set";

    private static readonly LocalizableString MessageFormat =
        "A '.{1}(...)' modifier exists for '{0}'. Prefer it over '.Set(...)', which re-runs every render, is never unwound, and keeps the element on the reconciler's update path.";

    private static readonly LocalizableString Description =
        "Reactor exposes a fluent modifier for this property. Modifier values are stored on " +
        "Element.Modifiers, structurally diffed, and cleared when removed, whereas '.Set(...)' " +
        "setters are imperative writes that the reconciler cannot diff — Element.SettersEqual " +
        "only treats setter arrays as equal when they are the same instance or both empty, so " +
        "any element carrying setters re-runs them on every reconcile.";

    internal static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        Title,
        MessageFormat,
        "Reactor.Modifier",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: Description);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeInvocation, SyntaxKind.InvocationExpression);
    }

    private static void AnalyzeInvocation(SyntaxNodeAnalysisContext context)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;

        if (!SetLambdaHelpers.IsSetInvocation(invocation, out _))
            return;

        var lambdaExpr = invocation.ArgumentList.Arguments[0].Expression;

        // Every assignment in the body, not just a lone one — a modifier-backed write is
        // just as wrong when it shares a block with other statements.
        var assignments = SetLambdaHelpers.GetLambdaAssignments(lambdaExpr);
        if (assignments.IsDefaultOrEmpty)
            return;

        var lambdaParam = SetLambdaHelpers.GetSingleLambdaParameter(lambdaExpr);
        if (lambdaParam is null)
            return;

        var isReactorSet = false;
        var reactorSetChecked = false;

        foreach (var assignment in assignments)
        {
            // Simple assignment only. '+=' on an event is REACTOR_EVENT_001's job, and a
            // numeric compound assignment has no modifier equivalent.
            if (!assignment.IsKind(SyntaxKind.SimpleAssignmentExpression))
                continue;

            var leftAccess = SetLambdaHelpers.GetAssignedMemberAccess(assignment, lambdaParam.Identifier.Text);
            if (leftAccess is null)
                continue;

            var propertyName = leftAccess.Name.Identifier.Text;

            // A null / default right-hand side is not expressible through the modifier.
            // ApplyModifiers treats a null modifier value as "no modifier supplied" and only
            // clears the property when the PREVIOUS render had one, so `.Background(null)`
            // does not reliably write null the way `.Set(x => x.Background = null)` does.
            // Suggesting the rewrite here would change behaviour — the precise failure this
            // analyzer exists to prevent.
            if (assignment.Right.IsKind(SyntaxKind.NullLiteralExpression)
                || assignment.Right.IsKind(SyntaxKind.DefaultLiteralExpression)
                || assignment.Right is DefaultExpressionSyntax)
            {
                continue;
            }

            string modifier;
            if (UngatedProperties.TryGetValue(propertyName, out var ungatedModifier))
            {
                modifier = ungatedModifier;
            }
            else if (GatedProperties.TryGetValue(propertyName, out var gated))
            {
                // The lambda parameter's type IS the concrete WinUI control type (the .Set
                // overload is Action<WinUI.Grid> and friends), so this is a direct check
                // rather than an inference. If the reconciler would not apply the modifier
                // to this control, staying on .Set is correct — say nothing.
                var receiverType = context.SemanticModel
                    .GetTypeInfo(leftAccess.Expression, context.CancellationToken).Type;

                var applies = false;
                foreach (var allowed in gated.AppliesTo)
                {
                    if (SetLambdaHelpers.InheritsFrom(receiverType, allowed, "Microsoft.UI.Xaml.Controls"))
                    {
                        applies = true;
                        break;
                    }
                }
                if (!applies)
                    continue;

                modifier = gated.Modifier;
            }
            else
            {
                continue;
            }

            // Guard against an unrelated user-defined '.Set' helper with the same shape.
            // Resolved lazily and once — it is the most expensive check here.
            if (!reactorSetChecked)
            {
                isReactorSet = SetLambdaHelpers.IsReactorSetInvocation(
                    invocation, context.SemanticModel, context.CancellationToken);
                reactorSetChecked = true;
            }
            if (!isReactorSet)
                return;

            context.ReportDiagnostic(Diagnostic.Create(
                Rule,
                invocation.GetLocation(),
                propertyName,
                modifier));
        }
    }
}
