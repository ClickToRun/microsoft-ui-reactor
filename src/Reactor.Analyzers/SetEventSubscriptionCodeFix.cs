using System.Collections.Immutable;
using System.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Microsoft.UI.Reactor.Analyzers;

/// <summary>
/// Code fix for REACTOR_LIFECYCLE_001: rewrites
/// <c>x.Set(c =&gt; c.Event += h)</c> into
/// <c>x.OnMountAdd(c =&gt; ((TControl)c).Event += h).OnUnmountAdd(c =&gt; ((TControl)c).Event -= h)</c>.
/// </summary>
/// <remarks>
/// <para><c>.OnMountAdd</c>/<c>.OnUnmountAdd</c> receive an <c>Action&lt;FrameworkElement&gt;</c>,
/// so the lambda casts to the concrete control type. That type is read from the original
/// <c>.Set</c> lambda parameter (each <c>.Set</c> overload is concrete-typed) and emitted
/// via <c>ToMinimalDisplayString</c> so it resolves at the call site.</para>
/// <para>The composing <c>Add</c> variants are used (not plain <c>.OnMount</c>/<c>.OnUnmount</c>,
/// which overwrite via <c>ElementModifiers.Merge</c>) so the rewrite preserves any existing
/// mount/unmount action and lets a fix-all over several <c>.Set</c> subscriptions on one
/// element stack rather than clobbering each other.</para>
/// <para>The fix is only offered when the handler <c>h</c> is a stable delegate — a
/// <c>static</c> method group or a field/property — because <c>.OnMount</c> runs once at
/// mount and <c>.OnUnmount</c> once at unmount; a per-render captured lambda/local would
/// make <c>-=</c> remove a different delegate and leak. Otherwise the diagnostic stands
/// with no fix. Only the subscribe (<c>+=</c>) shape is rewritten.</para>
/// </remarks>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(SetEventSubscriptionCodeFix))]
[Shared]
public sealed class SetEventSubscriptionCodeFix : CodeFixProvider
{
    public override ImmutableArray<string> FixableDiagnosticIds =>
        ImmutableArray.Create(SetEventSubscriptionAnalyzer.DiagnosticId);

    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        if (root is null)
            return;
        var model = await context.Document.GetSemanticModelAsync(context.CancellationToken).ConfigureAwait(false);
        if (model is null)
            return;

        foreach (var diagnostic in context.Diagnostics)
        {
            var node = root.FindNode(diagnostic.Location.SourceSpan);
            if (node is not InvocationExpressionSyntax invocation)
                continue;
            if (!SetLambdaHelpers.IsSetInvocation(invocation, out var memberAccess))
                continue;

            var lambdaExpr = invocation.ArgumentList.Arguments[0].Expression;
            var assignment = SetLambdaHelpers.TryGetLambdaAssignment(lambdaExpr);
            if (assignment is null || !assignment.IsKind(SyntaxKind.AddAssignmentExpression))
                continue; // Only the subscribe (+=) case has a mechanical OnMount/OnUnmount rewrite.

            var lambdaParam = SetLambdaHelpers.GetSingleLambdaParameter(lambdaExpr);
            if (lambdaParam is null)
                continue;
            var leftAccess = SetLambdaHelpers.GetAssignedMemberAccess(assignment, lambdaParam.Identifier.Text);
            if (leftAccess is null)
                continue;

            var handler = assignment.Right;
            if (!IsStableHandler(handler, model, context.CancellationToken))
                continue; // Unstable handler: nudge only.

            var controlType = model.GetDeclaredSymbol(lambdaParam, context.CancellationToken)?.Type;
            if (controlType is null)
                continue;

            var paramName = lambdaParam.Identifier.Text;
            var controlName = controlType.ToMinimalDisplayString(model, invocation.SpanStart);
            var eventName = leftAccess.Name.Identifier.Text;
            var receiverText = memberAccess.Expression.ToString();
            var handlerText = handler.ToString();

            var replacementText =
                $"{receiverText}.OnMountAdd({paramName} => (({controlName}){paramName}).{eventName} += {handlerText})" +
                $".OnUnmountAdd({paramName} => (({controlName}){paramName}).{eventName} -= {handlerText})";

            context.RegisterCodeFix(
                CodeAction.Create(
                    "Move event subscription to .OnMountAdd/.OnUnmountAdd",
                    ct =>
                    {
                        var replacement = SyntaxFactory.ParseExpression(replacementText)
                            .WithTriviaFrom(invocation);
                        var newRoot = root.ReplaceNode(invocation, replacement);
                        return Task.FromResult(context.Document.WithSyntaxRoot(newRoot));
                    },
                    equivalenceKey: SetEventSubscriptionAnalyzer.DiagnosticId),
                diagnostic);
        }
    }

    /// <summary>
    /// A handler is stable across renders — safe to <c>+=</c> at mount and <c>-=</c> at
    /// unmount — when it is a <c>static</c> (ordinary) method group or a field/property
    /// reference. Lambdas, anonymous methods, and locals are unstable.
    /// </summary>
    private static bool IsStableHandler(ExpressionSyntax handler, SemanticModel model, CancellationToken ct)
    {
        if (handler is AnonymousFunctionExpressionSyntax)
            return false;

        var info = model.GetSymbolInfo(handler, ct);
        var symbol = info.Symbol ?? info.CandidateSymbols.FirstOrDefault();

        return symbol switch
        {
            IMethodSymbol method => method.IsStatic && method.MethodKind == MethodKind.Ordinary,
            IFieldSymbol => true,
            IPropertySymbol => true,
            _ => false,
        };
    }
}
