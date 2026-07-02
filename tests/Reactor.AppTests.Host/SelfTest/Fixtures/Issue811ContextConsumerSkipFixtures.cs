using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.AppTests.Host.SelfTest;
using static Microsoft.UI.Reactor.Factories;

namespace Microsoft.UI.Reactor.AppTests.Host.SelfTest.Fixtures;

/// <summary>
/// Issue #811 — a reference-stable child subtree that consumes context must still
/// re-render when the provided context value changes.
///
/// The reproducer is intentionally minimal: a parent owns internal interactive
/// state, provides it via <c>.Provide(...)</c>, and re-emits the SAME overlay
/// element instance on every render. The overlay consumes that context to derive
/// both visible text and a click action. If child-skip short-circuits before the
/// reconciler descends into the consumer, the label stays stale and the click path
/// keeps dispatching the old action shape.
/// </summary>
internal static class Issue811ContextConsumerSkipFixtures
{
    private static readonly Context<bool> InteractiveCtx = new(true);

    private sealed class Probe
    {
        public int RenderCount;
        public string? LastAction;
    }

    private sealed record OverlayProps(Probe Probe);

    private sealed class OverlayConsumer : Component<OverlayProps>
    {
        public override Element Render()
        {
            Props.Probe.RenderCount++;
            bool interactive = UseContext(InteractiveCtx);

            return VStack(
                TextBlock(interactive ? "Lock interactivity" : "Unlock interactivity"),
                Button("Invoke overlay action", () =>
                    Props.Probe.LastAction = interactive ? "lock" : "unlock"));
        }
    }

    internal sealed class ReferenceStableChildSkip_ContextConsumerRerenders(Harness h)
        : SelfTestFixtureBase(h)
    {
        public override async Task RunAsync()
        {
            var probe = new Probe();
            var stableOverlay = Component<OverlayConsumer, OverlayProps>(new OverlayProps(probe));

            var host = H.CreateHost();
            host.Mount(ctx =>
            {
                var (interactive, setInteractive) = ctx.UseState(true);

                return VStack(
                        TextBlock(interactive ? "surface:on" : "surface:off"),
                        Button("Toggle interactive", () => setInteractive(!interactive)),
                        stableOverlay)
                    .Provide(InteractiveCtx, interactive);
            });

            await Harness.Render();

            H.Check("Issue811_Mount_SurfaceOn", H.FindText("surface:on") is not null);
            H.Check("Issue811_Mount_LabelLock", H.FindText("Lock interactivity") is not null);
            H.Check("Issue811_Mount_OverlayRenderedOnce", probe.RenderCount == 1);

            H.ClickButton("Toggle interactive");
            await Harness.Render();

            H.Check("Issue811_Toggle_SurfaceOff", H.FindText("surface:off") is not null);
            H.Check("Issue811_Toggle_LabelUpdated", H.FindText("Unlock interactivity") is not null);
            H.Check("Issue811_Toggle_OverlayRerendered", probe.RenderCount >= 2);

            H.ClickButton("Invoke overlay action");
            await Harness.Render();

            H.Check("Issue811_ActionUsesCurrentContext", probe.LastAction == "unlock");
            H.Check("Issue811_ActionDidNotUseStaleContext", probe.LastAction != "lock");
        }
    }
}