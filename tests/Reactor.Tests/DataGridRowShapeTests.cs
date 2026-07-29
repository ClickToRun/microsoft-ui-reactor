using Microsoft.UI.Reactor.Controls;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Data;
using Xunit;
using static Microsoft.UI.Reactor.Factories;

namespace Microsoft.UI.Reactor.Tests;

/// <summary>
/// Issue #919 regression cover: a DataGrid row's ROOT element type must never change between
/// renders. Rows are virtualized through ItemsRepeater, whose realized containers cannot be
/// swapped from managed code, so a root-type flip (the old Grid → FlexPanel wrap on expand)
/// desynced the reconciler's realized-row bookkeeping and threw InvalidCastException on the
/// following render pass.
/// </summary>
public class DataGridRowShapeTests
{
    private record TestItem(int Id, string Name, double Score);

    private sealed class TestDataSource : IDataSource<TestItem>
    {
        private readonly List<TestItem> _items;
        public TestDataSource(int count = 5)
            => _items = Enumerable.Range(1, count)
                .Select(i => new TestItem(i, $"Item {i}", i * 10.0)).ToList();

        public Task<DataPage<TestItem>> GetPageAsync(DataRequest request, CancellationToken ct = default)
            => Task.FromResult(new DataPage<TestItem>(_items, TotalCount: _items.Count));

        public RowKey GetRowKey(TestItem item) => new(item.Id.ToString());
        public DataSourceCapabilities Capabilities => DataSourceCapabilities.None;
    }

    private static readonly FieldDescriptor[] Columns =
    [
        new FieldDescriptor
        {
            Name = "Id",
            FieldType = typeof(int),
            GetValue = obj => ((TestItem)obj).Id,
            IsReadOnly = true,
            Width = 60,
        },
        new FieldDescriptor
        {
            Name = "Name",
            FieldType = typeof(string),
            GetValue = obj => ((TestItem)obj).Name,
            SetValue = (obj, val) => ((TestItem)obj) with { Name = (string)(val ?? "") },
            Width = 160,
        },
    ];

    private static async Task<DataGridState<TestItem>> LoadedState(SelectionMode mode = SelectionMode.None)
    {
        var state = new DataGridState<TestItem>(new TestDataSource(), Columns, mode);
        await state.LoadDataAsync();
        return state;
    }

    private static DataGridElement<TestItem> Grid(
        bool withDetail = false, bool editable = false, bool asyncCommit = false)
        => new()
        {
            Source = new TestDataSource(),
            Columns = Columns,
            RowHeight = 36,
            EstimatedRowHeight = 44,
            Editable = editable,
            OnRowChanged = asyncCommit ? (_, _) => Task.CompletedTask : null,
            RowDetailTemplate = withDetail
                ? (item, key) => TextBlock($"detail for {item.Name} ({key.Value})")
                : null,
        };

    private static Element BuildRow(DataGridState<TestItem> state, DataGridElement<TestItem> el, int index = 0)
        => DataGridComponent<TestItem>.BuildRowForTests(index, state, Columns, el, new TypeRegistry());

    private static int ChildCount(Element row) => row switch
    {
        StackElement s => s.Children.Length,
        _ => 0,
    };

    // ── Root-type stability ──────────────────────────────────────────

    [Fact]
    public async Task ExpandingRow_DoesNotChangeRootElementType()
    {
        var state = await LoadedState();
        var el = Grid(withDetail: true);
        var key = new RowKey(state.GetRowKeyAt(0)!);

        var collapsed = BuildRow(state, el);
        state.ExpandRow(key);
        var expanded = BuildRow(state, el);

        // The realized container is reused across this transition, so the root types must match.
        Assert.Equal(collapsed.GetType(), expanded.GetType());
        // ...and the transition must actually have done something (guards against a row builder
        // that silently stopped emitting the detail pane).
        Assert.Equal(ChildCount(collapsed) + 1, ChildCount(expanded));
    }

    [Fact]
    public async Task CollapsingRow_DoesNotChangeRootElementType()
    {
        var state = await LoadedState();
        var el = Grid(withDetail: true);
        var key = new RowKey(state.GetRowKeyAt(0)!);

        state.ExpandRow(key);
        var expanded = BuildRow(state, el);
        state.CollapseRow(key);
        var collapsed = BuildRow(state, el);

        Assert.Equal(expanded.GetType(), collapsed.GetType());
        Assert.Equal(ChildCount(expanded) - 1, ChildCount(collapsed));
    }

    [Fact]
    public async Task DetailCapableGrid_UsesTheSameRootTypeForEveryRow()
    {
        var state = await LoadedState();
        var el = Grid(withDetail: true);
        state.ExpandRow(new RowKey(state.GetRowKeyAt(1)!));

        // Row 1 is expanded, rows 0 and 2 are not — every realized container must still be
        // interchangeable, because ItemsRepeater recycles containers across row indices.
        var types = new[] { BuildRow(state, el, 0), BuildRow(state, el, 1), BuildRow(state, el, 2) }
            .Select(r => r.GetType())
            .Distinct()
            .ToArray();

        Assert.Single(types);
    }

    [Fact]
    public async Task EditableGrid_RowRootTypeSurvivesRowEdit()
    {
        var state = await LoadedState();
        var el = Grid(editable: true) with { EditMode = EditMode.Row };

        var idle = BuildRow(state, el);
        state.BeginRowEdit(0);
        var editing = BuildRow(state, el);

        Assert.Equal(idle.GetType(), editing.GetType());
    }

    // ── The shell is opt-in: plain grids keep the bare Grid root ─────

