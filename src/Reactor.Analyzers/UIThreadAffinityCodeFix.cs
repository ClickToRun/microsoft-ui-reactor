using System.Collections.Immutable;
using System.Composition;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Simplification;

namespace Microsoft.UI.Reactor.Analyzers;

/// <summary>
/// Code fix for REACTOR_THREAD_001: marshals a UI-thread-only call that runs on a
/// background thread back onto the UI thread through the Reactor dispatcher.
/// </summary>
/// <remarks>
/// The rewrite is null-safe by design — <c>ReactorApp.UIDispatcher</c> is a
/// <c>DispatcherQueue?</c> that is null until the first window bootstraps, so the
/// fix falls back to the direct call rather than a null-forgiving <c>!</c>:
/// <code>
/// var d = ReactorApp.UIDispatcher;
/// if (d is null)
///     window.Close();
/// else
///     d.TryEnqueue(() =&gt; window.Close());
/// </code>
/// It handles the two common shapes — the flagged call as a statement inside a
/// background lambda block, and the flagged call as the expression body of the
/// background lambda. Other shapes leave the warning unfixed (the trap is still
/// reported); a non-void expression-bodied lambda is skipped because turning it
/// into a block would drop the produced value.
/// </remarks>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(UIThreadAffinityCodeFix))]
[Shared]
public sealed class UIThreadAffinityCodeFix : CodeFixProvider
{
    private const string Title = "Marshal call onto the UI thread via ReactorApp.UIDispatcher";

    public override ImmutableArray<string> FixableDiagnosticIds =>
        ImmutableArray.Create(UIThreadAffinityAnalyzer.DiagnosticId);

    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        if (root is null) return;

        foreach (var diagnostic in context.Diagnostics)
        {
            var node = root.FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie: true);
            var invocation = node.FirstAncestorOrSelf<InvocationExpressionSyntax>();
            if (invocation is null) continue;

            // Shape A: the call is a stand-alone statement (`window.Close();`).
            if (invocation.Parent is ExpressionStatementSyntax statement)
            {
                context.RegisterCodeFix(
                    CodeAction.Create(
                        Title,
                        ct => Task.FromResult(FixStatement(context.Document, root, statement, invocation)),
                        equivalenceKey: UIThreadAffinityAnalyzer.DiagnosticId),
                    diagnostic);
                continue;
            }

