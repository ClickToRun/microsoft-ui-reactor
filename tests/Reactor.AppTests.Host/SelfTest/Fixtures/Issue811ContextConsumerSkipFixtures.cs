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

    internal sealed class SplitViewPane_ContextConsumerRerenders(Harness h)
        : SelfTestFixtureBase(h)
    {
        public override async Task RunAsync()
        {
            var probe = new Probe();
            // Reference-stable SplitView instance re-emitted every render, with the
            // consumer hosted in its Pane slot — a named-slot host the subtree walk
            // must descend into (issue #811 follow-up: SplitView traversal coverage).
            var stableSplit = SplitView(
                pane: Component<OverlayConsumer, OverlayProps>(new OverlayProps(probe)),
                content: TextBlock("split-content"));

            var host = H.CreateHost();
            host.Mount(ctx =>
            {
                var (interactive, setInteractive) = ctx.UseState(true);

                return VStack(
                        Button("Toggle split interactive", () => setInteractive(!interactive)),
                        stableSplit)
                    .Provide(InteractiveCtx, interactive);
            });

            await Harness.Render();

            H.Check("Issue811_Split_Mount_OverlayRenderedOnce", probe.RenderCount == 1);

            H.ClickButton("Toggle split interactive");
            await Harness.Render();

            // Before the traversal fix the Pane consumer stayed at RenderCount 1
            // because the walk never reached SplitView.Pane behind the stable skip.
            H.Check("Issue811_Split_Toggle_PaneConsumerRerendered", probe.RenderCount >= 2);
        }
    }

    internal sealed class SplitViewContent_ContextConsumerRerenders(Harness h)
        : SelfTestFixtureBase(h)
    {
        public override async Task RunAsync()
        {
            var probe = new Probe();
            // Consumer in the Content slot (the other SplitView named slot) with a
            // simple pane — guards the Content visit in the traversal arm, which the
            // Pane fixture alone would not catch if the Content leg regressed.
            var stableSplit = SplitView(
                pane: TextBlock("split-pane"),
                content: Component<OverlayConsumer, OverlayProps>(new OverlayProps(probe)));

            var host = H.CreateHost();
            host.Mount(ctx =>
            {
                var (interactive, setInteractive) = ctx.UseState(true);

                return VStack(
                        Button("Toggle split-content interactive", () => setInteractive(!interactive)),
                        stableSplit)
                    .Provide(InteractiveCtx, interactive);
            });

            await Harness.Render();

            H.Check("Issue811_SplitContent_Mount_LabelLock", H.FindText("Lock interactivity") is not null);
            H.Check("Issue811_SplitContent_Mount_OverlayRenderedOnce", probe.RenderCount == 1);

            H.ClickButton("Toggle split-content interactive");
            await Harness.Render();

            H.Check("Issue811_SplitContent_Toggle_LabelUpdated", H.FindText("Unlock interactivity") is not null);
            H.Check("Issue811_SplitContent_Toggle_ContentConsumerRerendered", probe.RenderCount >= 2);
        }
    }

    internal sealed class ViewboxNested_ContextConsumerRerenders(Harness h)
        : SelfTestFixtureBase(h)
    {
        public override async Task RunAsync()
        {
            var probe = new Probe();
            // Consumer nested inside a reference-stable Viewbox (a single-content host
            // that is a FrameworkElement, not a ContentControl) — exercises both the
            // Viewbox traversal arm and the recursive descent.
            var stableViewbox = Viewbox(Component<OverlayConsumer, OverlayProps>(new OverlayProps(probe)));

            var host = H.CreateHost();
            host.Mount(ctx =>
            {
                var (interactive, setInteractive) = ctx.UseState(true);

                return VStack(
                        Button("Toggle viewbox interactive", () => setInteractive(!interactive)),
                        stableViewbox)
                    .Provide(InteractiveCtx, interactive);
            });

            await Harness.Render();

            H.Check("Issue811_Viewbox_Mount_LabelLock", H.FindText("Lock interactivity") is not null);
            H.Check("Issue811_Viewbox_Mount_OverlayRenderedOnce", probe.RenderCount == 1);

            H.ClickButton("Toggle viewbox interactive");
            await Harness.Render();

            H.Check("Issue811_Viewbox_Toggle_LabelUpdated", H.FindText("Unlock interactivity") is not null);
            H.Check("Issue811_Viewbox_Toggle_NestedConsumerRerendered", probe.RenderCount >= 2);
        }
    }

    internal sealed class ActiveButUnchangedContext_StillSkips(Harness h)
        : SelfTestFixtureBase(h)
    {
        public override async Task RunAsync()
        {
            var probe = new Probe();
            var stableOverlay = Component<OverlayConsumer, OverlayProps>(new OverlayProps(probe));

            var host = H.CreateHost();
            host.Mount(ctx =>
            {
                // A provider is active every render, but its value never changes.
                var (tick, setTick) = ctx.UseState(0);

                return VStack(
                        TextBlock($"tick:{tick}"),
                        Button("Bump tick", () => setTick(tick + 1)),
                        stableOverlay)
                    .Provide(InteractiveCtx, true);
            });

            await Harness.Render();

            H.Check("Issue811_Negative_Mount_OverlayRenderedOnce", probe.RenderCount == 1);

            // Re-render from unrelated state. HasActiveContextValues is true, but the
            // provided value is unchanged, so the reference-stable consumer must still
            // be skipped — the coarse gate must not regress into over-rendering.
            H.ClickButton("Bump tick");
            await Harness.Render();

            H.Check("Issue811_Negative_TickUpdated", H.FindText("tick:1") is not null);
            H.Check("Issue811_Negative_OverlayNotRerendered", probe.RenderCount == 1);
        }
    }
}