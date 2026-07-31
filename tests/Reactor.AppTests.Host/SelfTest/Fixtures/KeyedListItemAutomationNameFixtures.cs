using Microsoft.UI.Reactor.AppTests.Host.SelfTest;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml.Automation.Peers;
using static Microsoft.UI.Reactor.Factories;
using WinXC = Microsoft.UI.Xaml.Controls;

namespace Microsoft.UI.Reactor.AppTests.Host.SelfTest.Fixtures;

// Issue #951 — the UIA Name of a keyed ListView/GridView row.
//
// The oracle here is deliberately the *item* peer, not the container peer:
// WinUI builds each row's UIA node as a ListViewItemDataAutomationPeer /
// GridViewItemDataAutomationPeer constructed from the **data item**, and that
// peer resolves its name as "container peer's name if non-empty, else the data
// item's string representation". Querying
// CreatePeerForElement(lvb.ContainerFromIndex(i)) returns the *container* peer
// and does NOT reproduce the bug — it never consults the data item. The only
// faithful in-process read is CreatePeerForElement(lvb).GetChildren()[i].
//
// The item views below are deliberately **composite** (a stack, not a bare
// TextBlock). A container peer composes a name from plain text at the root of
// the realized template; a bare TextBlock therefore masks the leak entirely,
// which is exactly why the bug survived the existing keyed-list coverage.
internal static class KeyedListItemAutomationNameFixtures
{
    private record Fruit(string Id, string Label);

    private static Element Composite(Fruit f, int index) => HStack(8,
        Border(TextBlock($"{index + 1}")).Size(28, 28),
        TextBlock(f.Label));

    private static IReadOnlyList<Fruit> GuidKeyed(params string[] labels) => labels
        .Select(l => new Fruit(global::System.Guid.NewGuid().ToString("N"), l))
        .ToList();
    /// <summary>Name of the row at <paramref name="index"/> as UIA sees it.</summary>
    private static string ItemPeerName(WinXC.ListViewBase lvb, int index)
    {
        var children = FrameworkElementAutomationPeer.CreatePeerForElement(lvb)?.GetChildren();
        if (children is null || index >= children.Count) return "<no item peer>";
        return children[index].GetName() ?? string.Empty;
    }

    // ── Leak ────────────────────────────────────────────────────────────

    internal class NoRowIdentityLeak(Harness h) : SelfTestFixtureBase(h)
    {
        public override async Task RunAsync()
        {
            var fruits = GuidKeyed("Apples", "Bananas", "Carrots");

            var host = H.CreateHost();
            host.Mount(_ => VStack(
                ListView<Fruit>(fruits, f => f.Id, Composite).Height(140),
                GridView<Fruit>(fruits, f => f.Id, Composite).Height(160)
            ));
            await Harness.Render();
            await Harness.Render();

            var lv = H.FindControl<WinXC.ListView>(_ => true);
            var gv = H.FindControl<WinXC.GridView>(_ => true);
            if (lv is null || gv is null) { H.Check("KLIA_ListsRealized", false); return; }

            // Keys are GUIDs, so a leak is unmistakable: the row's internal
            // identity renders as "Row[<index>]=<key>".
            for (int i = 0; i < 2; i++)
            {
                var lvName = ItemPeerName(lv, i);
                var gvName = ItemPeerName(gv, i);
                Console.WriteLine($"# KLIA leak: lv[{i}]=<{lvName}> gv[{i}]=<{gvName}>");

                H.Check($"KLIA_ListView_Item{i}_NoRowPrefix", !lvName.Contains("Row[", StringComparison.Ordinal));
                H.Check($"KLIA_ListView_Item{i}_NoKeyText", !lvName.Contains(fruits[i].Id, StringComparison.OrdinalIgnoreCase));
                H.Check($"KLIA_GridView_Item{i}_NoRowPrefix", !gvName.Contains("Row[", StringComparison.Ordinal));
                H.Check($"KLIA_GridView_Item{i}_NoKeyText", !gvName.Contains(fruits[i].Id, StringComparison.OrdinalIgnoreCase));
            }
        }
    }

    // ── Differential oracle ─────────────────────────────────────────────

