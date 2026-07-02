using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Microsoft.UI.Reactor.Analyzers;

/// <summary>
/// <c>REACTOR_ANIM_003</c> — flags an <c>async</c> lambda (or <c>async delegate</c>)
/// passed to <see cref="M:AnimationScope.WithAnimation"/> /
/// <c>WithAnimationAsync</c>, where mutations that run <b>after</b> an <c>await</c>
/// silently animate nothing.
/// </summary>
/// <remarks>
/// <para>
/// <c>AnimationScope</c> stores the ambient curve in <c>[ThreadStatic]</c> fields
/// (<c>src/Reactor/Animation/AnimationScope.cs</c>). <c>WithAnimation(Curve?, Action)</c>
/// sets the scope, invokes <c>action()</c> <b>synchronously</b>, then restores the
/// previous scope in a <c>finally</c>. Both scope-taking entry points —
/// <c>WithAnimation</c> (AnimationScope.cs:28) and <c>WithAnimationAsync</c>
/// (AnimationScope.cs:63) — take a plain <c>Action</c>; neither has a
/// <c>Func&lt;Task&gt;</c> overload.
/// </para>
/// <para>
/// Because the parameter is <c>Action</c>, an <c>async</c> lambda binds as
/// <c>async void</c>. Calling it returns the moment control hits the first suspended
/// <c>await</c>, so the <c>finally</c> restores (empties) the scope <b>before</b> the
/// continuation runs. Any property mutation after the <c>await</c> therefore executes
/// with no ambient curve and does not animate:
/// <code>
/// AnimationScope.WithAnimation(Curve.Ease(300), async () =>
/// {
///     setStage("loading");
///     await api.SaveAsync();
///     setStage("done");   // scope already restored — animates nothing
/// });
/// </code>
/// </para>
/// <para>
/// There is <b>no clean mechanical rewrite</b>: switching to <c>WithAnimationAsync</c>
/// would not remove the <c>async void</c> (it also takes an <c>Action</c>, not a
/// <c>Func&lt;Task&gt;</c>), so this rule ships <b>no code fix</b>. The diagnostic
/// message instead advises splitting the animated mutations into a separate
/// <c>WithAnimation</c> call per phase, sequenced around each <c>await</c>. See
/// <c>docs/guide/animation.md</c> ("Awaiting inside <c>WithAnimation</c>").
/// </para>
/// <para>
/// Low false-positive gate: the callee must resolve to the Reactor
/// <c>AnimationScope</c>; the lambda must convert to exactly <c>System.Action</c>
/// (proving the <c>async void</c> binding, and excluding any future
/// <c>Func&lt;Task&gt;</c> overload); and the lambda body must contain an
/// <c>await</c> followed by a real mutation statement — both evaluated at the
/// lambda's own async level, so awaits/mutations inside nested closures never trip
/// (or suppress) the rule.
/// </para>
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class AnimationScopeAsyncAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "REACTOR_ANIM_003";

    private const string AnimationScopeTypeName = "AnimationScope";
    private const string AnimationNamespace = "Microsoft.UI.Reactor.Animation";
    private const string WithAnimationName = "WithAnimation";
    private const string WithAnimationAsyncName = "WithAnimationAsync";

    private static readonly LocalizableString Title =
        "Async lambda to WithAnimation loses the animation scope after await";

    private static readonly LocalizableString MessageFormat =
        "This async lambda passed to '{0}' binds as 'async void'; AnimationScope is [ThreadStatic], " +
        "so mutations after the 'await' run with an empty scope and don't animate. " +
        "WithAnimationAsync won't help (it also takes an Action, not a Func<Task>) — split the animated " +
        "mutations into a separate WithAnimation call per phase, sequenced around each await.";

    private static readonly LocalizableString Description =
        "AnimationScope stores the ambient curve in [ThreadStatic] fields and WithAnimation/" +
        "WithAnimationAsync take a plain Action, so an async lambda runs as async void: it returns " +
        "at the first suspended await and the scope is restored before the continuation resumes. " +
        "Property changes after the await execute with no ambient curve and are not animated. There " +
        "is no one-click fix (the async variant also takes an Action); split the mutations into a " +
        "WithAnimation call per phase around each await.";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        Title,
        MessageFormat,
        "Reactor.Animation",
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

        // Cheap syntactic gate: a call named WithAnimation / WithAnimationAsync with at least one
        // async anonymous-function argument, before touching the semantic model.
        var name = GetInvokedSimpleName(invocation.Expression);
        if (name != WithAnimationName && name != WithAnimationAsyncName)
            return;

        var asyncArgs = invocation.ArgumentList.Arguments
            .Select(a => a.Expression)
            .OfType<AnonymousFunctionExpressionSyntax>()
            .Where(f => f.AsyncKeyword.IsKind(SyntaxKind.AsyncKeyword))
            .ToList();
        if (asyncArgs.Count == 0)
            return;

        // Anchor on the real Reactor AnimationScope so a look-alike WithAnimation(…, Action) on some
        // other type (not [ThreadStatic]-scoped) never fires. Require resolution — low FP over recall.
        if (context.SemanticModel.GetSymbolInfo(invocation, context.CancellationToken).Symbol
                is not IMethodSymbol method)
            return;
        if (method.Name != WithAnimationName && method.Name != WithAnimationAsyncName)
            return;
        if (method.ContainingType?.Name != AnimationScopeTypeName)
            return;
        if (method.ContainingType.ContainingNamespace?.ToDisplayString() != AnimationNamespace)
            return;

        foreach (var lambda in asyncArgs)
        {
            // The lambda must bind to exactly System.Action — that is what makes the async lambda
            // an async void. A Func<Task> parameter (generic) would await correctly and is excluded.
            var converted = context.SemanticModel.GetTypeInfo(lambda, context.CancellationToken).ConvertedType;
            if (!IsSystemAction(converted))
                continue;

            if (!HasPostAwaitMutation(lambda.Body))
                continue;

            context.ReportDiagnostic(Diagnostic.Create(
                Rule,
                lambda.AsyncKeyword.GetLocation(),
                method.Name));
        }
    }

    /// <summary>
    /// True when the async lambda body performs a real mutation (a call or assignment) that runs
    /// <b>after</b> an <c>await</c>, evaluated at the lambda's own async level. Awaits and statements
    /// inside nested closures (lambdas / anonymous methods / local functions) are ignored — they run
    /// in their own async context, so they must neither trip nor suppress this lambda's diagnostic.
    /// </summary>
    private static bool HasPostAwaitMutation(CSharpSyntaxNode body)
    {
        var ownLevel = body.DescendantNodes(descendIntoChildren: n => !IsClosureBoundary(n)).ToList();

        var awaits = ownLevel.OfType<AwaitExpressionSyntax>().ToList();
        if (awaits.Count == 0)
            return false; // async-with-no-await is CS1998, not this footgun — never fire.

        var firstAwaitStart = awaits.Min(a => a.SpanStart);

        // A "mutation" is an expression statement that calls something or assigns something and is
        // itself not an await statement (`await X;` / `x = await Y;` are the await, not lost work).
        return ownLevel
            .OfType<ExpressionStatementSyntax>()
            .Any(es =>
                es.SpanStart > firstAwaitStart
                && es.Expression is InvocationExpressionSyntax or AssignmentExpressionSyntax
                && !ContainsOwnLevelAwait(es));
    }

    private static bool ContainsOwnLevelAwait(SyntaxNode node) =>
        node.DescendantNodesAndSelf(descendIntoChildren: n => !IsClosureBoundary(n))
            .OfType<AwaitExpressionSyntax>()
            .Any();

    private static bool IsClosureBoundary(SyntaxNode node) =>
        node is AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax;

    private static bool IsSystemAction(ITypeSymbol? type) =>
        type is INamedTypeSymbol { Name: "Action", IsGenericType: false } named
        && named.ContainingNamespace?.ToDisplayString() == "System";

    private static string? GetInvokedSimpleName(ExpressionSyntax expression) => expression switch
    {
        MemberAccessExpressionSyntax m => m.Name.Identifier.ValueText,
        IdentifierNameSyntax id => id.Identifier.ValueText,
        _ => null,
    };
}
