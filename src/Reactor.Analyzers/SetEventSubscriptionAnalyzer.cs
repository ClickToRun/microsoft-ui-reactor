using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Microsoft.UI.Reactor.Analyzers;

/// <summary>
/// REACTOR_LIFECYCLE_001: Detects an event subscription performed through
/// <c>.Set(c =&gt; c.Event += handler)</c>. Because <c>.Set(...)</c> setters re-run on
/// every reconcile, each render adds another subscription — old closures are never
/// removed, so the handler fires once per past render and leaks. The subscription belongs
/// in <c>.OnMount(...)</c> with teardown in <c>.OnUnmount(...)</c>.
/// </summary>
/// <remarks>
/// The event-symbol check is mandatory: <c>.Set(c =&gt; c.Opacity += 0.1)</c> (numeric
/// compound assignment) and <c>+=</c> against a non-event delegate field must not fire.
/// Restricted to receivers deriving from <c>FrameworkElement</c>. See spec 060 §4.6.
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class SetEventSubscriptionAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "REACTOR_LIFECYCLE_001";

    private static readonly LocalizableString Title =
        "Wire events through .OnMount/.OnUnmount, not .Set";

    private static readonly LocalizableString MessageFormat =
        "Event '{0}' is wired imperatively through '.Set(...)', which re-runs on every render. Subscribe in '.OnMount(...)' and unsubscribe in '.OnUnmount(...)' instead.";

    private static readonly LocalizableString Description =
        "'.Set(...)' setters are re-applied on every reconcile, so wiring an event there is " +
        "wrong in both directions: a '+=' subscription adds a new handler each render (the " +
        "handler multiplies its invocations and old closures leak), and a '-=' repeatedly " +
        "runs teardown. Use '.OnMount(c => control.Event += h)' for the one-time subscription " +
        "and '.OnUnmount(c => control.Event -= h)' for teardown.";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        Title,
        MessageFormat,
        "Reactor.Lifecycle",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: Description);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeInvocation, SyntaxKind.InvocationExpression);
    }

    private static void AnalyzeInvocation(SyntaxNodeAnalysisContext context)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;

        // Syntactic fast path: receiver.Set(x => x.Event += handler) / -= handler.
        if (!SetLambdaHelpers.IsSetInvocation(invocation, out _))
            return;

        var lambdaExpr = invocation.ArgumentList.Arguments[0].Expression;
        var assignment = SetLambdaHelpers.TryGetLambdaAssignment(lambdaExpr);
        if (assignment is null)
            return;
        var kind = assignment.Kind();
        if (kind != SyntaxKind.AddAssignmentExpression && kind != SyntaxKind.SubtractAssignmentExpression)
            return;

        var lambdaParam = SetLambdaHelpers.GetSingleLambdaParameter(lambdaExpr);
        var leftAccess = SetLambdaHelpers.GetAssignedMemberAccess(assignment, lambdaParam?.Identifier.Text);
        if (leftAccess is null)
            return;

        // MANDATORY: the assigned member must be an event symbol. Without this the rule
        // false-fires on numeric compound assignment (c.Opacity += 0.1) and on '+=' to a
        // non-event delegate field / ObservableCollection.CollectionChanged.
        if (context.SemanticModel.GetSymbolInfo(leftAccess, context.CancellationToken).Symbol is not IEventSymbol)
            return;

        // Restrict to receivers deriving from FrameworkElement (the native control).
        var receiverType = context.SemanticModel
            .GetTypeInfo(leftAccess.Expression, context.CancellationToken).Type;
        if (!SetLambdaHelpers.InheritsFrom(receiverType, "FrameworkElement", "Microsoft.UI.Xaml"))
            return;

        context.ReportDiagnostic(Diagnostic.Create(
            Rule,
            invocation.GetLocation(),
            leftAccess.Name.Identifier.Text));
    }
}
