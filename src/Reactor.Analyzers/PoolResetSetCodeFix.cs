using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Composition;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Microsoft.UI.Reactor.Analyzers;

/// <summary>
/// Code fix for REACTOR_POOL_001 / REACTOR_MOD_002: rewrites
/// <c>x.Set(fe =&gt; fe.PROP = VALUE)</c> to <c>x.PROP(VALUE)</c> using the corresponding
/// Reactor modifier. Block-body lambdas are handled too, including multi-statement ones —
/// <c>fe =&gt; { fe.A = 1; fe.B = 2; }</c> becomes <c>.A(1).B(2)</c>.
/// </summary>
/// <remarks>
/// <para>
/// Where the modifier signature differs from the property type, the codefix
/// translates the RHS into the modifier's expected shape (see <c>Margin</c>
/// below). When no safe translation exists, the codefix is suppressed —
/// the analyzer still reports the trap, the developer just has to fix by hand.
/// </para>
/// <para>
/// Multi-statement bodies are converted all-or-nothing; see
/// <see cref="TryBuildChain"/> for why a partial extraction is not offered.
/// </para>
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

        // Each diagnostic carries the complete reported set for its invocation, so one is
        // enough to rewrite the whole `.Set(...)`. Dedupe by span so a block body — which
        // reports once per convertible assignment — produces a single chain fix rather than N
        // competing fixes that each want to replace the same node.
        var seen = new HashSet<TextSpan>();

        foreach (var diagnostic in context.Diagnostics)
        {
            var span = diagnostic.Location.SourceSpan;
            if (!seen.Add(span)) continue;

            var node = root.FindNode(span);
            if (node is not InvocationExpressionSyntax invocation) continue;
            if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess) continue;

            var args = invocation.ArgumentList.Arguments;
            if (args.Count != 1) continue;

            // Only the properties the analyzer actually reported are safe to convert — it
            // applied the receiver gates, this fix does not repeat them.
            if (!diagnostic.Properties.TryGetValue(PoolResetSetAnalyzer.ReportedPropertiesKey, out var packed)
                || string.IsNullOrEmpty(packed))
            {
                continue;
            }
            var reported = new HashSet<string>(packed!.Split(','), StringComparer.Ordinal);

            var assignments = SetLambdaHelpers.GetLambdaAssignments(args[0].Expression);
            if (assignments.IsDefaultOrEmpty) continue;

            var steps = TryBuildChain(assignments, reported);
            if (steps is null) continue; // Mixed or untranslatable body — leave the diagnostic unfixed.

            var title = steps.Count == 1
                ? $"Use .{steps[0].Modifier}() modifier"
                : $"Use .{string.Join("().", steps.Select(s => s.Modifier))}() modifiers";

            context.RegisterCodeFix(
                CodeAction.Create(
                    title,
                    ct =>
                    {
                        // Chain in source order so any ordering the author relied on between
                        // the writes is preserved.
                        ExpressionSyntax rewritten = memberAccess.Expression;
                        foreach (var step in steps)
                        {
                            rewritten = SyntaxFactory.InvocationExpression(
                                SyntaxFactory.MemberAccessExpression(
                                    SyntaxKind.SimpleMemberAccessExpression,
                                    rewritten,
                                    SyntaxFactory.IdentifierName(step.Modifier)),
                                step.Arguments);
                        }

                        var newRoot = root.ReplaceNode(invocation, rewritten.WithTriviaFrom(invocation));
                        return Task.FromResult(context.Document.WithSyntaxRoot(newRoot));
                    },
                    equivalenceKey: "ReactorModifierChain:" + string.Join(",", steps.Select(s => s.Modifier))),
                diagnostic);
        }
    }

    /// <summary>
    /// Build the ordered modifier chain replacing a whole <c>.Set(...)</c> body, or
    /// <c>null</c> when the body cannot be converted in full.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately all-or-nothing. Converting <em>every</em> statement removes the
    /// <c>.Set</c> entirely, which is exactly N applications of the long-standing
    /// single-assignment rewrite and carries no new risk.
    /// </para>
    /// <para>
    /// A partial extraction would be different in kind: it leaves a residual <c>.Set</c>, and
    /// the extracted write moves from the setter phase into the modifier phase — so its order
    /// relative to the statements left behind changes. That is harmless for independent
    /// properties but cannot be shown safe in general (the reconciler has real
    /// order-sensitive pairs, e.g. TextBox <c>AcceptsReturn</c> before <c>Text</c>). So a
    /// mixed body keeps its diagnostic and gets no automatic fix.
    /// </para>
    /// </remarks>
    private static List<(string Modifier, ArgumentListSyntax Arguments)>? TryBuildChain(
        ImmutableArray<AssignmentExpressionSyntax> assignments,
        HashSet<string> reported)
    {
        var steps = new List<(string Modifier, ArgumentListSyntax Arguments)>();

        foreach (var assignment in assignments)
        {
            if (!assignment.IsKind(SyntaxKind.SimpleAssignmentExpression)) return null;
            if (assignment.Left is not MemberAccessExpressionSyntax leftAccess) return null;

            var propName = leftAccess.Name.Identifier.Text;

            // Not reported means the analyzer gated it out (wrong control type for the
            // modifier, or no modifier at all). Converting it would be the silent no-op the
            // gate exists to prevent.
            if (!reported.Contains(propName)) return null;
            if (!ModifierTable.Properties.TryGetValue(propName, out var info)) return null;

            var modifierArgs = TryBuildModifierArguments(propName, assignment.Right);
            if (modifierArgs is null) return null;

            steps.Add((info.Modifier, modifierArgs));
        }

        return steps.Count == 0 ? null : steps;
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
