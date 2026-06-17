using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Reactor.Wrappers;   // [GenerateReactorWrapper]
using WinUI = Microsoft.UI.Xaml.Controls;
using static Microsoft.UI.Reactor.Factories;

namespace Microsoft.UI.Reactor.AppTests.Host.SelfTest.Fixtures;

// Spec 058 §14 (P3) — panel-children parity proof. A control exposing a public
// `Children` of type UIElementCollection (StackPanel) gets a generated Panel
// children strategy + a `params Element[] children` factory. This is the
// generated analogue of the hand-written StackPanelDescriptor's
// `Children = new Panel<…>(GetChildren: e => e.Children, GetCollection: c => c.Children)`.
[GenerateReactorWrapper(typeof(WinUI.StackPanel))]
internal partial record StackPanelWrapperElement;

/// <summary>
/// Spec 058 §14 — proves the source-generated <see cref="StackPanelWrapperElement"/>
/// mounts and reconciles a children collection against a real WinUI
/// <see cref="WinUI.StackPanel"/>.
/// </summary>
internal static class StackPanelWrapperParityFixture
{
    internal class Execution(Harness h) : SelfTestFixtureBase(h)
    {
        // The OUTER VStack is itself a WinUI StackPanel, so target the wrapper's
        // own panel by its distinctive "SPW_b*" button children.
        private WinUI.StackPanel? FindInner() => H.FindControl<WinUI.StackPanel>(sp =>
            sp.Children.Count > 0 &&
            sp.Children[0] is WinUI.Button b && b.Content is string s && s.StartsWith("SPW_b"));

        public override async Task RunAsync()
        {
            await Mounts_Children_And_Reconciles_On_Rerender();
        }

        private async Task Mounts_Children_And_Reconciles_On_Rerender()
        {
            var host = H.CreateHost();
            host.Mount(ctx =>
            {
                var (count, setCount) = ctx.UseState(2);
                var kids = Enumerable.Range(0, count)
                    .Select(i => (Core.Element)Button($"SPW_b{i}", () => { }))
                    .ToArray();
                return VStack(
                    Button("SPW_Add", () => setCount(count + 1)),
                    StackPanelWrapperElement.StackPanel(children: kids));
            });

            await Harness.Render();
            var sp = FindInner();
            H.Check("StackPanelWrapper_Mounted", sp is not null);
            if (sp is null) return;
            H.Check("StackPanelWrapper_TwoChildren", sp.Children.Count == 2);

            // Add a child → reconcile grows the live collection.
            H.ClickButton("SPW_Add");
            await Harness.Render();
            var grew = await Harness.WaitFor(() =>
            {
                var c = FindInner();
                return c is not null && c.Children.Count == 3;
            }, 30, 25);
            H.Check("StackPanelWrapper_ChildAdded", grew);
        }
    }
}
