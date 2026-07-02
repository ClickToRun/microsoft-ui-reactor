using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Microsoft.UI.Reactor.Analyzers;

/// <summary>
/// REACTOR_THREAD_001: Detects an invocation of a UI-thread-only Reactor member
/// (marked <c>[UIThreadOnly]</c>) that runs lexically inside a background-launch
/// lambda (<c>Task.Run</c> / <c>Task.Factory.StartNew</c> /
/// <c>ThreadPool.QueueUserWorkItem</c>) without being marshaled back through a
/// <c>DispatcherQueue.TryEnqueue</c>. Such calls hit
/// <c>ThreadAffinity.ThrowIfNotOnUIThread</c> and throw at runtime.
/// </summary>
/// <remarks>
/// The framework is a metadata-only reference in a consumer compilation, so the
/// analyzer cannot inspect a callee's body for the runtime guard. The committed
/// mechanism is the <c>[UIThreadOnly]</c> marker attribute
/// (<see cref="M:Microsoft.UI.Reactor.Hosting.ThreadAffinity"/> annotations),
/// which is metadata-visible. The syntactic background-lambda gate runs first;
/// the attribute check is the semantic backstop that keeps false positives low.
/// (spec 060 §4.6)
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class UIThreadAffinityAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "REACTOR_THREAD_001";

    internal const string UIThreadOnlyAttributeMetadataName =
        "Microsoft.UI.Reactor.Hosting.UIThreadOnlyAttribute";

    private static readonly LocalizableString Title =
        "UI-thread-only member called on a background thread";

    private static readonly LocalizableString MessageFormat =
        "'{0}' must run on the UI thread; calling it inside a background task throws at runtime. " +
        "Marshal through ReactorApp.UIDispatcher.TryEnqueue(...).";

    private static readonly LocalizableString Description =
        "Members annotated with [UIThreadOnly] call ThreadAffinity.ThrowIfNotOnUIThread and throw " +
        "InvalidOperationException when reached from a Task.Run / Task.Factory.StartNew / " +
        "ThreadPool.QueueUserWorkItem lambda. Marshal the call back onto the UI thread with " +
        "ReactorApp.UIDispatcher.TryEnqueue(...) — null-safe, because the dispatcher is null until " +
        "the first window bootstraps.";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        Title,
        MessageFormat,
        "Reactor.Threading",
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

        // Syntactic gate first (spec §3): only proceed when the call is lexically
        // inside a background-launch lambda and not already marshaled via TryEnqueue.
        if (!IsInsideUnmarshaledBackgroundLambda(invocation))
            return;

        // Don't re-flag the null-dispatcher fallback the code fix itself emits
        // (`if (d is null) window.Close();` paired with an `else d.TryEnqueue(...)`).
        // When the dispatcher is null the runtime guard is a no-op, so the direct
        // call is the correct pre-bootstrap fallback — and this also stops the
        // code fix from looping on its own output.
        if (IsInsideDispatcherNullFallback(invocation))
            return;

        // Semantic backstop: confirm the callee carries [UIThreadOnly].
        var symbolInfo = context.SemanticModel.GetSymbolInfo(invocation, context.CancellationToken);
        var method = symbolInfo.Symbol as IMethodSymbol
            ?? symbolInfo.CandidateSymbols.FirstOrDefault() as IMethodSymbol;
        if (method is null)
            return;
        if (!HasUIThreadOnlyAttribute(method))
            return;

        context.ReportDiagnostic(Diagnostic.Create(
            Rule,
            invocation.GetLocation(),
            method.Name));
    }

    /// <summary>
    /// Walk the lexical ancestors of <paramref name="invocation"/>. Returns
    /// <see langword="true"/> when the nearest enclosing thread-affecting lambda
    /// is a background launcher (<c>Task.Run</c> / <c>Task.Factory.StartNew</c> /
    /// <c>ThreadPool.QueueUserWorkItem</c>); returns <see langword="false"/> the
    /// moment a <c>TryEnqueue</c> lambda is seen first (already marshaled) or no
    /// background lambda encloses the call. Walking inner→outer makes nesting
    /// resolve correctly: <c>Task.Run(() =&gt; d.TryEnqueue(() =&gt; w.Close()))</c>
    /// hits the TryEnqueue boundary before the Task.Run boundary.
    /// </summary>
    private static bool IsInsideUnmarshaledBackgroundLambda(SyntaxNode invocation)
    {
        for (var node = invocation.Parent; node is not null; node = node.Parent)
        {
            switch (node)
            {
                case AnonymousFunctionExpressionSyntax lambda:
                    switch (ClassifyLambdaHost(lambda))
                    {
                        case LambdaHost.Marshaled:
                            return false;
                        case LambdaHost.Background:
                            return true;
                        // LambdaHost.Unrelated → transparent; keep walking outward.
                    }
                    break;

                // Don't leak out of the enclosing member into sibling code.
                case MemberDeclarationSyntax:
                    return false;
            }
        }

        return false;
    }

    private enum LambdaHost { Unrelated, Background, Marshaled }

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="invocation"/> sits in
    /// the then-branch of an <c>if (x is null)</c> / <c>if (x == null)</c> whose
    /// <c>else</c> marshals through <c>TryEnqueue</c> — the null-dispatcher
    /// fallback idiom the code fix emits (and app authors write by hand). The
    /// fallback is safe because <c>ThrowIfNotOnUIThread</c> is a no-op while the
    /// dispatcher is null.
    /// </summary>
    private static bool IsInsideDispatcherNullFallback(SyntaxNode invocation)
    {
        var child = invocation;
        for (var node = invocation.Parent; node is not null; child = node, node = node.Parent)
        {
            if (node is IfStatementSyntax ifStatement &&
                ReferenceEquals(child, ifStatement.Statement) &&
                IsNullCheck(ifStatement.Condition) &&
                ifStatement.Else is { } elseClause &&
                ContainsTryEnqueue(elseClause))
            {
                return true;
            }

            if (node is MemberDeclarationSyntax)
                break;
        }

        return false;
    }

    private static bool IsNullCheck(ExpressionSyntax condition) => condition switch
    {
        IsPatternExpressionSyntax { Pattern: ConstantPatternSyntax { Expression: LiteralExpressionSyntax literal } }
            => literal.IsKind(SyntaxKind.NullLiteralExpression),
        BinaryExpressionSyntax binary when binary.IsKind(SyntaxKind.EqualsExpression)
            => binary.Left.IsKind(SyntaxKind.NullLiteralExpression)
               || binary.Right.IsKind(SyntaxKind.NullLiteralExpression),
        _ => false,
    };

    private static bool ContainsTryEnqueue(SyntaxNode node) =>
        node.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Any(invocation => GetInvokedNames(invocation).methodName == "TryEnqueue");

    /// <summary>
    /// Classify a lambda by the method it is passed to: a background launcher, a
    /// dispatcher marshal (<c>TryEnqueue</c>), or unrelated.
    /// </summary>
    private static LambdaHost ClassifyLambdaHost(AnonymousFunctionExpressionSyntax lambda)
    {
        if (lambda.Parent is not ArgumentSyntax argument ||
            argument.Parent is not ArgumentListSyntax argumentList ||
            argumentList.Parent is not InvocationExpressionSyntax hostInvocation)
        {
            return LambdaHost.Unrelated;
        }

        var (methodName, receiverName) = GetInvokedNames(hostInvocation);

        // DispatcherQueue.TryEnqueue(...) — the call is already marshaled onto the
        // UI thread regardless of receiver (d / ReactorApp.UIDispatcher / etc.).
        if (methodName == "TryEnqueue")
            return LambdaHost.Marshaled;

        // Background launchers. The receiver check keeps a stray user-defined
        // Run/StartNew/QueueUserWorkItem from tripping the gate; the [UIThreadOnly]
        // attribute is the real confirmation downstream.
        return (methodName, receiverName) switch
        {
            ("Run", "Task") => LambdaHost.Background,
            ("StartNew", "Factory") => LambdaHost.Background,
            ("QueueUserWorkItem", "ThreadPool") => LambdaHost.Background,
            _ => LambdaHost.Unrelated,
        };
    }

    /// <summary>
    /// Extract the invoked simple method name and the rightmost identifier of its
    /// receiver — e.g. <c>Task.Factory.StartNew</c> → (<c>StartNew</c>,
    /// <c>Factory</c>), <c>Task.Run</c> → (<c>Run</c>, <c>Task</c>).
    /// </summary>
    private static (string? methodName, string? receiverName) GetInvokedNames(InvocationExpressionSyntax invocation)
    {
        switch (invocation.Expression)
        {
            case MemberAccessExpressionSyntax memberAccess:
                var receiverName = memberAccess.Expression switch
                {
                    IdentifierNameSyntax id => id.Identifier.Text,
                    MemberAccessExpressionSyntax inner => inner.Name.Identifier.Text,
                    _ => null,
                };
                return (memberAccess.Name.Identifier.Text, receiverName);

            case IdentifierNameSyntax id:
                return (id.Identifier.Text, null);

            case MemberBindingExpressionSyntax binding:
                return (binding.Name.Identifier.Text, null);

            default:
                return (null, null);
        }
    }

    internal static bool HasUIThreadOnlyAttribute(IMethodSymbol method)
    {
        foreach (var attribute in method.GetAttributes())
        {
            var attributeClass = attribute.AttributeClass;
            if (attributeClass is null)
                continue;
            if (attributeClass.Name == "UIThreadOnlyAttribute" &&
                attributeClass.ContainingNamespace?.ToDisplayString() == "Microsoft.UI.Reactor.Hosting")
            {
                return true;
            }
        }

        return false;
    }
}
