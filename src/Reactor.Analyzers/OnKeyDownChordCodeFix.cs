using System.Collections.Immutable;
using System.Composition;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Microsoft.UI.Reactor.Analyzers;

/// <summary>
/// Template code fix for <see cref="OnKeyDownChordAnalyzer"/> (<c>REACTOR_INPUT_001</c>).
/// </summary>
/// <remarks>
/// <para>
/// Rewriting a focus-scoped <c>.OnKeyDown</c> chord into an app-wide <c>Command</c> accelerator is
/// <b>intent-heavy</b>: the command belongs wherever the app registers its shortcuts (not on the
/// element), its <c>Execute</c> body has to be lifted out of a handler that closes over the event
/// args, and only the author knows the command's label/scope. There is therefore no safe, fully
/// mechanical rewrite. This fix is a <b>template/preview</b>: it appends a single-line scaffold
/// comment to the offending call — extracting the concrete <c>VirtualKey</c> and the Ctrl/Alt
/// modifier(s) it detected — that shows the exact <c>new Command { …, Accelerator =
/// Accelerator(VirtualKey.S, VirtualKeyModifiers.Control) }</c> shape to write.
/// </para>
/// <para>
/// The fix is deliberately <b>additive</b>: it never edits executable code, so it can never drop a
/// handler's other key handling, break compilation on any receiver, or change runtime behavior. The
/// warning persists until the author migrates the shortcut and removes the <c>.OnKeyDown</c> chord.
/// </para>
/// </remarks>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(OnKeyDownChordCodeFix))]
[Shared]
public sealed class OnKeyDownChordCodeFix : CodeFixProvider
{
    public override ImmutableArray<string> FixableDiagnosticIds =>
        ImmutableArray.Create(OnKeyDownChordAnalyzer.DiagnosticId);

    // No FixAll: the scaffold is per-call context (each handler's key/modifiers differ), and the
    // fix is a template preview rather than a mechanical rewrite, so "fix all" carries no benefit.
    public override FixAllProvider? GetFixAllProvider() => null;

    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        if (root is null) return;

        foreach (var diagnostic in context.Diagnostics)
        {
            var node = root.FindNode(diagnostic.Location.SourceSpan);
            if (node is not InvocationExpressionSyntax invocation) continue;
            if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "OnKeyDown" }) continue;
            if (invocation.ArgumentList.Arguments.Count != 1) continue;
            if (invocation.ArgumentList.Arguments[0].Expression is not LambdaExpressionSyntax lambda) continue;

            var body = lambda.Body;
            if (body is null) continue;

            var comment = BuildScaffoldComment(body);

            context.RegisterCodeFix(
                CodeAction.Create(
                    "Add Command-accelerator template (REACTOR_INPUT_001)",
                    ct => Task.FromResult(AppendComment(context.Document, root, invocation, comment)),
                    equivalenceKey: OnKeyDownChordAnalyzer.DiagnosticId),
                diagnostic);
        }
    }

    private static Document AppendComment(Document document, SyntaxNode root, InvocationExpressionSyntax invocation, string comment)
    {
        var newTrailing = invocation.GetTrailingTrivia()
            .Add(SyntaxFactory.Space)
            .Add(SyntaxFactory.Comment(comment));

        var newInvocation = invocation.WithTrailingTrivia(newTrailing);
        return document.WithSyntaxRoot(root.ReplaceNode(invocation, newInvocation));
    }

    /// <summary>
    /// Builds the single-line block-comment scaffold, filling in the concrete <c>VirtualKey</c>
    /// (from the first <c>VirtualKey.&lt;X&gt;</c> the lambda references) and the Ctrl/Alt
    /// modifier expression (<c>VirtualKeyModifiers.Control</c>, <c>.Menu</c>, or both).
    /// </summary>
    private static string BuildScaffoldComment(SyntaxNode body)
    {
        var key = ExtractVirtualKey(body) is { } k ? $"VirtualKey.{k}" : "VirtualKey.<key>";
        var modifiers = BuildModifierExpression(body);

        return $"/* REACTOR_INPUT_001: .OnKeyDown is focus-scoped. Register this shortcut app-wide as a " +
               $"Command accelerator instead, e.g. new Command {{ Label = <name>, Execute = <handler>, " +
               $"Accelerator = Accelerator({key}, {modifiers}) }}, then remove this .OnKeyDown chord. */";
    }

    /// <summary>Name of the first <c>VirtualKey.&lt;X&gt;</c> the lambda body references, or null.</summary>
    private static string? ExtractVirtualKey(SyntaxNode body)
    {
        foreach (var access in body.DescendantNodesAndSelf().OfType<MemberAccessExpressionSyntax>())
        {
            if (access.Expression is IdentifierNameSyntax { Identifier.Text: "VirtualKey" })
                return access.Name.Identifier.Text;
        }
        return null;
    }

    /// <summary>
    /// The <c>VirtualKeyModifiers</c> expression to seed the template with, reflecting whichever of
    /// <c>Control</c> / <c>Menu</c> the lambda tests. Defaults to <c>Control</c> if neither is found
    /// syntactically (the analyzer only fires when at least one bound semantically).
    /// </summary>
    private static string BuildModifierExpression(SyntaxNode body)
    {
        var hasControl = false;
        var hasMenu = false;

        foreach (var access in body.DescendantNodesAndSelf().OfType<MemberAccessExpressionSyntax>())
        {
            var receiver = access.Expression switch
            {
                IdentifierNameSyntax id => id.Identifier.Text,
                MemberAccessExpressionSyntax m => m.Name.Identifier.Text,
                _ => null,
            };
            if (receiver != "VirtualKeyModifiers") continue;

            switch (access.Name.Identifier.Text)
            {
                case "Control": hasControl = true; break;
                case "Menu": hasMenu = true; break;
            }
        }

        return (hasControl, hasMenu) switch
        {
            (true, true) => "VirtualKeyModifiers.Control | VirtualKeyModifiers.Menu",
            (false, true) => "VirtualKeyModifiers.Menu",
            _ => "VirtualKeyModifiers.Control",
        };
    }
}
