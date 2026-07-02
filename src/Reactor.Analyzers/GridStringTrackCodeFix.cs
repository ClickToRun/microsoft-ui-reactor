using System.Collections.Generic;
using System.Collections.Immutable;
using System.Composition;
using System.Globalization;
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
/// Code fix registered on the compiler diagnostic <c>CS0618</c> for the obsolete
/// string-form <c>Grid(string[], string[], …)</c> factory
/// (<c>Microsoft.UI.Reactor.Factories</c>). Rewrites each inline string-literal
/// track (<c>"*"</c> / <c>"Auto"</c> / <c>"2*"</c> / <c>"200"</c>) to the typed
/// <see cref="M:Microsoft.UI.Reactor.GridSize.Star(System.Double)"/> /
/// <c>GridSize.Auto</c> / <c>GridSize.Px</c> form, which swaps overload resolution
/// to the typed <c>Grid(GridSize[], GridSize[], …)</c> overload — killing the
/// obsolete warning. Spec 060 §4.5.
/// </summary>
/// <remarks>
/// <para>
/// This is the one documented id-less member of the spec-060 suite: there is no
/// new diagnostic id, no <see cref="DiagnosticDescriptor"/>, and no
/// <c>AnalyzerReleases.Unshipped.md</c> row. It keys off the compiler's own
/// obsolete diagnostic, so the false-positive risk is nil.
/// </para>
/// <para>
/// The rewrite is fully mechanical, but only for <b>inline literal arrays</b>
/// whose every element is a plain string literal (collection expressions
/// <c>["*", …]</c>, <c>new[] { … }</c>, or <c>new string[] { … }</c>). When a
/// track array is a variable, a non-literal expression, an interpolated string,
/// or a spread — the concrete track values aren't visible, so no fix is offered
/// and the <c>CS0618</c> warning is left to stand.
/// </para>
/// <para>
/// The string→<c>GridSize</c> mapping mirrors <c>GridSize.Parse</c> (which agrees
/// with the runtime layout parse on every valid track): <c>"Auto"</c>
/// (case-insensitive) → <c>GridSize.Auto</c>; <c>"*"</c> → <c>GridSize.Star()</c>;
/// <c>"&lt;n&gt;*"</c> → <c>GridSize.Star(n)</c>; <c>"&lt;n&gt;"</c> →
/// <c>GridSize.Px(n)</c>. Any other literal cannot be mapped safely, so the whole
/// fix is withheld.
/// </para>
/// </remarks>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(GridStringTrackCodeFix))]
[Shared]
public sealed class GridStringTrackCodeFix : CodeFixProvider
{
    /// <summary>The C# compiler's "member is obsolete (warning)" diagnostic.</summary>
    private const string ObsoleteWarningId = "CS0618";

    private const string EquivalenceKey = "Reactor_GridStringTrack";

    public override ImmutableArray<string> FixableDiagnosticIds =>
        ImmutableArray.Create(ObsoleteWarningId);

    // Each fix is local and self-contained (it rewrites one invocation's two
    // track arrays), and only genuine Grid(string[],string[],…) call sites ever
    // register an action — unrelated CS0618s simply get no offer. That makes the
    // batch fixer safe for the common "convert every legacy Grid in the file" case.
    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        if (root is null) return;

        var model = await context.Document.GetSemanticModelAsync(context.CancellationToken).ConfigureAwait(false);
        if (model is null) return;

        foreach (var diagnostic in context.Diagnostics)
        {
            var node = root.FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie: true);
            var invocation = node.FirstAncestorOrSelf<InvocationExpressionSyntax>();
            if (invocation is null) continue;

            // Confirm the obsolete target is specifically our string-track Grid
            // overload — CS0618 at this span could be any other obsolete symbol.
            if (!IsObsoleteGridStringOverload(model, invocation, context.CancellationToken)) continue;

            var args = invocation.ArgumentList.Arguments;
            if (args.Count < 2) continue;

            var columnsExpr = args[0].Expression;
            var rowsExpr = args[1].Expression;

