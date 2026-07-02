using System.Collections.Generic;
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
/// Code fix for <see cref="DuplicateAtomicModifierAnalyzer"/>
/// (<c>REACTOR_MOD_001</c>) — merges duplicate atomic-replace placement modifier
/// calls in one fluent chain into a single call, e.g.
/// <c>.Grid(row: 1).Grid(column: 2)</c> → <c>.Grid(row: 1, column: 2)</c>.
/// </summary>
/// <remarks>
/// The merge combines each call's <em>explicitly supplied</em> arguments; when
/// the same parameter is set more than once the later (outer) call wins, and
/// parameters only ever set by one call are preserved. This is the behaviour the
/// author almost certainly intended — the current chain silently drops every
/// argument except those on the final call.
///
/// The fix withholds itself when the calls do not all bind to the same modifier
/// overload, or when an argument can't be mapped to a named parameter (e.g. a
/// <c>params</c> slot) — cases where a single merged call can't be produced
/// safely. The diagnostic still fires so the author can merge by hand.
/// </remarks>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(DuplicateAtomicModifierCodeFix))]
[Shared]
public sealed class DuplicateAtomicModifierCodeFix : CodeFixProvider
{
    public override ImmutableArray<string> FixableDiagnosticIds =>
        ImmutableArray.Create(DuplicateAtomicModifierAnalyzer.DiagnosticId);

    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        if (root is null) return;

        var model = await context.Document.GetSemanticModelAsync(context.CancellationToken).ConfigureAwait(false);
        if (model is null) return;

        foreach (var diagnostic in context.Diagnostics)
        {
            var outermost = root.FindNode(diagnostic.Location.SourceSpan)
                .FirstAncestorOrSelf<InvocationExpressionSyntax>();
            if (outermost is null) continue;

            var name = DuplicateAtomicModifierAnalyzer.GetFluentMethodName(outermost);
            if (name is null || !DuplicateAtomicModifierAnalyzer.AtomicModifiers.ContainsKey(name))
                continue;

            var occurrences = CollectSameNameOccurrences(outermost, name);
            if (occurrences.Count < 2) continue;

            var mergedArgList = TryBuildMergedArgumentList(model, occurrences, context.CancellationToken);
            if (mergedArgList is null) continue; // withhold — can't merge safely

            context.RegisterCodeFix(
                CodeAction.Create(
                    $"Merge duplicate '.{name}(...)' calls into one",
                    ct => Task.FromResult(
                        context.Document.WithSyntaxRoot(
                            MergeChain(root, occurrences, mergedArgList))),
                    equivalenceKey: $"{DuplicateAtomicModifierAnalyzer.DiagnosticId}_Merge"),
                diagnostic);
        }
    }

    /// <summary>All same-name occurrences on the chain, innermost first.</summary>
    private static List<InvocationExpressionSyntax> CollectSameNameOccurrences(
        InvocationExpressionSyntax outermost, string name)
    {
        var list = new List<InvocationExpressionSyntax>();
        for (var node = outermost; node is not null;
             node = DuplicateAtomicModifierAnalyzer.GetReceiverInvocation(node))
        {
            if (DuplicateAtomicModifierAnalyzer.GetFluentMethodName(node) == name)
                list.Add(node);
        }
        list.Reverse();
        return list;
    }

    /// <summary>
    /// Merge the explicit arguments of every occurrence (innermost → outermost,
    /// later wins per parameter) into a single named-argument list. Returns null
    /// when the merge can't be produced safely.
    /// </summary>
    private static ArgumentListSyntax? TryBuildMergedArgumentList(
        SemanticModel model,
        List<InvocationExpressionSyntax> occurrences,
        CancellationToken ct)
    {
        IMethodSymbol? sharedMethod = null;
        var byOrdinal = new SortedDictionary<int, ArgumentSyntax>();

        foreach (var occ in occurrences)
        {
            if (model.GetSymbolInfo(occ, ct).Symbol is not IMethodSymbol method)
                return null;

            // Require a single shared overload so the merged named-argument list
            // is guaranteed to bind back to it.
            var key = method.ReducedFrom ?? method.OriginalDefinition;
            if (sharedMethod is null)
                sharedMethod = key;
            else if (!SymbolEqualityComparer.Default.Equals(sharedMethod, key))
                return null;

            var parameters = method.Parameters;
            var arguments = occ.ArgumentList.Arguments;

            for (var i = 0; i < arguments.Count; i++)
            {
                var argument = arguments[i];

                IParameterSymbol? parameter;
                if (argument.NameColon is { } nameColon)
                {
                    var pname = nameColon.Name.Identifier.ValueText;
                    parameter = parameters.FirstOrDefault(p => p.Name == pname);
                }
                else
                {
                    parameter = i < parameters.Length ? parameters[i] : null;
                }

                // Can't map the argument to a discrete named parameter (unknown
                // name, positional overflow, or a params slot) — bail out.
                if (parameter is null || parameter.IsParams)
                    return null;

                byOrdinal[parameter.Ordinal] = MakeNamedArgument(parameter.Name, argument.Expression);
            }
        }

        if (byOrdinal.Count == 0)
            return SyntaxFactory.ArgumentList();

        var ordered = byOrdinal.Values.ToList();
        var separators = Enumerable.Repeat(
            SyntaxFactory.Token(SyntaxKind.CommaToken).WithTrailingTrivia(SyntaxFactory.Space),
            ordered.Count - 1);

        return SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(ordered, separators));
    }

    private static ArgumentSyntax MakeNamedArgument(string parameterName, ExpressionSyntax value)
    {
        var nameColon = SyntaxFactory.NameColon(SyntaxFactory.IdentifierName(parameterName))
            .WithColonToken(SyntaxFactory.Token(SyntaxKind.ColonToken).WithTrailingTrivia(SyntaxFactory.Space));

        return SyntaxFactory.Argument(nameColon, default, value.WithoutTrivia());
    }

    /// <summary>
    /// Collapse the chain: peel every inner same-name call and give the outermost
    /// call the merged argument list. <see cref="SyntaxNode.ReplaceNodes"/>
    /// rewrites descendants first, so the outermost node already has its inner
    /// duplicates removed by the time we swap in the merged arguments.
    /// </summary>
    private static SyntaxNode MergeChain(
        SyntaxNode root,
        List<InvocationExpressionSyntax> occurrences,
        ArgumentListSyntax mergedArgList)
    {
        var outermost = occurrences[occurrences.Count - 1];

        return root.ReplaceNodes(occurrences, (original, rewritten) =>
        {
            if (original == outermost)
                return rewritten.WithArgumentList(mergedArgList);

            // Peel this inner call: replace `receiver.Name(args)` with `receiver`.
            var memberAccess = (MemberAccessExpressionSyntax)rewritten.Expression;
            return memberAccess.Expression.WithTriviaFrom(rewritten);
        });
    }
}
