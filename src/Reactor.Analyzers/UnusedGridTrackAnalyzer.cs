using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Microsoft.UI.Reactor.Analyzers;

/// <summary>
/// <c>REACTOR_GRID_001</c> — flags a declared <c>Grid</c> track (a <see cref="GridSize"/>
/// in the <c>columns</c>/<c>rows</c> array of the typed
/// <c>Factories.Grid(GridSize[], GridSize[], params Element?[])</c> factory) that no child
/// occupies: the "unused column"/"unused row" symptom (layout.md:555, spec 060 §12).
/// </summary>
/// <remarks>
/// <para>
/// A child's cell is the outermost <c>.Grid(row:, column:, rowSpan:, columnSpan:)</c> modifier
/// in its chain (<c>GridExtensions.Grid</c>, GridExtensions.cs); a child with no <c>.Grid()</c>
/// defaults to <c>(row 0, column 0)</c> (<c>GridAttached</c>, Element.cs). A track index that
/// is covered by no child's [row..row+rowSpan-1] × [column..column+columnSpan-1] range is
/// unused. Intent-heavy — ship at Warning, <b>no auto-fix</b> (the author may want to remove the
/// track or place a child there).
/// </para>
/// <para>
/// <b>False-positive discipline (the rule only fires when it can prove a track is unused).</b>
/// Because occupancy is a negative claim ("no child is here"), the analyzer bails — reports
/// nothing for the whole grid — the moment any child's placement is not statically visible in
/// the same call:
/// <list type="bullet">
/// <item>a bare variable / parameter / field child (e.g. <c>titleBar</c>) may have been placed
/// with <c>.Grid(...)</c> elsewhere, so its cell is unknown;</item>
/// <item>a conditional child (<c>cond ? a.Grid(col:2) : b.Grid(col:2)</c>) has a
/// branch-dependent cell;</item>
/// <item>a non-constant placement arg (<c>.Grid(column: i)</c> / <c>columnSpan: n</c>) could
/// cover the very track we would flag;</item>
/// <item>a spread/variable <c>columns</c>/<c>rows</c> array or a children array we cannot
/// enumerate hides both the track count and the placements.</item>
/// </list>
/// The remaining accepted limitation: a helper that hides a <c>.Grid(...)</c> inside its body
/// (<c>Cell(r, c, e) =&gt; e.Grid(r, c)</c>) is treated as the default <c>(0,0)</c> at the call
/// site — an intentional false positive the author can suppress, matching the documented
/// "a child with no explicit column is at column 0" model.
/// </para>
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class UnusedGridTrackAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "REACTOR_GRID_001";

    private const string FactoriesType = "Microsoft.UI.Reactor.Factories";
    private const string GridExtensionsType = "Microsoft.UI.Reactor.GridExtensions";
    private const string GridSizeType = "Microsoft.UI.Reactor.GridSize";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        "Grid declares a track that no child occupies",
        "This Grid declares {0} {1} but no child is placed in it. Remove the unused track or place a child there.",
        "Reactor.Layout",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description:
            "The typed Grid factory sizes its tracks from the columns/rows arrays, and each child " +
            "picks a cell via the .Grid(row:, column:, rowSpan:, columnSpan:) modifier (a child " +
            "with no .Grid() defaults to row 0, column 0). A declared track that no child's " +
            "row/column range covers renders empty — usually a leftover track after a child was " +
            "removed, or a child that was never placed. The analyzer only fires when every " +
            "child's placement is statically visible in the same call: it stays silent on grids " +
            "with variable/conditional children, dynamic (non-constant) placement, or " +
            "spread/variable track arrays, because it cannot then prove the track is unused.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeInvocation, SyntaxKind.InvocationExpression);
    }

    private static void AnalyzeInvocation(SyntaxNodeAnalysisContext context)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;

        // Cheap syntactic gate — the callee is named "Grid" and has at least columns + rows.
        if (GetInvokedSimpleName(invocation.Expression) != "Grid")
            return;
        if (invocation.ArgumentList.Arguments.Count < 2)
            return;

        // Semantic confirm — the typed Reactor Grid factory (not the .Grid modifier, GridView,
        // a user's Grid, or the obsolete string-track overload).
        if (context.SemanticModel.GetOperation(invocation, context.CancellationToken) is not IInvocationOperation op)
            return;
        if (!IsTypedGridFactory(op.TargetMethod))
            return;

        IOperation? columnsVal = null;
        IOperation? rowsVal = null;
        IOperation? childrenVal = null;
        foreach (var arg in op.Arguments)
        {
            if (arg.Parameter is null)
                continue;
            if (arg.Parameter.IsParams)
                childrenVal = arg.Value;
            else if (arg.Parameter.Name == "columns")
                columnsVal = arg.Value;
            else if (arg.Parameter.Name == "rows")
                rowsVal = arg.Value;
        }

        if (childrenVal is null)
            return;

        // Children must be an inline array we can fully enumerate. A variable/opaque children
        // array means we cannot see every placement → cannot prove any track unused.
        if (!TryGetChildOperations(childrenVal, out var children))
            return;
        if (children.Count == 0)
            return;

        // Resolve every child's placement. A single opaque child aborts the whole grid.
        var occupiedRows = new HashSet<int>();
        var occupiedCols = new HashSet<int>();
        foreach (var child in children)
        {
            var placement = ResolvePlacement(child);
            if (placement.Kind == PlacementKind.Bail)
                return;
            if (placement.Kind == PlacementKind.Skip)
                continue;

            for (var r = placement.RowStart; r <= placement.RowEnd; r++)
                occupiedRows.Add(r);
            for (var c = placement.ColStart; c <= placement.ColEnd; c++)
                occupiedCols.Add(c);
        }

        if (TryGetTrackLocations(columnsVal, out var columnLocations))
            ReportUnusedTracks(context, columnLocations, occupiedCols, "column");
        if (TryGetTrackLocations(rowsVal, out var rowLocations))
            ReportUnusedTracks(context, rowLocations, occupiedRows, "row");
    }

    private static void ReportUnusedTracks(
        SyntaxNodeAnalysisContext context,
        IReadOnlyList<Location> trackLocations,
        HashSet<int> occupied,
        string axis)
    {
        for (var i = 0; i < trackLocations.Count; i++)
        {
            if (!occupied.Contains(i))
                context.ReportDiagnostic(Diagnostic.Create(Rule, trackLocations[i], axis, i));
        }
    }

    // ── Grid factory / modifier recognition ────────────────────────────────

    private static bool IsTypedGridFactory(IMethodSymbol? method)
    {
        if (method is null || method.Name != "Grid")
            return false;
        if (method.ContainingType?.ToDisplayString() != FactoriesType)
            return false;

        var ps = method.Parameters;
        if (ps.Length < 3)
            return false;
        if (ps[0].Name != "columns" || ps[1].Name != "rows" || !ps[ps.Length - 1].IsParams)
            return false;

        // Typed overload only — the obsolete string[] overload has its own CS0618 code fix.
        return ps[0].Type is IArrayTypeSymbol { ElementType: INamedTypeSymbol element }
            && element.ToDisplayString() == GridSizeType;
    }

    private static bool IsGridModifier(IMethodSymbol? method)
    {
        var m = method?.ReducedFrom ?? method;
        return m is not null
            && m.Name == "Grid"
            && m.ContainingType?.ToDisplayString() == GridExtensionsType;
    }

    // ── Child placement resolution ─────────────────────────────────────────

    private enum PlacementKind
    {
        /// <summary>A statically known cell range.</summary>
        Known,

        /// <summary>A <c>null</c> child (filtered at runtime) — occupies nothing.</summary>
        Skip,

        /// <summary>Placement not provable — abort the whole grid.</summary>
        Bail,
    }

    private readonly struct Placement
    {
        public readonly PlacementKind Kind;
        public readonly int RowStart;
        public readonly int RowEnd;
        public readonly int ColStart;
        public readonly int ColEnd;

        private Placement(PlacementKind kind, int rowStart, int rowEnd, int colStart, int colEnd)
        {
            Kind = kind;
            RowStart = rowStart;
            RowEnd = rowEnd;
            ColStart = colStart;
            ColEnd = colEnd;
        }

        public static readonly Placement Bail = new(PlacementKind.Bail, 0, 0, 0, 0);
        public static readonly Placement Skip = new(PlacementKind.Skip, 0, 0, 0, 0);
        public static readonly Placement Default = Cell(0, 0, 1, 1);

        public static Placement Cell(int row, int column, int rowSpan, int columnSpan) =>
            new(PlacementKind.Known, row, row + rowSpan - 1, column, column + columnSpan - 1);
    }

    /// <summary>
    /// Walk a child's fluent chain to its outermost <c>.Grid(...)</c> placement (the last one
    /// applied wins), or to the static factory at its root (default <c>(0,0)</c>). Anything else —
    /// a variable/field reference, a conditional, a raw object creation, or a non-constant
    /// placement argument — is not provable and returns <see cref="Placement.Bail"/>.
    /// </summary>
    private static Placement ResolvePlacement(IOperation childOperation)
    {
        var current = Unwrap(childOperation);

        while (true)
        {
            if (current is IInvocationOperation invocation)
            {
                if (IsGridModifier(invocation.TargetMethod))
                    return ReadGridPlacement(invocation);

                var receiver = invocation.Instance;
                if (receiver is null
                    && invocation.TargetMethod.IsExtensionMethod
                    && invocation.Arguments.Length > 0)
                {
                    // Extension methods surface in unreduced form: the receiver is argument 0.
                    receiver = invocation.Arguments[0].Value;
                }

                if (receiver is null)
                {
                    // No receiver → a static factory root (Text(...), Grid(...), Component<..>(..))
                    // with no .Grid() above it → the framework default cell.
                    return Placement.Default;
                }

                current = Unwrap(receiver);
                continue;
            }

            if (current is ILiteralOperation { ConstantValue: { HasValue: true, Value: null } })
                return Placement.Skip;

            // Variable/parameter/field/property reference, conditional, object creation, etc. —
            // the cell is not provable from this call site.
            return Placement.Bail;
        }
    }

    private static Placement ReadGridPlacement(IInvocationOperation gridModifier)
    {
        int row = 0, column = 0, rowSpan = 1, columnSpan = 1;

        foreach (var arg in gridModifier.Arguments)
        {
            // Omitted optionals keep their defaults; only explicit args override. The extension
            // receiver ("el") and any other parameter are ignored.
            if (arg.ArgumentKind != ArgumentKind.Explicit)
                continue;

            switch (arg.Parameter?.Name)
            {
                case "row":
                    if (!TryGetConstInt(arg.Value, out row)) return Placement.Bail;
                    break;
                case "column":
                    if (!TryGetConstInt(arg.Value, out column)) return Placement.Bail;
                    break;
                case "rowSpan":
                    if (!TryGetConstInt(arg.Value, out rowSpan)) return Placement.Bail;
                    break;
                case "columnSpan":
                    if (!TryGetConstInt(arg.Value, out columnSpan)) return Placement.Bail;
                    break;
            }
        }

        // Out-of-range indices / spans are unusual and ambiguous — do not risk a false claim.
        if (row < 0 || column < 0 || rowSpan < 1 || columnSpan < 1)
            return Placement.Bail;

        return Placement.Cell(row, column, rowSpan, columnSpan);
    }

    // ── Track array enumeration ────────────────────────────────────────────

    private static bool TryGetChildOperations(IOperation childrenValue, out IReadOnlyList<IOperation> children)
    {
        // Both the params-expanded form (Grid(cols, rows, a, b)) and an explicit inline array
        // (Grid(cols, rows, new Element[] { a, b })) surface as an array creation with an
        // initializer we can enumerate. A variable array, Array.Empty<>(), or a spread does not.
        if (Unwrap(childrenValue) is IArrayCreationOperation { Initializer: { } initializer })
        {
            children = initializer.ElementValues;
            return true;
        }

        children = System.Array.Empty<IOperation>();
        return false;
    }

    private static bool TryGetTrackLocations(IOperation? trackValue, out IReadOnlyList<Location> locations)
    {
        if (trackValue is not null)
        {
            switch (Unwrap(trackValue).Syntax)
            {
                case CollectionExpressionSyntax collection:
                    // [GridSize.Auto, GridSize.Star()] — bail if any element is a spread (..x).
                    if (collection.Elements.All(e => e is ExpressionElementSyntax))
                    {
                        locations = collection.Elements.Select(e => e.GetLocation()).ToArray();
                        return true;
                    }
                    break;

                case ArrayCreationExpressionSyntax { Initializer: { } arrayInit }:
                    locations = arrayInit.Expressions.Select(e => e.GetLocation()).ToArray();
                    return true;

                case ImplicitArrayCreationExpressionSyntax { Initializer: { } implicitInit }:
                    locations = implicitInit.Expressions.Select(e => e.GetLocation()).ToArray();
                    return true;
            }
        }

        locations = System.Array.Empty<Location>();
        return false;
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static IOperation Unwrap(IOperation operation)
    {
        while (true)
        {
            switch (operation)
            {
                case IConversionOperation conversion:
                    operation = conversion.Operand;
                    continue;
                case IParenthesizedOperation parenthesized:
                    operation = parenthesized.Operand;
                    continue;
                default:
                    return operation;
            }
        }
    }

    private static bool TryGetConstInt(IOperation operation, out int value)
    {
        var constant = operation.ConstantValue;
        if (constant.HasValue && constant.Value is int i)
        {
            value = i;
            return true;
        }

        value = 0;
        return false;
    }

    private static string? GetInvokedSimpleName(ExpressionSyntax expression) => expression switch
    {
        IdentifierNameSyntax id => id.Identifier.ValueText,
        GenericNameSyntax generic => generic.Identifier.ValueText,
        MemberAccessExpressionSyntax member => member.Name switch
        {
            GenericNameSyntax genericMember => genericMember.Identifier.ValueText,
            { } simple => simple.Identifier.ValueText,
        },
        MemberBindingExpressionSyntax binding => binding.Name.Identifier.ValueText,
        _ => null,
    };
}
