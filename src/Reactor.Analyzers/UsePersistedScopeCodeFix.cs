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
/// Code fix for <see cref="UsePersistedScopeAnalyzer"/> (<c>REACTOR_PERSIST_001</c>).
/// Appends an explicit scope argument to a two-argument <c>UsePersisted(key, initial)</c>
/// call, offering two actions:
/// <list type="bullet">
///   <item><c>, PersistedScope.Window</c> — host-lifetime scope (recommended).</item>
///   <item><c>, PersistedScope.Application</c> — process-wide, i.e. make the current
///   implicit behavior explicit.</item>
/// </list>
/// </summary>
/// <remarks>
/// Both actions are always safe (the three-argument overload always exists), so both
/// are offered unconditionally and the author picks. The rewrite only inserts the
/// argument; <c>PersistedScope</c> resolves through the same namespace that already
/// brings <c>RenderContext</c> into scope at the call site.
/// </remarks>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(UsePersistedScopeCodeFix))]
[Shared]
public sealed class UsePersistedScopeCodeFix : CodeFixProvider
{
    private const string ScopeTypeName = "PersistedScope";
    private const string RecommendedScope = "Window";
    private const string ExplicitScope = "Application";

    public override ImmutableArray<string> FixableDiagnosticIds =>
        ImmutableArray.Create(UsePersistedScopeAnalyzer.DiagnosticId);

    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        if (root is null) return;

        foreach (var diagnostic in context.Diagnostics)
        {
            var node = root.FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie: true);
            var invocation = node as InvocationExpressionSyntax
                ?? node.FirstAncestorOrSelf<InvocationExpressionSyntax>();
            if (invocation is null) continue;
            if (invocation.ArgumentList.Arguments.Count != 2) continue;

            RegisterScopeFix(context, root, invocation, diagnostic, RecommendedScope,
                "Scope to the host window (PersistedScope.Window, recommended)");
            RegisterScopeFix(context, root, invocation, diagnostic, ExplicitScope,
                "Keep process-wide scope (PersistedScope.Application, explicit)");
        }
    }

    private static void RegisterScopeFix(
        CodeFixContext context,
        SyntaxNode root,
        InvocationExpressionSyntax invocation,
        Diagnostic diagnostic,
        string scopeMember,
        string title)
    {
        context.RegisterCodeFix(
            CodeAction.Create(
                title,
                _ =>
                {
                    var scopeArgument = SyntaxFactory.Argument(
                        SyntaxFactory.MemberAccessExpression(
                            SyntaxKind.SimpleMemberAccessExpression,
                            SyntaxFactory.IdentifierName(ScopeTypeName),
                            SyntaxFactory.IdentifierName(scopeMember)))
                        .WithLeadingTrivia(SyntaxFactory.Space);

                    var newArgumentList = invocation.ArgumentList.WithArguments(
                        invocation.ArgumentList.Arguments.Add(scopeArgument));
                    var newInvocation = invocation.WithArgumentList(newArgumentList);

                    var newRoot = root.ReplaceNode(invocation, newInvocation);
                    return Task.FromResult(context.Document.WithSyntaxRoot(newRoot));
                },
                equivalenceKey: UsePersistedScopeAnalyzer.DiagnosticId + ":" + scopeMember),
            diagnostic);
    }
}
