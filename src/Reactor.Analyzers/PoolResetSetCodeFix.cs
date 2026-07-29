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
/// Code fix for REACTOR_POOL_001: rewrites <c>x.Set(fe =&gt; fe.PROP = VALUE)</c>
/// to <c>x.PROP(VALUE)</c> using the corresponding Reactor modifier.
/// Block-body lambdas with a single assignment statement
/// (<c>fe =&gt; { fe.PROP = VALUE; }</c>) are also handled.
/// </summary>
/// <remarks>
/// Where the modifier signature differs from the property type, the codefix
/// translates the RHS into the modifier's expected shape (see <c>Margin</c>
/// below). When no safe translation exists, the codefix is suppressed —
/// the analyzer still reports the trap, the developer just has to fix by hand.
/// </remarks>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(PoolResetSetCodeFix))]
[Shared]
public sealed class PoolResetSetCodeFix : CodeFixProvider
{
    public override ImmutableArray<string> FixableDiagnosticIds =>
        ImmutableArray.Create(
            PoolResetSetAnalyzer.DiagnosticId,
            PoolResetSetAnalyzer.ModifierAvailableDiagnosticId);

    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        if (root is null) return;

        foreach (var diagnostic in context.Diagnostics)
        {
            var span = diagnostic.Location.SourceSpan;
            var node = root.FindNode(span);
            if (node is not InvocationExpressionSyntax invocation) continue;
            if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess) continue;

            var args = invocation.ArgumentList.Arguments;
            if (args.Count != 1) continue;

            var assignment = SetLambdaHelpers.TryGetLambdaAssignment(args[0].Expression);
            if (assignment is null) continue;
            if (assignment.Left is not MemberAccessExpressionSyntax leftAccess) continue;

            var propName = leftAccess.Name.Identifier.Text;
            if (!ModifierTable.Properties.TryGetValue(propName, out var info))
                continue;
            var modifierName = info.Modifier;

            var modifierArgs = TryBuildModifierArguments(propName, assignment.Right);
            if (modifierArgs is null) continue; // Cannot safely translate RHS; leave the warning unfixed.

            context.RegisterCodeFix(
                CodeAction.Create(
                    $"Use .{modifierName}() modifier",
                    ct =>
                    {
                        var newInvocation = SyntaxFactory.InvocationExpression(
                            SyntaxFactory.MemberAccessExpression(
                                SyntaxKind.SimpleMemberAccessExpression,
                                memberAccess.Expression,
                                SyntaxFactory.IdentifierName(modifierName)),
                            modifierArgs)
                            .WithTriviaFrom(invocation);

                        var newRoot = root.ReplaceNode(invocation, newInvocation);
                        return Task.FromResult(context.Document.WithSyntaxRoot(newRoot));
                    },
                    equivalenceKey: diagnostic.Id + ":" + modifierName),
                diagnostic);
        }
    }

    /// <summary>
    /// Build the argument list for the modifier call, translating the RHS when
    /// the modifier signature differs from the raw FE property type.
    /// </summary>
    /// <returns>
    /// The argument list to pass to the modifier, or <c>null</c> if no safe
    /// translation is possible (in which case no codefix is registered).
    /// </returns>
    private static ArgumentListSyntax? TryBuildModifierArguments(string propName, ExpressionSyntax value)
    {
        // Margin/Padding/BorderThickness are Thickness-typed properties whose modifiers all
        // take doubles, and CornerRadius is the same shape with a CornerRadius struct.
        // Translate the literal constructor forms:
        //   new Thickness(uniform)      → .Padding(uniform)
        //   new Thickness(l, t, r, b)   → .Padding(l, t, r, b)
        // Other RHS shapes (variables, member access, no-arg construction) cannot be
        // rewritten safely — skip the fix and leave the diagnostic for a human.
        if (propName is "Margin" or "Padding" or "BorderThickness" or "CornerRadius")
        {
            var structName = propName == "CornerRadius" ? "CornerRadius" : "Thickness";
            if (value is not ObjectCreationExpressionSyntax oce) return null;
            if (!IsNamedType(oce.Type, structName)) return null;
            var ctorArgs = oce.ArgumentList?.Arguments;
            if (ctorArgs is null) return null;
            // Both structs have 0/1/4-arg constructors. The 0-arg form is not interesting;
            // 1 and 4 map cleanly onto the uniform and per-edge modifier overloads.
            if (ctorArgs.Value.Count is 1 or 4)
                return SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(ctorArgs.Value));
            return null;
        }

        // All other tracked properties: the modifier accepts the same type
        // as the property (double / enum / string / Brush), so pass the RHS through.
        return SyntaxFactory.ArgumentList(
            SyntaxFactory.SingletonSeparatedList(SyntaxFactory.Argument(value)));
    }

    private static bool IsNamedType(TypeSyntax type, string simpleName) => type switch
    {
        IdentifierNameSyntax id => id.Identifier.Text == simpleName,
        QualifiedNameSyntax q => q.Right.Identifier.Text == simpleName,
        _ => false,
    };
}
