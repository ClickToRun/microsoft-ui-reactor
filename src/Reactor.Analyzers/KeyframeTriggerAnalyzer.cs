using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Microsoft.UI.Reactor.Analyzers;

/// <summary>
/// <c>REACTOR_ANIM_002</c> — the <c>.Keyframes(name, trigger, configure)</c>
/// modifier re-runs its animation whenever the <c>trigger</c> value changes
/// between renders (the reconciler compares <c>!Equals(prevTrigger, trigger)</c>
/// in <c>Reconciler.ApplyKeyframeAnimations</c>). Passing a value that is
/// freshly computed every render — <c>DateTime.Now</c>, <c>Guid.NewGuid()</c>,
/// a per-render allocation — restarts the animation on every reconcile, so the
/// element flickers as the keyframes constantly reset.
/// </summary>
/// <remarks>
/// Info-severity nudge, no code-fix (the correct value is intent-specific — a
/// state counter the author increments only when they mean to retrigger).
/// Purely syntactic: gates on a <c>.Keyframes(name, trigger, configure)</c>
/// invocation shape and classifies the <c>trigger</c> argument. See the terse
/// spec entry (docs/specs/060-analyzer-suite-expansion.md §12) and
/// docs/guide/animation.md "Re-running keyframes on every render".
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class KeyframeTriggerAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "REACTOR_ANIM_002";

    private static readonly LocalizableString Title =
        "Unstable .Keyframes trigger restarts the animation every render";

    private static readonly LocalizableString MessageFormat =
        "The .Keyframes trigger is {0}, which changes on every render and restarts the animation on each reconcile (visible flicker). Pass a value that changes only when you mean to retrigger — e.g. a UseState/UseReducer counter you increment deliberately.";

    private static readonly LocalizableString Description =
        "The .Keyframes(name, trigger, ...) modifier replays its animation whenever the trigger " +
        "value differs from the previous render (the reconciler compares with !Equals). A value " +
        "recomputed every render — DateTime.Now, Guid.NewGuid(), a freshly-allocated object/array/" +
        "collection — is never equal to the prior one, so the animation restarts on every reconcile " +
        "and the element flickers. Use a stable trigger (a counter you increment only when you mean " +
        "to retrigger).";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        Title,
        MessageFormat,
        "Reactor.Animation",
        DiagnosticSeverity.Info,
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

    // <snippet:keyframe-trigger-rule>
    private static void AnalyzeInvocation(SyntaxNodeAnalysisContext context)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;

        // Syntactic gate: `<receiver>.Keyframes(name, trigger, configure)`.
        // The extension is always called with instance syntax, so the three
        // declared parameters (name, trigger, configure) map to three
        // arguments — the receiver is the member-access target, not an argument.
        if (invocation.Expression is not MemberAccessExpressionSyntax member)
            return;
        if (member.Name.Identifier.ValueText != "Keyframes")
            return;

        var args = invocation.ArgumentList.Arguments;
        if (args.Count != 3)
            return;

        var triggerExpr = ResolveTriggerArgument(args);
        if (triggerExpr is null)
            return;

        var kind = ClassifyUnstableTrigger(triggerExpr);
        if (kind is null)
            return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, triggerExpr.GetLocation(), kind));
    }
    // </snippet:keyframe-trigger-rule>

    /// <summary>
    /// Resolves the <c>trigger</c> argument. A named <c>trigger:</c> argument
    /// wins (so reordered named args are handled correctly); otherwise, for an
    /// all-positional call, it is index 1 (<c>name</c>, <c>trigger</c>,
    /// <c>configure</c>). If some arguments are named but none is
    /// <c>trigger</c>, positional order is unreliable, so bail.
    /// </summary>
    private static ExpressionSyntax? ResolveTriggerArgument(SeparatedSyntaxList<ArgumentSyntax> args)
    {
        var anyNamed = false;
        foreach (var arg in args)
        {
            if (arg.NameColon is not { } nc)
                continue;
            anyNamed = true;
            if (nc.Name.Identifier.ValueText == "trigger")
                return arg.Expression;
        }

        return anyNamed ? null : args[1].Expression;
    }

    /// <summary>
    /// Restricted, syntactic "is this recomputed every render?" classifier.
    /// Returns a human-readable kind when the expression is a per-render
    /// allocation or a well-known time/id source; <c>null</c> otherwise.
    /// </summary>
    /// <remarks>
    /// NOTE (consolidation): mirrors the restricted subset of
    /// <c>HookRulesAnalyzer.ClassifyDepExpression</c>. Wave C (spec §3.2) extracts
    /// a shared <c>AllocationAnalysis</c> classifier; when that lands on this
    /// branch, replace the allocation arm here with the shared helper. Tuples and
    /// lambdas are intentionally excluded — a tuple has value equality (stable
    /// when its members are), and a bare lambda cannot bind to the
    /// <c>object? trigger</c> parameter.
    /// </remarks>
    private static string? ClassifyUnstableTrigger(ExpressionSyntax expr)
    {
        expr = UnwrapCasts(expr);

        switch (expr)
        {
            case ObjectCreationExpressionSyntax:
            case ImplicitObjectCreationExpressionSyntax:
                return "a freshly-allocated object";
            case ArrayCreationExpressionSyntax:
            case ImplicitArrayCreationExpressionSyntax:
                return "a freshly-allocated array";
            case CollectionExpressionSyntax:
                return "a freshly-allocated collection";
            case AnonymousObjectCreationExpressionSyntax:
                return "a freshly-allocated anonymous object";
        }

        // Well-known per-render-varying time / identity sources.
        // `X.Now` / `X.UtcNow` / `X.TickCount(64)` member reads.
        if (expr is MemberAccessExpressionSyntax ma)
        {
            var receiver = RightmostName(ma.Expression);
            var name = ma.Name.Identifier.ValueText;
            switch (receiver, name)
            {
                case ("DateTime", "Now"):
                case ("DateTimeOffset", "Now"):
                    return $"{receiver}.Now";
                case ("DateTime", "UtcNow"):
                case ("DateTimeOffset", "UtcNow"):
                    return $"{receiver}.UtcNow";
                case ("Environment", "TickCount"):
                    return "Environment.TickCount";
                case ("Environment", "TickCount64"):
                    return "Environment.TickCount64";
            }
        }

        // `Guid.NewGuid()` invocation.
        if (expr is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax call }
            && RightmostName(call.Expression) == "Guid"
            && call.Name.Identifier.ValueText == "NewGuid")
        {
            return "Guid.NewGuid()";
        }

        return null;
    }

    /// <summary>
    /// Returns the rightmost identifier of a (possibly qualified) receiver:
    /// <c>DateTime</c> for both <c>DateTime</c> and <c>System.DateTime</c>.
    /// </summary>
    private static string? RightmostName(ExpressionSyntax expr) => expr switch
    {
        IdentifierNameSyntax id => id.Identifier.ValueText,
        MemberAccessExpressionSyntax ma => ma.Name.Identifier.ValueText,
        _ => null,
    };

    private static ExpressionSyntax UnwrapCasts(ExpressionSyntax expr)
    {
        while (true)
        {
            switch (expr)
            {
                case CastExpressionSyntax cast: expr = cast.Expression; continue;
                case ParenthesizedExpressionSyntax paren: expr = paren.Expression; continue;
                default: return expr;
            }
        }
    }
}