            // Both track arrays must be inline literal arrays whose every element
            // parses to a GridSize. If either can't be rewritten mechanically we
            // offer nothing and let the warning stand (spec 060 §4.5).
            if (!TryRewriteTrackArray(columnsExpr, out var newColumns)) continue;
            if (!TryRewriteTrackArray(rowsExpr, out var newRows)) continue;

            context.RegisterCodeFix(
                CodeAction.Create(
                    "Use typed GridSize tracks",
                    ct =>
                    {
                        var newInvocation = invocation.ReplaceNodes(
                            new SyntaxNode[] { columnsExpr, rowsExpr },
                            (original, _) =>
                                ReferenceEquals(original, columnsExpr) ? newColumns! : newRows!);

                        var newRoot = root.ReplaceNode(invocation, newInvocation);
                        return Task.FromResult(context.Document.WithSyntaxRoot(newRoot));
                    },
                    equivalenceKey: EquivalenceKey),
                diagnostic);
        }
    }

    /// <summary>
    /// True when <paramref name="invocation"/> binds to
    /// <c>Microsoft.UI.Reactor.Factories.Grid(string[], string[], …)</c> — the
    /// obsolete string-track overload.
    /// </summary>
    private static bool IsObsoleteGridStringOverload(
        SemanticModel model, InvocationExpressionSyntax invocation, CancellationToken ct)
    {
        var symbolInfo = model.GetSymbolInfo(invocation, ct);
        var method = symbolInfo.Symbol as IMethodSymbol
                     ?? symbolInfo.CandidateSymbols.OfType<IMethodSymbol>().FirstOrDefault();
        if (method is null) return false;

        if (method.Name != "Grid") return false;
        if (method.ContainingType?.ToDisplayString() != "Microsoft.UI.Reactor.Factories") return false;
        if (method.Parameters.Length < 2) return false;

        return IsStringArray(method.Parameters[0].Type) && IsStringArray(method.Parameters[1].Type);
    }

    private static bool IsStringArray(ITypeSymbol type) =>
        type is IArrayTypeSymbol { ElementType.SpecialType: SpecialType.System_String };

    /// <summary>
    /// Rewrites an inline literal track array to the typed <c>GridSize[]</c> form
    /// in place (preserving separators/trivia). Returns <see langword="false"/>
    /// when the argument is not an inline literal array or any element cannot be
    /// mapped to a <c>GridSize</c>.
    /// </summary>
    private static bool TryRewriteTrackArray(ExpressionSyntax expr, out ExpressionSyntax? rewritten)
    {
        rewritten = null;

        switch (expr)
        {
            // C# 12 collection expression: ["*", "Auto", "200"]
            case CollectionExpressionSyntax collection:
            {
                var literals = new List<LiteralExpressionSyntax>();
                foreach (var element in collection.Elements)
                {
                    if (element is not ExpressionElementSyntax exprElement) return false;
                    if (!TryGetStringLiteral(exprElement.Expression, out var literal)) return false;
                    literals.Add(literal);
                }

                if (!TryBuildReplacements(literals, out var map)) return false;
                rewritten = collection.ReplaceNodes(literals, (original, _) => map[original]);
                return true;
            }

            // Implicitly typed array: new[] { "*", "Auto" }
            case ImplicitArrayCreationExpressionSyntax implicitArray:
                return TryRewriteInitializer(implicitArray, implicitArray.Initializer, out rewritten);

            // Explicitly typed array: new string[] { "*", "Auto" }
            case ArrayCreationExpressionSyntax explicitArray:
            {
                if (explicitArray.Initializer is null) return false;
                if (!IsStringElementType(explicitArray.Type)) return false;
                if (!TryRewriteInitializer(explicitArray, explicitArray.Initializer, out var withElements)) return false;

                // Swap the element type string -> GridSize so overload resolution
                // picks the typed Grid overload.
                var rewrittenArray = (ArrayCreationExpressionSyntax)withElements!;
                var newElementType = SyntaxFactory.IdentifierName("GridSize")
                    .WithTriviaFrom(explicitArray.Type.ElementType);
                var newArrayType = rewrittenArray.Type.WithElementType(newElementType);
                rewritten = rewrittenArray.WithType(newArrayType);
                return true;
            }

            default:
                // Variable, member access, interpolated string, spread, etc.
                return false;
        }
    }

    private static bool TryRewriteInitializer(
        ExpressionSyntax container, InitializerExpressionSyntax initializer, out ExpressionSyntax? rewritten)
    {
        rewritten = null;

        var literals = new List<LiteralExpressionSyntax>();
        foreach (var element in initializer.Expressions)
        {
            if (!TryGetStringLiteral(element, out var literal)) return false;
            literals.Add(literal);
        }

        if (!TryBuildReplacements(literals, out var map)) return false;
        rewritten = container.ReplaceNodes(literals, (original, _) => map[original]);
        return true;
    }

    private static bool TryBuildReplacements(
        List<LiteralExpressionSyntax> literals, out Dictionary<SyntaxNode, SyntaxNode> map)
    {
        map = new Dictionary<SyntaxNode, SyntaxNode>();
        foreach (var literal in literals)
        {
            if (!TryConvertTrack(literal.Token.ValueText, out var gridSize)) return false;
            map[literal] = gridSize!.WithTriviaFrom(literal);
        }
        return true;
    }

    private static bool TryGetStringLiteral(ExpressionSyntax expression, out LiteralExpressionSyntax literal)
    {
        if (expression is LiteralExpressionSyntax { RawKind: (int)SyntaxKind.StringLiteralExpression } stringLiteral)
        {
            literal = stringLiteral;
            return true;
        }

        literal = null!;
        return false;
    }

    /// <summary>
    /// Maps a track string to a <c>GridSize</c> factory expression, mirroring
    /// <c>GridSize.Parse</c>. Returns <see langword="false"/> for any string that
    /// does not map cleanly (so the whole fix is withheld rather than change
    /// runtime behaviour).
    /// </summary>
    private static bool TryConvertTrack(string raw, out ExpressionSyntax? gridSize)
    {
        gridSize = null;
        var trimmed = raw.Trim();
        if (trimmed.Length == 0) return false;

        // "Auto" / "auto" -> GridSize.Auto  (property — no parens)
        if (string.Equals(trimmed, "Auto", System.StringComparison.OrdinalIgnoreCase))
        {
            gridSize = GridSizeAccess("Auto");
            return true;
        }

        // "*" -> GridSize.Star()
        if (trimmed == "*")
        {
            gridSize = Call("Star");
            return true;
        }

        // "<n>*" -> GridSize.Star(n)
        if (trimmed[trimmed.Length - 1] == '*')
        {
            var numericText = trimmed.Substring(0, trimmed.Length - 1).Trim();
            if (numericText.Length == 0) return false;
            if (double.TryParse(numericText, NumberStyles.Float, CultureInfo.InvariantCulture, out var stars) && stars > 0)
            {
                gridSize = Call("Star", numericText);
                return true;
            }
            return false;
        }

        // "<n>" -> GridSize.Px(n)
        if (double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var pixels) && pixels >= 0)
        {
            gridSize = Call("Px", trimmed);
            return true;
        }

        return false;
    }

    private static MemberAccessExpressionSyntax GridSizeAccess(string member) =>
        SyntaxFactory.MemberAccessExpression(
            SyntaxKind.SimpleMemberAccessExpression,
            SyntaxFactory.IdentifierName("GridSize"),
            SyntaxFactory.IdentifierName(member));

    private static ExpressionSyntax Call(string member) =>
        SyntaxFactory.InvocationExpression(GridSizeAccess(member));

    private static ExpressionSyntax Call(string member, string numericLiteralText)
    {
        var argument = SyntaxFactory.Argument(
            (ExpressionSyntax)SyntaxFactory.ParseExpression(numericLiteralText));
        return SyntaxFactory.InvocationExpression(
            GridSizeAccess(member),
            SyntaxFactory.ArgumentList(SyntaxFactory.SingletonSeparatedList(argument)));
    }

    private static bool IsStringElementType(ArrayTypeSyntax arrayType) => arrayType.ElementType switch
    {
        PredefinedTypeSyntax predefined => predefined.Keyword.IsKind(SyntaxKind.StringKeyword),
        IdentifierNameSyntax { Identifier.ValueText: "String" or "string" } => true,
        QualifiedNameSyntax { Right.Identifier.ValueText: "String" } => true,
        _ => false,
    };
}
