using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Data;
using Microsoft.UI.Reactor.Data.Providers;
using Microsoft.UI.Reactor.Controls;
using Microsoft.UI.Reactor.AppTests.Host.SelfTest;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using static Microsoft.UI.Reactor.Factories;

namespace Microsoft.UI.Reactor.AppTests.Host.SelfTest.Fixtures;

/// <summary>
/// Issue #919 regression fixtures.
///
/// Expanding a virtualized DataGrid row used to change the row's ROOT element type
/// (<c>GridElement</c> → <c>FlexElement</c>). A realized <see cref="ItemsRepeater"/> container
/// cannot be swapped from managed code, so the swap desynced <c>ElementFactory</c>'s realized-row
/// bookkeeping and the NEXT render pass hard-cast a <c>Grid</c> to a <c>FlexPanel</c>
/// (<c>InvalidCastException</c>).
///
/// Two fixtures: the reported DataGrid repro, and the generic framework guarantee that a
/// virtualized row whose root type flips re-realizes instead of crashing.
/// </summary>
internal static class DataGridExpandFixtures
{
    record TestProduct(int Id, string Name, string Category, double Price);

    private static ListDataSource<TestProduct> CreateSource(int count = 200)
    {
        var items = Enumerable.Range(0, count).Select(i => new TestProduct(
            Id: i,
            Name: $"Product {i}",
            Category: i % 3 == 0 ? "A" : "B",
            Price: 10.0 + i * 5
        ));
        return new ListDataSource<TestProduct>(items, p => (RowKey)p.Id);
    }

    private static IReadOnlyList<FieldDescriptor> CreateColumns()
        => new FieldDescriptor[]
        {
            Column<TestProduct>("Id", p => p.Id, width: 60),
            Column<TestProduct>("Name", p => p.Name, editable: true, width: 160),
            Column<TestProduct>("Category", p => p.Category, editable: true, width: 120),
            Column<TestProduct>("Price", p => p.Price, editable: true, format: "C2", width: 100),
        };

    // ── The reported repro ───────────────────────────────────────────

    /// <summary>
    /// Mounts the TestApp's "Advanced Features" configuration (virtualized rows + row detail
    /// template + row editing), expands a row through the live state, and drives several more
    /// render passes — the passes that used to throw.
    ///
    /// Non-vacuous: before the fix the expanded row's element root flipped to a
    /// <c>FlexElement</c> while the realized container stayed a <c>Grid</c>, so the detail pane
    /// was never realized (the row visually vanished) and a follow-up refresh threw
    /// <c>InvalidCastException</c>. Both the detail-content check and the row-height check fail
    /// if the row shell is removed.
    /// </summary>
    internal class ExpandRowKeepsRealizedRow(Harness h) : SelfTestFixtureBase(h)
    {
        public override async Task RunAsync()
        {
            DataGridState<TestProduct>? state = null;
            Action? forceRender = null;

            var host = H.CreateHost();
            host.Mount(ctx =>
            {
                var source = ctx.UseMemo(() => CreateSource());
                var (tick, setTick) = ctx.UseState(0);
                forceRender = () => setTick(tick + 1);

                var grid = DataGrid(
                    source: source,
                    columns: CreateColumns(),
                    rowHeight: 36,
                    editable: true,
                    editMode: EditMode.Row,
                    rowDetailTemplate: (p, key) => VStack(
                        TextBlock($"Detail for {p.Name}"),
                        TextBlock($"Category {p.Category}")
                    )
                );

                grid = grid with { Props = grid.Props with { OnStateReadyInternal = s => state = s } };

                return VStack(TextBlock($"tick {tick}"), grid).Height(420);
            });

            await Harness.Render(600);

            H.Check("DataGridExpand_Mounted", state is not null);
            if (state is null) return;

            H.Check("DataGridExpand_RowsRendered", H.FindTextContaining("Product 0") is not null);

            var repeater = H.FindControl<ItemsRepeater>(_ => true);
            H.Check("DataGridExpand_RepeaterFound", repeater is not null);
            if (repeater is null) return;

            var collapsedRow = repeater.TryGetElement(0) as FrameworkElement;
            var collapsedHeight = collapsedRow?.ActualHeight ?? 0;
            H.Check($"DataGridExpand_CollapsedRowRealized (h={collapsedHeight:F1})",
                collapsedRow is not null && collapsedHeight > 0);

            // Expand row 0 the same way the toggle glyph does.
            state.ExpandRow((RowKey)0);
            await Harness.Render(600);

            // Drive additional render passes — the pass that used to throw InvalidCastException.
            forceRender?.Invoke();
            await Harness.Render(400);
            forceRender?.Invoke();
            await Harness.Render(400);

            H.Check("DataGridExpand_StillExpanded", state.IsExpanded((RowKey)0));

            // The detail pane must be realized inside the still-virtualized row.
            var detailShown = await Harness.WaitFor(
                () => H.FindTextContaining("Detail for Product 0") is not null, maxPasses: 20, perPassMs: 50);
            H.Check("DataGridExpand_DetailRealized", detailShown);

            // ...and the row must actually grow: the fixed-height fast path would pin it to 36px
            // and clip the detail pane.
            var expandedRow = repeater.TryGetElement(0) as FrameworkElement;
            expandedRow?.UpdateLayout();
            var expandedHeight = expandedRow?.ActualHeight ?? 0;
            H.Check($"DataGridExpand_RowGrew (collapsed={collapsedHeight:F1} expanded={expandedHeight:F1})",
                expandedHeight > 36);

            // Other rows keep rendering — the repeater was not left in a broken state.
            H.Check("DataGridExpand_SiblingRowsIntact", H.FindTextContaining("Product 2") is not null);

            // Collapsing returns to the compact row without another type flip.
            state.CollapseRow((RowKey)0);
            await Harness.Render(500);
            forceRender?.Invoke();
            await Harness.Render(400);

            var recollapsed = await Harness.WaitFor(
                () => H.FindTextContaining("Detail for Product 0") is null, maxPasses: 20, perPassMs: 50);
            H.Check("DataGridExpand_DetailRemovedOnCollapse", recollapsed);
            H.Check("DataGridExpand_RowsStillRendered", H.FindTextContaining("Product 0") is not null);
        }
    }