    // Characterization guard, not a regression test for the leak: with a bare
    // TextBlock item view the container composes a name from its plain text, and
    // that composed name outranks the data item's string representation — which
    // is precisely why the leak went unnoticed for so long and why every other
    // fixture here uses a composite item view. What this pins down is that the
    // keyed overload still agrees with the element-array reference for the
    // simple case, so a future change to row naming can't silently desync them.
    internal class MatchesElementArray(Harness h) : SelfTestFixtureBase(h)
    {
        public override async Task RunAsync()
        {
            var labels = new[] { "Apples", "Bananas", "Carrots" };
            var keyed = labels.Select(l => new Fruit(l, l)).ToList();

            var host = H.CreateHost();
            host.Mount(_ => VStack(
                ListView(labels.Select(l => TextBlock(l) as Element).ToArray()).Height(140),
                ListView<Fruit>(keyed, f => f.Id, (f, _) => TextBlock(f.Label)).Height(140)
            ));
            await Harness.Render();
            await Harness.Render();

            var lists = H.FindAllControls<WinXC.ListView>(_ => true);
            if (lists.Count < 2) { H.Check("KLIA_BothListsRealized", false); return; }

            for (int i = 0; i < 2; i++)
            {
                var reference = ItemPeerName(lists[0], i);
                var keyedName = ItemPeerName(lists[1], i);
                Console.WriteLine($"# KLIA diff: array[{i}]=<{reference}> keyed[{i}]=<{keyedName}>");

                H.Check($"KLIA_ElementArray_Item{i}_NamedByContent", reference == labels[i]);
                H.Check($"KLIA_Keyed_Item{i}_MatchesElementArray", keyedName == reference);
            }
        }
    }

    // The real cross-overload differential. Both overloads realize rows through
    // their own container hook, so an author-declared name has to be forwarded in
    // both places or the two DSL shapes disagree about how a row is named. With
    // composite item views neither overload composes a name on its own — the
    // element-array path falls back to the boxed index ("0", "1") and the keyed
    // path to nothing — so agreement here can only come from the forwarding.
    //
    // All four forwarding call sites are exercised: the element-array
    // ListViewHandler and GridViewHandler realize hooks, and the shared keyed
    // realize hook for both control types. Deleting any one of them fails this
    // fixture.
    internal class AuthorNameParityAcrossOverloads(Harness h) : SelfTestFixtureBase(h)
    {
        public override async Task RunAsync()
        {
            var labels = new[] { "Apples", "Bananas", "Carrots" };
            var keyed = GuidKeyed(labels);
            Element[] Array() => labels
                .Select((l, ix) => Composite(new Fruit(l, l), ix).AutomationName($"Fruit {l}") as Element)
                .ToArray();

            var host = H.CreateHost();
            host.Mount(_ => VStack(
                ListView(Array()).Height(140),
                ListView<Fruit>(keyed, f => f.Id,
                    (f, ix) => Composite(f, ix).AutomationName($"Fruit {f.Label}")).Height(140),
                GridView(Array()).Height(160),
                GridView<Fruit>(keyed, f => f.Id,
                    (f, ix) => Composite(f, ix).AutomationName($"Fruit {f.Label}")).Height(160)
            ));
            await Harness.Render();
            await Harness.Render();

            // GridView derives from ListViewBase, not ListView, so the two
            // lookups do not overlap.
            var lists = H.FindAllControls<WinXC.ListView>(_ => true);
            var grids = H.FindAllControls<WinXC.GridView>(_ => true);
            if (lists.Count < 2 || grids.Count < 2)
            {
                H.Check("KLIA_Parity_AllFourRealized", false);
                return;
            }

            for (int i = 0; i < 2; i++)
            {
                var expected = $"Fruit {labels[i]}";
                var arrayList = ItemPeerName(lists[0], i);
                var keyedList = ItemPeerName(lists[1], i);
                var arrayGrid = ItemPeerName(grids[0], i);
                var keyedGrid = ItemPeerName(grids[1], i);
                Console.WriteLine(
                    $"# KLIA parity[{i}]: lv-array=<{arrayList}> lv-keyed=<{keyedList}> " +
                    $"gv-array=<{arrayGrid}> gv-keyed=<{keyedGrid}> expected=<{expected}>");

                H.Check($"KLIA_Parity_ElementArray_Item{i}", arrayList == expected);
                H.Check($"KLIA_Parity_Keyed_Item{i}", keyedList == expected);
                H.Check($"KLIA_Parity_GridElementArray_Item{i}", arrayGrid == expected);
                H.Check($"KLIA_Parity_GridKeyed_Item{i}", keyedGrid == expected);
            }
        }
    }

    // ── Author-declared names ───────────────────────────────────────────

