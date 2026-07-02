using System.Collections.Immutable;
using System.Composition;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Microsoft.UI.Reactor.Analyzers;

/// <summary>
/// Code fix for REACTOR_WIN2D_001: appends <c>.UseSharedDevice()</c> to the outermost fluent
/// expression of the offending Win2D canvas, e.g.
/// <c>Win2DCanvas(draw).ClearColor(c)</c> → <c>Win2DCanvas(draw).ClearColor(c).UseSharedDevice()</c>.
/// </summary>
/// <remarks>
/// The bare <c>.UseSharedDevice()</c> call always resolves at the fix site: the rule only fires
/// when <c>UseCanvasResources</c> — an extension method in
/// <c>Microsoft.UI.Reactor.Advanced.Win2D</c> — is in scope, which means that namespace is already
/// imported, and <c>.UseSharedDevice()</c> (defined on <c>Win2DCanvasModifiers</c> in the same
/// namespace) is therefore in scope too. No <c>using</c> insertion or qualification is needed.
/// </remarks>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(Win2DSharedDeviceCodeFix))]
[Shared]
public sealed class Win2DSharedDeviceCodeFix : CodeFixProvider
{
    private const string Title = "Append .UseSharedDevice() to the canvas";

    public override ImmutableArray<string> FixableDiagnosticIds =>
        ImmutableArray.Create(Win2DSharedDeviceAnalyzer.DiagnosticId);

    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        if (root is null) return;

        foreach (var diagnostic in context.Diagnostics)
        {
            var node = root.FindNode(diagnostic.Location.SourceSpan);
            var factory = node.FirstAncestorOrSelf<InvocationExpressionSyntax>();
            if (factory is null) continue;

            var outer = Win2DSharedDeviceAnalyzer.GetOutermostFluentInvocation(factory);

            context.RegisterCodeFix(
                CodeAction.Create(
                    Title,
                    _ => Task.FromResult(AppendUseSharedDevice(context.Document, root, outer)),
                    equivalenceKey: Win2DSharedDeviceAnalyzer.DiagnosticId),
                diagnostic);
        }
    }

    private static Document AppendUseSharedDevice(Document document, SyntaxNode root, InvocationExpressionSyntax outer)
    {
        // Keep the chain's leading trivia on the receiver and move its trailing trivia past the
        // appended call so `...Height(220)\n` becomes `...Height(220).UseSharedDevice()\n`.
        var trailing = outer.GetTrailingTrivia();
        var receiver = outer.WithoutTrailingTrivia();

        var appended = SyntaxFactory.InvocationExpression(
            SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                receiver,
                SyntaxFactory.IdentifierName("UseSharedDevice")))
            .WithTrailingTrivia(trailing);

        return document.WithSyntaxRoot(root.ReplaceNode(outer, appended));
    }
}