    [Fact]
    public async Task PlainGrid_KeepsBareGridRoot()
    {
        var state = await LoadedState();

        var row = BuildRow(state, Grid());

        // No row details, not editable, no async commit — nothing can ever grow this row, so it
        // must NOT pay for the stability shell.
        Assert.IsType<GridElement>(row);
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public async Task GrowableGrid_WrapsRowsInAStableShell(bool detail, bool editable, bool asyncCommit)
    {
        var state = await LoadedState();

        var row = BuildRow(state, Grid(withDetail: detail, editable: editable, asyncCommit: asyncCommit));

        Assert.IsType<StackElement>(row);
    }

    // ── Virtualization mode ─────────────────────────────────────────

    private static VirtualListElement DataRowsProps(DataGridState<TestItem> state, DataGridElement<TestItem> el)
    {
        var rows = DataGridComponent<TestItem>.BuildDataRowsForTests(state, Columns, el, new TypeRegistry());
        return Assert.IsType<ComponentElement<VirtualListElement>>(rows).Props;
    }

    [Fact]
    public async Task CollapsedDetailGrid_KeepsTheFixedHeightFastPath()
    {
        var state = await LoadedState();

        var props = DataRowsProps(state, Grid(withDetail: true));

        Assert.Equal(36d, props.ItemHeight);
    }

    [Fact]
    public async Task ExpandedRow_SwitchesToMeasuredRowHeights()
    {
        var state = await LoadedState();
        var el = Grid(withDetail: true);

        state.ExpandRow(new RowKey(state.GetRowKeyAt(0)!));
        var props = DataRowsProps(state, el);

        // A fixed height would pin the expanded row to the collapsed row height and clip its
        // detail pane, so the grid must ask the virtualizer to measure rows instead.
        Assert.Null(props.ItemHeight);
        Assert.Equal(36d, props.EstimatedItemHeight);
    }

    [Fact]
    public async Task PlainGrid_NeverLeavesTheFixedHeightFastPath()
    {
        var state = await LoadedState();

        var props = DataRowsProps(state, Grid());

        Assert.Equal(36d, props.ItemHeight);
    }

    // ── Row height across the expand transition (issue #919 pr-review M4) ──

    private static Element FirstChild(Element row)
        => Assert.IsType<StackElement>(row).Children[0];

    [Fact]
    public async Task ShellRow_PinsRowHeightOnTheInnerGrid()
    {
        var state = await LoadedState();

        // The shell itself must stay unconstrained so an expanded row can grow,
        // but the row grid inside it carries the author's RowHeight.
        var row = BuildRow(state, Grid(withDetail: true));

        Assert.Null(Assert.IsType<StackElement>(row).Modifiers?.Height);
        Assert.Equal(36d, FirstChild(row).Modifiers?.Height);
    }

    [Fact]
    public async Task CollapsedSibling_KeepsRowHeightWhileAnotherRowIsExpanded()
    {
        var state = await LoadedState();
        var el = Grid(withDetail: true);

        // Expanding row 0 drops VirtualList's fixed-height stamp for the WHOLE
        // list (ItemHeight goes null so the detail pane isn't clipped). Row 1 is
        // still collapsed, so it must keep its height from the inner grid or the
        // entire list visibly reflows when one row opens.
        state.ExpandRow(new RowKey(state.GetRowKeyAt(0)!));
        Assert.Null(DataRowsProps(state, el).ItemHeight);

        var collapsedSibling = BuildRow(state, el, index: 1);

        Assert.Equal(36d, FirstChild(collapsedSibling).Modifiers?.Height);
    }

    [Fact]
    public async Task RowWithoutAnAuthorHeight_LeavesTheInnerGridUnconstrained()
    {
        var state = await LoadedState();
        var el = Grid(withDetail: true) with { RowHeight = null };

        var row = BuildRow(state, el);

        // Guards against unconditionally stamping a height (e.g. a default) —
        // an author who omits RowHeight gets measured rows.
        Assert.Null(FirstChild(row).Modifiers?.Height);
    }

    // ── Shell slot identity (issue #919 pr-review M5) ────────────────

    [Fact]
    public async Task ShellSlots_CarryStableKeysAcrossOptionalChildren()
    {
        var state = await LoadedState();
        var el = Grid(withDetail: true);
        var key = new RowKey(state.GetRowKeyAt(0)!);

        state.ExpandRow(key);
        var expanded = Assert.IsType<StackElement>(BuildRow(state, el));

        // Without keys the reconciler matches these positionally, so a
        // validation summary or commit-error bar appearing above the detail pane
        // would shift it a slot, diff it against a TextBlock and remount the
        // whole detail subtree — losing any state it holds.
        var keys = expanded.Children.Select(c => c.Key).ToArray();
        Assert.Equal(new[] { "row", "detail" }, keys);
    }

    [Fact]
    public async Task DetailSlotKey_IsUnchangedByTheRowIndex()
    {
        var state = await LoadedState();
        var el = Grid(withDetail: true);

        state.ExpandRow(new RowKey(state.GetRowKeyAt(0)!));
        state.ExpandRow(new RowKey(state.GetRowKeyAt(1)!));

        // Slot keys are per-shell, not per-item: the row's own identity already
        // comes from ElementFactory's item key. Rotating them per row would make
        // every row its own reuse shape.
        var first = Assert.IsType<StackElement>(BuildRow(state, el, index: 0));
        var second = Assert.IsType<StackElement>(BuildRow(state, el, index: 1));

        var firstKeys = first.Children.Select(c => c.Key).ToArray();
        Assert.All(firstKeys, k => Assert.False(string.IsNullOrEmpty(k)));
        Assert.Equal(firstKeys, second.Children.Select(c => c.Key).ToArray());
    }
}