    // ── Framework-level guarantee ────────────────────────────────────

    /// <summary>
    /// A virtualized row whose view builder returns a different ROOT element type for the same key
    /// must end up hosting the new control type instead of crashing. Mounts a
    /// <c>LazyVStack</c> whose builder returns a <c>Grid</c> root in state A and a flex root in
    /// state B, flips the state, and pumps several render passes.
    ///
    /// Non-vacuous: before the fix the realized container stayed a <c>Grid</c> showing the stale
    /// "grid-N" text (and the following refresh threw), so both the FlexPanel check and the
    /// content check fail without the ElementFactory re-realize path.
    /// </summary>
    internal class LazyStackRootTypeFlip(Harness h) : SelfTestFixtureBase(h)
    {
        public override async Task RunAsync()
        {
            Action<bool>? setFlipped = null;
            Action? forceRender = null;

            var items = Enumerable.Range(0, 40).Select(i => i.ToString()).ToList();

            var host = H.CreateHost();
            host.Mount(ctx =>
            {
                var (flipped, setF) = ctx.UseState(false);
                var (tick, setTick) = ctx.UseState(0);
                setFlipped = setF;
                forceRender = () => setTick(tick + 1);

                return VStack(
                    TextBlock($"tick {tick}"),
                    LazyVStack(
                        items,
                        k => k,
                        (item, index) => flipped
                            ? FlexColumn(TextBlock($"flex-{item}")).Height(30)
                            : Grid(
                                new[] { GridSize.Star() },
                                new[] { GridSize.Star() },
                                TextBlock($"grid-{item}").Grid(row: 0, column: 0)
                              ).Height(30)
                    ).Height(300)
                );
            });

            await Harness.Render(500);

            var repeater = H.FindControl<ItemsRepeater>(_ => true);
            H.Check("RootFlip_RepeaterFound", repeater is not null);
            if (repeater is null) return;

            H.Check("RootFlip_InitialGridRoot", repeater.TryGetElement(0) is Grid);
            H.Check("RootFlip_InitialContent", H.FindTextContaining("grid-0") is not null);

            setFlipped?.Invoke(true);
            await Harness.Render(500);

            // Extra passes: the re-realize is dispatcher-deferred, and the second refresh is the
            // one that used to hard-cast the stale Grid to a FlexPanel.
            forceRender?.Invoke();
            await Harness.Render(400);
            forceRender?.Invoke();
            await Harness.Render(400);

            var flippedContent = await Harness.WaitFor(
                () => H.FindTextContaining("flex-0") is not null, maxPasses: 25, perPassMs: 50);
            H.Check("RootFlip_ContentSwapped", flippedContent);

            var realized = repeater.TryGetElement(0);
            H.Check($"RootFlip_RealizedControlSwapped (type={realized?.GetType().Name ?? "null"})",
                realized is Microsoft.UI.Reactor.Layout.FlexPanel);

            H.Check("RootFlip_NoStaleGridContent", H.FindTextContaining("grid-0") is null);

            // Flip back — the reverse transition must work too.
            setFlipped?.Invoke(false);
            await Harness.Render(400);
            forceRender?.Invoke();
            await Harness.Render(400);

            var backContent = await Harness.WaitFor(
                () => H.FindTextContaining("grid-0") is not null, maxPasses: 25, perPassMs: 50);
            H.Check("RootFlip_FlipsBack", backContent);
        }
    }
}
