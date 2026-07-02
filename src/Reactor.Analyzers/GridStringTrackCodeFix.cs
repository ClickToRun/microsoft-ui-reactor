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
using Microsoft.CodeAnalysis.Operations;

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
/// The string→<c>GridSize</c> mapping mirrors the obsolete overload's runtime
/// parser <c>PanelAttachedHooks.ParseColumnDef</c>/<c>ParseRowDef</c> EXACTLY (raw
/// string, exact matches): <c>"*"</c> → <c>GridSize.Star()</c>; <c>"Auto"</c>/<c>"auto"</c>
/// (exact) → <c>GridSize.Auto</c>; a whole-string numeric (incl. surrounding
/// whitespace, which <c>NumberStyles.Float</c> allows) → <c>GridSize.Px(n)</c>; a raw
/// <c>*</c>-suffixed numeric → <c>GridSize.Star(n)</c>. The legacy parser's catch-all
/// is <c>Star(1)</c>; the fix withholds there (and on non-finite/out-of-range) so
/// those keep the <c>CS0618</c> warning rather than risk a silent layout change.
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

            // Bind the columns/rows arguments by parameter (robust to named /
            // reordered args), never by syntactic position — a named call such as
            // `Grid(children: [], columns: […], rows: […])` would otherwise let a
            // non-track argument sit in slot 0/1 and get rewritten.
            if (!TryGetTrackArguments(model, invocation, context.CancellationToken, out var columnsExpr, out var rowsExpr))
                continue;

            // Render `GridSize` with exactly enough qualification to compile at this
            // call site: bare `GridSize` when the namespace is imported, otherwise a
            // qualified name. Mirrors CommandDebounceCodeFix's ToMinimalDisplayString use.
            var gridSizeName = ResolveGridSizeName(model, invocation.SpanStart);

            // Both track arrays must be inline literal arrays whose every element
            // parses to a GridSize. If either can't be rewritten mechanically we
            // offer nothing and let the warning stand (spec 060 §4.5).
            if (!TryRewriteTrackArray(columnsExpr!, gridSizeName, out var newColumns)) continue;
            if (!TryRewriteTrackArray(rowsExpr!, gridSizeName, out var newRows)) continue;

            var capturedColumns = columnsExpr!;
            var capturedRows = rowsExpr!;
            context.RegisterCodeFix(
                CodeAction.Create(
                    "Use typed GridSize tracks",
                    ct =>
                    {
                        var newInvocation = invocation.ReplaceNodes(
                            new SyntaxNode[] { capturedColumns, capturedRows },
                            (original, _) =>
                                ReferenceEquals(original, capturedColumns) ? newColumns! : newRows!);

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
    /// Resolves the syntax of the arguments bound to the <c>columns</c> and
    /// <c>rows</c> parameters via the invocation operation, so named / reordered
    /// arguments map correctly (never assume positional slot 0/1).
    /// </summary>
    private static bool TryGetTrackArguments(
        SemanticModel model, InvocationExpressionSyntax invocation, CancellationToken ct,
        out ExpressionSyntax? columnsExpr, out ExpressionSyntax? rowsExpr)
    {
        columnsExpr = null;
        rowsExpr = null;

        if (model.GetOperation(invocation, ct) is not IInvocationOperation operation) return false;

        foreach (var argument in operation.Arguments)
        {
            if (argument.Syntax is not ArgumentSyntax argumentSyntax) continue;
            switch (argument.Parameter?.Name)
            {
                case "columns": columnsExpr = argumentSyntax.Expression; break;
                case "rows": rowsExpr = argumentSyntax.Expression; break;
            }
        }

        return columnsExpr is not null && rowsExpr is not null;
    }

    /// <summary>
    /// The shortest name for <c>Microsoft.UI.Reactor.GridSize</c> that is
    /// unambiguous at <paramref name="position"/> — bare <c>GridSize</c> when the
    /// namespace is in scope, otherwise a qualified form so the emitted fix always
    /// compiles (even when the call site imported only
    /// <c>using static Microsoft.UI.Reactor.Factories;</c>).
    /// </summary>
    private static string ResolveGridSizeName(SemanticModel model, int position)
    {
        var gridSize = model.Compilation.GetTypeByMetadataName("Microsoft.UI.Reactor.GridSize");
        return gridSize is null ? "GridSize" : gridSize.ToMinimalDisplayString(model, position);
    }

    /// <summary>
    /// Rewrites an inline literal track array to the typed <c>GridSize[]</c> form
    /// in place (preserving separators/trivia). Returns <see langword="false"/>
    /// when the argument is not an inline literal array or any element cannot be
    /// mapped to a <c>GridSize</c>.
    /// </summary>
    private static bool TryRewriteTrackArray(ExpressionSyntax expr, string gridSizeName, out ExpressionSyntax? rewritten)
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

                if (!TryBuildReplacements(literals, gridSizeName, out var map)) return false;
                rewritten = collection.ReplaceNodes(literals, (original, _) => map[original]);
                return true;
            }

            // Implicitly typed array: new[] { "*", "Auto" }
            case ImplicitArrayCreationExpressionSyntax implicitArray:
                return TryRewriteInitializer(implicitArray, implicitArray.Initializer, gridSizeName, out rewritten);

            // Explicitly typed array: new string[] { "*", "Auto" }
            case ArrayCreationExpressionSyntax explicitArray:
            {
                if (explicitArray.Initializer is null) return false;
                if (!IsStringElementType(explicitArray.Type)) return false;
                if (!TryRewriteInitializer(explicitArray, explicitArray.Initializer, gridSizeName, out var withElements)) return false;

                // Swap the element type string -> GridSize so overload resolution
                // picks the typed Grid overload.
                var rewrittenArray = (ArrayCreationExpressionSyntax)withElements!;
                var newElementType = SyntaxFactory.ParseTypeName(gridSizeName)
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
        ExpressionSyntax container, InitializerExpressionSyntax initializer, string gridSizeName, out ExpressionSyntax? rewritten)
    {
        rewritten = null;

        var literals = new List<LiteralExpressionSyntax>();
        foreach (var element in initializer.Expressions)
        {
            if (!TryGetStringLiteral(element, out var literal)) return false;
            literals.Add(literal);
        }

        if (!TryBuildReplacements(literals, gridSizeName, out var map)) return false;
        rewritten = container.ReplaceNodes(literals, (original, _) => map[original]);
        return true;
    }

    private static bool TryBuildReplacements(
        List<LiteralExpressionSyntax> literals, string gridSizeName, out Dictionary<SyntaxNode, SyntaxNode> map)
    {
        map = new Dictionary<SyntaxNode, SyntaxNode>();
        foreach (var literal in literals)
        {
            if (!TryConvertTrack(literal.Token.ValueText, gridSizeName, out var gridSize)) return false;
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
    /// Maps a track string to a <c>GridSize</c> factory expression, mirroring the
    /// obsolete overload's runtime parser <c>ParseColumnDef</c>/<c>ParseRowDef</c>
    /// exactly (raw string, exact <c>"*"</c>/<c>"Auto"</c>/<c>"auto"</c>). Returns
    /// <see langword="false"/> for anything that would hit the legacy <c>Star(1)</c>
    /// catch-all or is non-finite/out-of-range, so the whole fix is withheld rather than
    /// change runtime behaviour. Numeric weights/pixels are re-emitted from the parsed
    /// value in round-trip invariant form, so lenient-but-not-C#-literal inputs
    /// (e.g. <c>"5."</c>) still yield a compiling literal (<c>5</c>).
    /// </summary>
    private static bool TryConvertTrack(string raw, string gridSizeName, out ExpressionSyntax? gridSize)
    {
        gridSize = null;

        // Mirror the obsolete overload's ACTUAL runtime parser
        // (PanelAttachedHooks.ParseColumnDef/ParseRowDef) EXACTLY, so the rewrite can
        // never change layout: switch on the RAW string, match "*"/"Auto"/"auto"
        // exactly (no Trim, no extra casing), then the whole-string numeric parse, then
        // the raw '*' suffix. The legacy catch-all is Star(1); we WITHHOLD there (and on
        // non-finite / out-of-range) so those keep the CS0618 warning rather than risk a
        // silent change. Note NumberStyles.Float already allows surrounding whitespace,
        // so a faithful " 200 " still converts to Px(200) — matching the legacy parser.

        // "*" -> GridSize.Star()
        if (raw == "*")
        {
            gridSize = Call(gridSizeName, "Star");
            return true;
        }

        // "Auto" / "auto" (exact) -> GridSize.Auto  (property — no parens)
        if (raw == "Auto" || raw == "auto")
        {
            gridSize = GridSizeAccess(gridSizeName, "Auto");
            return true;
        }

        // "<n>" -> GridSize.Px(n)
        if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var pixels)
            && pixels >= 0 && IsFinite(pixels))
        {
            gridSize = Call(gridSizeName, "Px", FormatNumber(pixels));
            return true;
        }

        // "<n>*" -> GridSize.Star(n). The legacy parser tests raw.EndsWith('*'), so a
        // trailing space defeats it (and it falls back to Star(1)); match that exactly by
        // testing the RAW final char rather than a trimmed string.
        if (raw.Length > 0 && raw[raw.Length - 1] == '*'
            && double.TryParse(raw.Substring(0, raw.Length - 1), NumberStyles.Float, CultureInfo.InvariantCulture, out var stars)
            && stars > 0 && IsFinite(stars))
        {
            gridSize = Call(gridSizeName, "Star", FormatNumber(stars));
            return true;
        }

        // Legacy fallback is Star(1); withhold so the CS0618 warning stands (safe — no
        // semantic change, the author converts by hand).
        return false;
    }

    // NumberStyles.Float accepts "Infinity"/"-Infinity" and overflows (e.g. "1e400")
    // to ±Infinity; "R" would then emit "Infinity", which is not a valid C# numeric
    // literal (and GridLength rejects non-finite anyway). Withhold the fix instead.
    // (netstandard2.0 has no double.IsFinite.)
    private static bool IsFinite(double value) => !double.IsInfinity(value) && !double.IsNaN(value);

    /// <summary>Round-trip invariant form that is always a valid C# numeric literal.</summary>
    private static string FormatNumber(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    private static MemberAccessExpressionSyntax GridSizeAccess(string gridSizeName, string member) =>
        SyntaxFactory.MemberAccessExpression(
            SyntaxKind.SimpleMemberAccessExpression,
            SyntaxFactory.ParseExpression(gridSizeName),
            SyntaxFactory.IdentifierName(member));

    private static ExpressionSyntax Call(string gridSizeName, string member) =>
        SyntaxFactory.InvocationExpression(GridSizeAccess(gridSizeName, member));

    private static ExpressionSyntax Call(string gridSizeName, string member, string numericLiteralText)
    {
        var argument = SyntaxFactory.Argument(SyntaxFactory.ParseExpression(numericLiteralText));
        return SyntaxFactory.InvocationExpression(
            GridSizeAccess(gridSizeName, member),
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