    // A composite row has no plain text at its template root, so WinUI composes
    // no name for it — and an AutomationProperties.Name set on the item view's
    // own root is not consulted for the row either. Reactor forwards it to the
    // generated container so .AutomationName(...) on an item view actually names
    // the row. Without that forwarding these names come back empty.
    internal class AuthorNameReachesItem(Harness h) : SelfTestFixtureBase(h)
    {
        public override async Task RunAsync()
        {
            var fruits = GuidKeyed("Apples", "Bananas", "Carrots");

            var host = H.CreateHost();
            host.Mount(_ => VStack(
                ListView<Fruit>(fruits, f => f.Id,
                    (f, ix) => Composite(f, ix).AutomationName($"Fruit {f.Label}")).Height(140),
                GridView<Fruit>(fruits, f => f.Id,
                    (f, ix) => Composite(f, ix).AutomationName($"Fruit {f.Label}")).Height(160)
            ));
            await Harness.Render();
            await Harness.Render();

            var lv = H.FindControl<WinXC.ListView>(_ => true);
            var gv = H.FindControl<WinXC.GridView>(_ => true);
            if (lv is null || gv is null) { H.Check("KLIA_AuthorName_ListsRealized", false); return; }

            for (int i = 0; i < 2; i++)
            {
                var expected = $"Fruit {fruits[i].Label}";
                var lvName = ItemPeerName(lv, i);
                var gvName = ItemPeerName(gv, i);
                Console.WriteLine($"# KLIA author: lv[{i}]=<{lvName}> gv[{i}]=<{gvName}> expected=<{expected}>");

                H.Check($"KLIA_ListView_Item{i}_UsesAuthorName", lvName == expected);
                H.Check($"KLIA_GridView_Item{i}_UsesAuthorName", gvName == expected);
            }
        }
    }

    // A container outlives the row it was realized for. If the forwarded name
    // were written only at realize time, re-rendering the list with reordered or
    // renamed items would leave each container announcing its previous row —
    // a worse failure than no name at all, because it is confidently wrong.
    internal class AuthorNameTracksUpdates(Harness h) : SelfTestFixtureBase(h)
    {
        public override async Task RunAsync()
        {
            var initial = GuidKeyed("Apples", "Bananas", "Carrots");
            // Same keys, so the diff reuses the realized containers instead of
            // recycling them — this exercises the update path, not the realize path.
            var renamed = initial.Select(f => f with { Label = f.Label + " (organic)" }).ToList();

            var host = H.CreateHost();
            host.Mount(ctx =>
            {
                var (phase, setPhase) = ctx.UseState(0);
                var data = phase == 0 ? initial : renamed;
                return VStack(
                    Button("Rename", () => setPhase(1)),
                    ListView<Fruit>(data, f => f.Id,
                        (f, ix) => Composite(f, ix).AutomationName($"Fruit {f.Label}")).Height(140)
                );
            });
            await Harness.Render();
            await Harness.Render();

            var lv = H.FindControl<WinXC.ListView>(_ => true);
            if (lv is null) { H.Check("KLIA_Update_ListRealized", false); return; }

            H.Check("KLIA_Update_InitialName", ItemPeerName(lv, 0) == "Fruit Apples");

            H.ClickButton("Rename");
            await Harness.Render();
            await Harness.Render();

            var after = ItemPeerName(lv, 0);
            Console.WriteLine($"# KLIA update: after=<{after}>");
            H.Check("KLIA_Update_NameFollowsItem", after == "Fruit Apples (organic)");
        }
    }

    // The mirror of the update case: when the author drops the name, the stale
    // one must not survive on the recycled/reused container.
    internal class AuthorNameClearedWhenRemoved(Harness h) : SelfTestFixtureBase(h)
    {
        public override async Task RunAsync()
        {
            var fruits = GuidKeyed("Apples", "Bananas", "Carrots");

            var host = H.CreateHost();
            host.Mount(ctx =>
            {
                var (named, setNamed) = ctx.UseState(true);
                return VStack(
                    Button("DropName", () => setNamed(false)),
                    ListView<Fruit>(fruits, f => f.Id, (f, ix) =>
                        named ? Composite(f, ix).AutomationName($"Fruit {f.Label}") : Composite(f, ix)).Height(140)
                );
            });
            await Harness.Render();
            await Harness.Render();

            var lv = H.FindControl<WinXC.ListView>(_ => true);
            if (lv is null) { H.Check("KLIA_Clear_ListRealized", false); return; }

            H.Check("KLIA_Clear_NamedFirst", ItemPeerName(lv, 0) == "Fruit Apples");

            H.ClickButton("DropName");
            await Harness.Render();
            await Harness.Render();

            var after = ItemPeerName(lv, 0);
            Console.WriteLine($"# KLIA clear: after=<{after}>");
            H.Check("KLIA_Clear_NoStaleName", after != "Fruit Apples");
            H.Check("KLIA_Clear_NoRowIdentityLeak",
                !after.Contains("Row[", StringComparison.Ordinal)
                && !after.Contains(fruits[0].Id, StringComparison.OrdinalIgnoreCase));
        }
    }
}