            // Shape B: the call is the expression body of the background lambda
            // (`Task.Run(() => window.Close())`). Only rewrite when the call is
            // void — otherwise the lambda's produced value would be lost.
            if (invocation.Parent is LambdaExpressionSyntax lambda &&
                lambda.ExpressionBody == invocation)
            {
                var semanticModel = await context.Document
                    .GetSemanticModelAsync(context.CancellationToken).ConfigureAwait(false);
                if (semanticModel is null) continue;
                if (semanticModel.GetSymbolInfo(invocation, context.CancellationToken).Symbol
                        is not IMethodSymbol { ReturnsVoid: true })
                    continue;

                context.RegisterCodeFix(
                    CodeAction.Create(
                        Title,
                        ct => Task.FromResult(FixExpressionLambda(context.Document, root, lambda, invocation)),
                        equivalenceKey: UIThreadAffinityAnalyzer.DiagnosticId),
                    diagnostic);
            }
        }
    }

    private static Document FixStatement(
        Document document,
        SyntaxNode root,
        ExpressionStatementSyntax statement,
        InvocationExpressionSyntax invocation)
    {
        var declaration = BuildDispatcherDeclaration();
        var dispatchIf = BuildDispatchIf(invocation);

        SyntaxNode newRoot;
        if (statement.Parent is BlockSyntax block)
        {
            // Reformat the whole enclosing block so the declaration + guard land
            // cleanly next to the block's other statements.
            var index = block.Statements.IndexOf(statement);
            var newStatements = block.Statements
                .RemoveAt(index)
                .Insert(index, dispatchIf)
                .Insert(index, declaration);
            var newBlock = block.WithStatements(newStatements)
                .NormalizeWhitespace(elasticTrivia: true)
                .WithTriviaFrom(block)
                .WithAdditionalAnnotations(Formatter.Annotation);
            newRoot = root.ReplaceNode(block, newBlock);
        }
        else
        {
            // Embedded (braceless) or switch-section context — wrap in a block so
            // the two replacement statements stay well-formed.
            var wrapper = SyntaxFactory.Block(declaration, dispatchIf)
                .NormalizeWhitespace(elasticTrivia: true)
                .WithTriviaFrom(statement)
                .WithAdditionalAnnotations(Formatter.Annotation);
            newRoot = root.ReplaceNode(statement, wrapper);
        }

        return document.WithSyntaxRoot(newRoot);
    }

    private static Document FixExpressionLambda(
        Document document,
        SyntaxNode root,
        LambdaExpressionSyntax lambda,
        InvocationExpressionSyntax invocation)
    {
        var block = SyntaxFactory.Block(BuildDispatcherDeclaration(), BuildDispatchIf(invocation));

        LambdaExpressionSyntax newLambda = lambda switch
        {
            SimpleLambdaExpressionSyntax simple =>
                simple.WithExpressionBody(null).WithBlock(block),
            ParenthesizedLambdaExpressionSyntax paren =>
                paren.WithExpressionBody(null).WithBlock(block),
            _ => lambda,
        };

        newLambda = newLambda
            .NormalizeWhitespace(elasticTrivia: true)
            .WithTriviaFrom(lambda)
            .WithAdditionalAnnotations(Formatter.Annotation);

        return document.WithSyntaxRoot(root.ReplaceNode(lambda, newLambda));
    }

    /// <summary>Builds <c>var d = ReactorApp.UIDispatcher;</c>.</summary>
    private static LocalDeclarationStatementSyntax BuildDispatcherDeclaration()
    {
        var dispatcherAccess = SyntaxFactory
            .ParseExpression("global::Microsoft.UI.Reactor.ReactorApp.UIDispatcher")
            .WithAdditionalAnnotations(Simplifier.Annotation);

        return SyntaxFactory.LocalDeclarationStatement(
            SyntaxFactory.VariableDeclaration(
                SyntaxFactory.IdentifierName("var"),
                SyntaxFactory.SingletonSeparatedList(
                    SyntaxFactory.VariableDeclarator(SyntaxFactory.Identifier("d"))
                        .WithInitializer(SyntaxFactory.EqualsValueClause(dispatcherAccess)))))
            .WithAdditionalAnnotations(Formatter.Annotation);
    }

    /// <summary>
    /// Builds <c>if (d is null) &lt;call&gt;; else d.TryEnqueue(() =&gt; &lt;call&gt;);</c>.
    /// </summary>
    private static IfStatementSyntax BuildDispatchIf(InvocationExpressionSyntax invocation)
    {
        var condition = SyntaxFactory.IsPatternExpression(
            SyntaxFactory.IdentifierName("d"),
            SyntaxFactory.ConstantPattern(
                SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression)));

        var directCall = SyntaxFactory.ExpressionStatement(invocation.WithoutTrivia());

        var marshaledCall = SyntaxFactory.ExpressionStatement(
            SyntaxFactory.InvocationExpression(
                SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    SyntaxFactory.IdentifierName("d"),
                    SyntaxFactory.IdentifierName("TryEnqueue")),
                SyntaxFactory.ArgumentList(
                    SyntaxFactory.SingletonSeparatedList(
                        SyntaxFactory.Argument(
                            SyntaxFactory.ParenthesizedLambdaExpression()
                                .WithExpressionBody(invocation.WithoutTrivia()))))));

        return SyntaxFactory.IfStatement(condition, directCall)
            .WithElse(SyntaxFactory.ElseClause(marshaledCall))
            .WithAdditionalAnnotations(Formatter.Annotation);
    }
}
