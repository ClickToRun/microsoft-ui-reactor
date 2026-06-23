using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using static Microsoft.UI.Reactor.Factories;

namespace Microsoft.UI.Reactor.AppTests.Host.Fixtures;

/// <summary>
/// Interactive fixture for the generated <c>[WrapElementSlot]</c> bridge, exercised
/// end-to-end through real UIA via WinAppDriver. Drives a live WinUI <c>TabView</c> whose
/// <c>TabStripHeader</c> slot is declared with <c>[WrapElementSlot]</c>, through the three
/// transitions the generated <c>ImperativeBridged</c> entry must handle:
/// <list type="number">
///   <item>mount — the slot element materializes onto the control property,</item>
///   <item>update — a content change reconciles in place,</item>
///   <item>removal — setting the slot to <c>null</c> clears the control property.</item>
/// </list>
/// The slot content carries its own AutomationId ("SlotHeader") so the test can observe the
/// real control property through UIA, not just the component's own state ("SlotStatus").
/// </summary>
internal static class WrapElementSlotE2EFixtures
{
    internal class TabStripHeaderSlotComponent : Component
    {
        public override Element Render()
        {
            var (phase, setPhase) = UseState(0);

            Element? header = phase switch
            {
                0 => TextBlock("slot-v1").AutomationId("SlotHeader"),
                1 => TextBlock("slot-v2").AutomationId("SlotHeader"),
                _ => null,
            };

            var tab = TabView(new TabViewItemData("Tab1", TextBlock("body")));
            if (header is not null)
                tab = tab.TabStripHeader(header);

            var status = header is null ? "none" : (phase == 0 ? "slot-v1" : "slot-v2");

            return VStack(8,
                Button("AdvanceSlot", () => setPhase(phase + 1))
                    .AutomationId("AdvanceSlot"),
                TextBlock($"Header: {status}")
                    .AutomationId("SlotStatus"),
                tab
            );
        }
    }

    internal static Element TabStripHeaderSlot(RenderContext ctx) =>
        Component<TabStripHeaderSlotComponent>();
}
