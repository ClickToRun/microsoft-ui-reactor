using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Reactor;            // Optional
using Microsoft.UI.Reactor.Wrappers;   // [GenerateReactorWrapper], [WrapControlled]
using WinUI = Microsoft.UI.Xaml.Controls;
using static Microsoft.UI.Reactor.Factories;

namespace Microsoft.UI.Reactor.AppTests.Host.SelfTest.Fixtures;

// Spec 058 §14 (P4) — items-control parity proof. A control exposing a public
// `Items` collection (ItemsControl-derived: ListBox) gets a generated ItemsHost
// children strategy + a `params object[] items` factory. SelectedIndex is a
// controlled prop wired to SelectionChanged (read-back). This is the generated
// analogue of the hand-written ListBoxDescriptor's
// `Children = new ItemsHost<…>(GetItems: e => e.Items, GetCollection: c => c.Items)`.
[GenerateReactorWrapper(typeof(WinUI.ListBox))]
[WrapControlled("SelectedIndex", Events = new[] { "SelectionChanged" })]
internal partial record ListBoxWrapperElement;

/// <summary>
/// Spec 058 §14 — proves the source-generated <see cref="ListBoxWrapperElement"/>
/// populates and reconciles a flat items collection AND writes a controlled
/// SelectedIndex against a real WinUI <see cref="WinUI.ListBox"/>.
/// </summary>
internal static class ListBoxWrapperParityFixture
{
    internal class Execution(Harness h) : SelfTestFixtureBase(h)
    {
        private WinUI.ListBox? Find() => H.FindControl<WinUI.ListBox>(_ => true);

        public override async Task RunAsync()
        {
            await Mounts_Items_Reconciles_And_Writes_SelectedIndex();
        }

        private async Task Mounts_Items_Reconciles_And_Writes_SelectedIndex()
        {
            int lastSelected = -99;
            var host = H.CreateHost();
            host.Mount(ctx =>
            {
                var (count, setCount) = ctx.UseState(3);
                var (sel, setSel) = ctx.UseState(0);
                var items = Enumerable.Range(0, count).Select(i => (object)$"LBW_{i}").ToArray();
                return VStack(
                    Button("LBW_Add", () => setCount(count + 1)),
                    Button("LBW_Select1", () => setSel(1)),
                    ListBoxWrapperElement.ListBox(
                        selectedIndex: Optional<int>.Of(sel),
                        onSelectedIndexChanged: i => lastSelected = i,
                        items: items));
            });

            await Harness.Render();
            var lb = Find();
            H.Check("ListBoxWrapper_Mounted", lb is not null);
            if (lb is null) return;
            H.Check("ListBoxWrapper_ThreeItems", lb.Items.Count == 3);
            H.Check("ListBoxWrapper_InitialSelected", lb.SelectedIndex == 0);

            // Add an item → ItemsHost reconcile grows the live collection.
            H.ClickButton("LBW_Add");
            await Harness.Render();
            var grew = await Harness.WaitFor(() =>
            {
                var c = Find();
                return c is not null && c.Items.Count == 4;
            }, 30, 25);
            H.Check("ListBoxWrapper_ItemAdded", grew);

            // Force a controlled SelectedIndex write (Optional.Of(1)).
            H.ClickButton("LBW_Select1");
            await Harness.Render();
            var selected = await Harness.WaitFor(() =>
            {
                var c = Find();
                return c is not null && c.SelectedIndex == 1;
            }, 30, 25);
            H.Check("ListBoxWrapper_SelectedIndexWritten", selected);
        }
    }
}
