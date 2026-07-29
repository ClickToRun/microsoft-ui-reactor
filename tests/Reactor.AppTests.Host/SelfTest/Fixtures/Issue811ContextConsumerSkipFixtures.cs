using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.AppTests.Host.SelfTest;
using Microsoft.UI.Reactor.Hooks;
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

    internal sealed class KeyedReferenceStableChildSkip_ContextConsumerRerenders(Harness h)
        : SelfTestFixtureBase(h)
    {
        public override async Task RunAsync()
        {
            var probe = new Probe();
            var stableOverlay = Component<OverlayConsumer, OverlayProps>(new OverlayProps(probe))
                .WithKey("overlay");

            var host = H.CreateHost();
            host.Mount(ctx =>
            {
                var (interactive, setInteractive) = ctx.UseState(true);

                return VStack(
                        stableOverlay,
                        TextBlock(interactive ? "keyed-surface:on" : "keyed-surface:off")
                            .WithKey("surface"),
                        Button("Toggle keyed interactive", () => setInteractive(!interactive))
                            .WithKey("toggle"))
                    .Provide(InteractiveCtx, interactive);
            });

            await Harness.Render();

            H.Check("Issue811_Keyed_Mount_LabelLock", H.FindText("Lock interactivity") is not null);
            H.Check("Issue811_Keyed_Mount_OverlayRenderedOnce", probe.RenderCount == 1);

            H.ClickButton("Toggle keyed interactive");
            await Harness.Render();

            H.Check("Issue811_Keyed_Toggle_SurfaceOff", H.FindText("keyed-surface:off") is not null);
            H.Check("Issue811_Keyed_Toggle_LabelUpdated", H.FindText("Unlock interactivity") is not null);
            H.Check("Issue811_Keyed_Toggle_OverlayRerendered", probe.RenderCount >= 2);

            H.ClickButton("Invoke overlay action");
            await Harness.Render();

            H.Check("Issue811_Keyed_ActionUsesCurrentContext", probe.LastAction == "unlock");
        }
    }

    internal sealed class HintedRange_ContextConsumerRerenders(Harness h)
        : SelfTestFixtureBase(h)
    {
        public override async Task RunAsync()
        {
            var probe = new Probe();
            var host = H.CreateHost();
            host.Mount(ctx =>
            {
                var (interactive, setInteractive) = ctx.UseState(true);
                var (values, setValues) = ctx.UseState(new[] { 0, 0 });
                var (changed, setChanged) = ctx.UseState(Array.Empty<int>());
                var cells = ctx.UseMemoCellsByIndex(
                    values,
                    changed,
                    (item, index) => index == 0
                        ? Component<OverlayConsumer, OverlayProps>(new OverlayProps(probe))
                        : TextBlock($"hint-cell:{item}"));

                return VStack(
                        Button("Toggle hinted interactive", () =>
                        {
                            setInteractive(!interactive);
                            setChanged(new[] { 1 });
                            setValues(new[] { values[0], values[1] + 1 });
                        }),
                        VStack(cells))
                    .Provide(InteractiveCtx, interactive);
            });

            await Harness.Render();

            H.Check("Issue811_Hint_Mount_LabelLock", H.FindText("Lock interactivity") is not null);
            H.Check("Issue811_Hint_Mount_OverlayRenderedOnce", probe.RenderCount == 1);

            H.ClickButton("Toggle hinted interactive");
            await Harness.Render();

            H.Check("Issue811_Hint_ChangedCellUpdated", H.FindText("hint-cell:1") is not null);
            H.Check("Issue811_Hint_LabelUpdated", H.FindText("Unlock interactivity") is not null);
            H.Check("Issue811_Hint_OverlayRerendered", probe.RenderCount >= 2);
        }
    }
}