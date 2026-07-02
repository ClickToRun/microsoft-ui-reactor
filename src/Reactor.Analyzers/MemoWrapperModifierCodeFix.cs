using System.Collections.Immutable;
using System.Composition;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Microsoft.UI.Reactor.Analyzers;

/// <summary>
/// Code fix for <see cref="MemoWrapperModifierAnalyzer"/> (<c>REACTOR_MEMO_001</c>) — moves the
/// fluent modifier chain off the keyed <c>Memo(key, factory)</c> wrapper and onto the element the
/// factory returns, so the wrapper stays bare and cacheable:
/// <c>Memo(id, () =&gt; Row(item)).Padding(8)</c> → <c>Memo(id, () =&gt; Row(item).Padding(8))</c>.
/// </summary>
/// <remarks>
/// The transform re-roots the modifier chain: within the full decorated expression
/// (<c>Memo(...).Padding(8).Margin(4)</c>) the <c>Memo(...)</c> sub-node is replaced by the
/// factory body, yielding the new body (<c>Row(item).Padding(8).Margin(4)</c>), which is then
/// dropped back into the factory lambda. Only movable factories reach here — the analyzer already
/// guaranteed a parameterless lambda with an expression body or a single-<c>return</c> block — so
/// the fix never has to reason about captures or multi-statement bodies.
/// </remarks>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(MemoWrapperModifierCodeFix))]
[Shared]
public sealed class MemoWrapperModifierCodeFix : CodeFixProvider
{
    public override ImmutableArray<string> FixableDiagnosticIds =>
        ImmutableArray.Create(MemoWrapperModifierAnalyzer.DiagnosticId);

    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        if (root is null) return;

        foreach (var diagnostic in context.Diagnostics)
        {
            // The diagnostic is reported on the modifier's name token; the enclosing invocation is
            // the innermost modifier call whose receiver is the raw keyed Memo(...) call.
            var node = root.FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie: true);
            if (node.FirstAncestorOrSelf<InvocationExpressionSyntax>() is not { } innerModifier)
                continue;

            if (innerModifier.Expression is not MemberAccessExpressionSyntax { Expression: InvocationExpressionSyntax memoInvocation })
                continue;

            if (!MemoWrapperModifierAnalyzer.TryGetMovableFactory(memoInvocation, out var lambda, out var body))
                continue;

            context.RegisterCodeFix(
                CodeAction.Create(
                    "Move modifiers inside the Memo factory",
                    ct => MoveModifiersIntoFactory(context.Document, root, innerModifier, memoInvocation, lambda, body),
                    equivalenceKey: MemoWrapperModifierAnalyzer.DiagnosticId),
                diagnostic);
        }
    }

    private static Task<Document> MoveModifiersIntoFactory(
        Document document,
        SyntaxNode root,
        InvocationExpressionSyntax innerModifier,
        InvocationExpressionSyntax memoInvocation,
        ParenthesizedLambdaExpressionSyntax lambda,
        ExpressionSyntax factoryBody)
    {
        // Walk up the modifier chain to its outermost invocation:
        //   Memo(k, f).Padding(8).Margin(4)  →  outermost = the `.Margin(4)` invocation.
        var outermost = innerModifier;
        while (outermost.Parent is MemberAccessExpressionSyntax parentAccess
               && parentAccess.Expression == outermost
               && parentAccess.Parent is InvocationExpressionSyntax parentInvocation)
        {
            outermost = parentInvocation;
        }

        // Re-root the whole chain on the factory body: replacing the Memo(...) sub-node with the
        // body turns `Memo(k, f).Padding(8).Margin(4)` into `body.Padding(8).Margin(4)`.
        var substituteBody = NeedsParentheses(factoryBody)
            ? SyntaxFactory.ParenthesizedExpression(factoryBody.WithoutTrivia())
            : factoryBody.WithoutTrivia();

        var newFactoryBody = outermost.ReplaceNode(memoInvocation, substituteBody);

        // Drop the re-rooted chain back into the lambda, preserving the lambda's original shape.
        ParenthesizedLambdaExpressionSyntax newLambda = lambda.ExpressionBody is not null
            ? lambda.WithExpressionBody(newFactoryBody)
            : lambda.WithBlock(((BlockSyntax)lambda.Block!).ReplaceNode(factoryBody, newFactoryBody));

        // Rebuild the Memo(...) call with the rewritten factory and no trailing modifiers.
        var factoryArgument = memoInvocation.ArgumentList.Arguments[1];
        var newArguments = memoInvocation.ArgumentList.Arguments.Replace(
            factoryArgument,
            factoryArgument.WithExpression(newLambda));
        var newMemoInvocation = memoInvocation
            .WithArgumentList(memoInvocation.ArgumentList.WithArguments(newArguments))
            .WithTriviaFrom(outermost);

        var newRoot = root.ReplaceNode(outermost, newMemoInvocation);
        return Task.FromResult(document.WithSyntaxRoot(newRoot));
    }

    /// <summary>
    /// When the factory body takes the receiver position of the moved modifier chain, wrap it in
    /// parentheses unless it is already a primary/postfix expression — otherwise operator
    /// precedence would rebind the modifier (e.g. <c>cond ? a : b.Padding()</c>).
    /// </summary>
    private static bool NeedsParentheses(ExpressionSyntax expression) => expression switch
    {
        InvocationExpressionSyntax => false,
        MemberAccessExpressionSyntax => false,
        ElementAccessExpressionSyntax => false,
        IdentifierNameSyntax => false,
        GenericNameSyntax => false,
        ParenthesizedExpressionSyntax => false,
        ObjectCreationExpressionSyntax => false,
        ImplicitObjectCreationExpressionSyntax => false,
        ThisExpressionSyntax => false,
        BaseExpressionSyntax => false,
        LiteralExpressionSyntax => false,
        MemberBindingExpressionSyntax => false,
        ConditionalAccessExpressionSyntax => false,
        PostfixUnaryExpressionSyntax => false,
        TupleExpressionSyntax => false,
        _ => true,
    };
}
