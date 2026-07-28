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
}
