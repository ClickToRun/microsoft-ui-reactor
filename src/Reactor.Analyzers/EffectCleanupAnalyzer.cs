using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Microsoft.UI.Reactor.Analyzers;

/// <summary>
/// <c>REACTOR_LIFECYCLE_002</c> — flags a <c>UseEffect(Action, …)</c> whose body allocates a
/// long-lived producer (a timer, an <c>IObservable</c> subscription, or a CLR event
/// subscription) but returns <b>no cleanup</b>.
/// </summary>
/// <remarks>
/// <para>
/// <c>UseEffect</c> has two families of overloads: the <c>Action</c> overloads
/// (<c>RenderContext.cs:363</c> and the arity-1..3 forms) run a fire-and-forget side effect and
/// have <b>no way to return a teardown</b>, while the <c>Func&lt;Action&gt;</c> overloads
/// (<c>RenderContext.cs:379</c>) return a cleanup that the reconciler runs before the next effect
/// and on unmount. When an author creates a <c>PeriodicTimer</c> / <c>Timer</c>, subscribes to an
/// <c>IObservable</c>, or wires a CLR event inside the <c>Action</c> overload, the producer
/// outlives the component: after unmount it keeps firing, its handler calls a state setter on a
/// dead <see cref="!:RenderContext"/> (which throws, or silently leaks the captured closure tree).
/// This is the "Missing cleanup" pitfall documented in <c>docs/guide/effects.md</c> §"Missing
/// cleanup" (lines 340-376).
/// </para>
/// <para>
/// Detection is deliberately conservative (nudge, not a mechanical fix — the correct teardown
/// differs per resource and the created handle is often captured into a nested task):
/// the invocation must bind to the Reactor <c>Component</c>/<c>RenderContext</c> <c>UseEffect</c>
/// whose first parameter is the non-generic <see cref="!:System.Action"/> overload; the effect
/// argument must be a lambda whose body is visible; a known-lifetime allocation must appear at the
/// <b>top level</b> of that body (not inside a nested lambda / local function, whose lifetime is
/// its own); and there must be <b>no</b> teardown signal anywhere in the body (<c>using</c>,
/// <c>Dispose</c>/<c>DisposeAsync</c>, or a matching event <c>-=</c>). Any of those bails the rule.
/// The fix is a template nudge: return a cleanup <c>Action</c> (which selects the
/// <c>Func&lt;Action&gt;</c> overload).
/// </para>
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class EffectCleanupAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "REACTOR_LIFECYCLE_002";

    private const string UseEffectName = "UseEffect";
    private const string ComponentType = "Microsoft.UI.Reactor.Core.Component";
    private const string RenderContextType = "Microsoft.UI.Reactor.Core.RenderContext";

    /// <summary>
    /// Simple type names whose construction inside an effect body denotes a producer that keeps
    /// running until explicitly stopped. Kept as simple names so both <c>System.Threading.Timer</c>
    /// and <c>System.Timers.Timer</c> (and the WinUI dispatcher timers) match without binding.
    /// </summary>
    private static readonly HashSet<string> KnownTimerTypes = new(System.StringComparer.Ordinal)
    {
        "PeriodicTimer",
        "Timer",
        "DispatcherTimer",
        "DispatcherQueueTimer",
        "ThreadPoolTimer",
    };

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        "UseEffect allocates a long-lived resource with no cleanup",
        "This UseEffect creates {0} but returns no cleanup; after unmount it keeps firing and the state setter runs on a dead RenderContext. Return a cleanup Action (use the Func<Action> overload) that stops/disposes it.",
        "Reactor.Lifecycle",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description:
            "The Action overload of UseEffect cannot return a teardown, so a timer, IObservable " +
            "subscription, or CLR event wired inside it outlives the component. After unmount the " +
            "producer keeps firing and its handler calls a state setter on a dead RenderContext, " +
            "which throws or silently leaks the captured closure. Switch to the Func<Action> " +
            "overload and return a cleanup that cancels/disposes the resource — e.g. " +
            "UseEffect(() => { var t = new PeriodicTimer(...); ...; return () => t.Dispose(); }, ...). " +
            "See docs/guide/effects.md \"Missing cleanup\".");

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

        // Syntactic fast path: bail before any semantic query unless the call names UseEffect.
        if (GetInvokedMethodName(invocation) != UseEffectName)
            return;

        var args = invocation.ArgumentList.Arguments;
        if (args.Count == 0)
            return;

        // The effect must be a lambda/anonymous method whose body we can inspect. A method group
        // (UseEffect(SetUp, ...)) hides the body — unprovable, so bail.
        if (args[0].Expression is not AnonymousFunctionExpressionSyntax effect)
            return;
        var body = (SyntaxNode?)effect.Body;
        if (body is null)
            return;

        // Anchor to the Reactor UseEffect AND select the no-cleanup (Action) overload.
        if (context.SemanticModel.GetSymbolInfo(invocation, context.CancellationToken).Symbol is not IMethodSymbol method)
            return;
        if (!IsReactorUseEffect(method) || !IsActionOverload(method))
            return;

        // Conservative bail: any in-body teardown signal (anywhere, including nested continuations)
        // means the author is managing the lifetime — favor a false negative over a false positive.
        if (HasCleanupSignal(body, context.SemanticModel, context.CancellationToken))
            return;

        // Find the offending producer at the top level of the effect body (do not descend into
        // nested lambdas / local functions — their lifetime is their own).
        var (offender, resourceKind) = FindLifetimeAllocation(body, context.SemanticModel, context.CancellationToken);
        if (offender is null)
            return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, offender.GetLocation(), resourceKind));
    }

    private static string? GetInvokedMethodName(InvocationExpressionSyntax invocation)
        => invocation.Expression switch
        {
            MemberAccessExpressionSyntax ma => ma.Name.Identifier.Text,
            IdentifierNameSyntax id => id.Identifier.Text,
            GenericNameSyntax gn => gn.Identifier.Text,
            _ => null,
        };

    /// <summary>
    /// True when the resolved <c>UseEffect</c> lives on the Reactor <c>Component</c> (the protected
    /// wrappers) or <c>RenderContext</c> (the instance methods). Mirrors
    /// <c>HookRulesAnalyzer.IsLikelyReactorHook</c>'s Component-or-RenderContext anchoring.
    /// </summary>
    private static bool IsReactorUseEffect(IMethodSymbol method)
        => IsOrDerivesFrom(method.ContainingType, ComponentType)
        || IsOrDerivesFrom(method.ContainingType, RenderContextType);

    /// <summary>
    /// True when the first parameter is the non-generic <see cref="!:System.Action"/> — i.e. the
    /// overload that cannot return a cleanup. The <c>Func&lt;Action&gt;</c> overloads carry a
    /// teardown contract and are intentionally excluded.
    /// </summary>
    private static bool IsActionOverload(IMethodSymbol method)
    {
        if (method.Parameters.Length == 0)
            return false;
        return method.Parameters[0].Type is INamedTypeSymbol { Name: "Action", Arity: 0 } t
            && t.ContainingNamespace?.ToDisplayString() == "System";
    }

    private static bool IsOrDerivesFrom(INamedTypeSymbol? type, string fullyQualifiedName)
    {
        for (var t = type; t is not null; t = t.BaseType)
        {
            var name = t.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat).Replace("global::", "");
            if (name == fullyQualifiedName || name.StartsWith(fullyQualifiedName + "<", System.StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Scans the whole effect body (including nested lambdas / continuations) for any teardown
    /// signal: a <c>using</c> statement/declaration, a <c>.Dispose(</c>/<c>.DisposeAsync(</c> call,
    /// or an event unsubscription (<c>-=</c> whose left side binds to an event). Presence means the
    /// author is managing the lifetime. A numeric <c>-=</c> (e.g. <c>count -= 1</c>) is not counted.
    /// </summary>
    private static bool HasCleanupSignal(SyntaxNode body, SemanticModel model, System.Threading.CancellationToken ct)
    {
        foreach (var node in body.DescendantNodesAndSelf())
        {
            switch (node)
            {
                case UsingStatementSyntax:
                case LocalDeclarationStatementSyntax { UsingKeyword.RawKind: not 0 }:
                    return true;
                case AssignmentExpressionSyntax a
                    when a.IsKind(SyntaxKind.SubtractAssignmentExpression) && IsEvent(a.Left, model, ct):
                    return true;
                case InvocationExpressionSyntax inv
                    when inv.Expression is MemberAccessExpressionSyntax ma
                    && ma.Name.Identifier.Text is "Dispose" or "DisposeAsync":
                    return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Returns the first known-lifetime allocation at the top level of the effect body (skipping
    /// subtrees rooted at a nested anonymous function or local function), plus a human-readable
    /// description of what it is. Returns <c>(null, "")</c> when nothing qualifies.
    /// </summary>
    private static (SyntaxNode? node, string kind) FindLifetimeAllocation(
        SyntaxNode body, SemanticModel model, System.Threading.CancellationToken ct)
    {
        foreach (var node in EnumerateExcludingNestedFunctions(body))
        {
            switch (node)
            {
                case ObjectCreationExpressionSyntax oc when IsKnownTimer(oc.Type):
                    return (oc, $"a {SimpleTypeName(oc.Type)}");

                case InvocationExpressionSyntax inv
                    when inv.Expression is MemberAccessExpressionSyntax { Name.Identifier.Text: "Subscribe" }
                    && ReturnsDisposable(inv, model, ct):
                    return (inv, "an IObservable subscription");

                case AssignmentExpressionSyntax add
                    when add.IsKind(SyntaxKind.AddAssignmentExpression)
                    && IsEvent(add.Left, model, ct):
                    return (add, "an event subscription");
            }
        }
        return (null, string.Empty);
    }

    /// <summary>
    /// Pre-order walk (document order) of <paramref name="node"/> and its descendants that does not
    /// enter the body of a nested lambda, anonymous method, or local function — allocations there
    /// have their own lifetime and are not the effect's setup work. The node itself is yielded so an
    /// expression-bodied effect (<c>() =&gt; source.Subscribe(...)</c>) is still inspected.
    /// </summary>
    private static IEnumerable<SyntaxNode> EnumerateExcludingNestedFunctions(SyntaxNode node)
    {
        yield return node;
        foreach (var child in node.ChildNodes())
        {
            if (child is AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax)
                continue;
            foreach (var descendant in EnumerateExcludingNestedFunctions(child))
                yield return descendant;
        }
    }

    private static bool IsKnownTimer(TypeSyntax type)
        => KnownTimerTypes.Contains(SimpleTypeName(type));

    private static string SimpleTypeName(TypeSyntax type)
        => type switch
        {
            QualifiedNameSyntax q => q.Right.Identifier.Text,
            GenericNameSyntax g => g.Identifier.Text,
            IdentifierNameSyntax id => id.Identifier.Text,
            _ => type.ToString(),
        };

    private static bool ReturnsDisposable(InvocationExpressionSyntax invocation, SemanticModel model, System.Threading.CancellationToken ct)
    {
        if (model.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol m)
            return false;
        var ret = m.ReturnType;
        if (ret is null || ret.SpecialType == SpecialType.System_Void)
            return false;
        if (ret.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) == "global::System.IDisposable")
            return true;
        return ret.AllInterfaces.Any(i =>
            i.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) == "global::System.IDisposable");
    }

    private static bool IsEvent(ExpressionSyntax left, SemanticModel model, System.Threading.CancellationToken ct)
        => model.GetSymbolInfo(left, ct).Symbol is IEventSymbol;
}
