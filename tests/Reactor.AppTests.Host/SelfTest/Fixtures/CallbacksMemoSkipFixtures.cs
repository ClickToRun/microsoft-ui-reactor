using System;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.AppTests.Host.SelfTest;
using static Microsoft.UI.Reactor.Factories;

namespace Microsoft.UI.Reactor.AppTests.Host.SelfTest.Fixtures;

/// <summary>
/// Issue #151 end-to-end proof through the REAL reconciler. When a child's data
/// is unchanged but the parent passes a freshly-allocated callback (wrapped in
/// <see cref="Callbacks{T}"/>), <c>Reconciler.ReconcileComponent</c> memo-skips the
/// child render — yet its skip branch still refreshes the child's live
/// <see cref="Component{TProps}.Props"/>. So a handler that reads
/// <c>Props.Cb.Value</c> at dispatch time invokes the CURRENT delegate, never a
/// memoized-stale one.
///
/// Unlike the headless <c>CallbacksMemoTests</c> (which drive the production
/// primitives <c>IPropsComparable.CompareProps</c> / <c>IPropsReceiver.SetProps</c>
/// directly), this fixture mounts a live parent/child tree and exercises the actual
/// skip branch wired into the reconciler.
/// </summary>
internal static class CallbacksMemoSkipFixtures
{
    private sealed record ChildCallbacks(Action OnTap);

    // A reference-stable render tally the parent owns and the child writes to. It is
    // a data prop, but its identity never changes across renders (same instance), so
    // it compares reference-equal and never drives a re-render — it just lets the test
    // observe how many times the child actually rendered, without static mutable state.
    private sealed class RenderTally
    {
        public int Count;
    }

    private sealed record MemoChildProps(string Data, RenderTally Tally, Callbacks<ChildCallbacks> Cb);

    private sealed class MemoChild : Component<MemoChildProps>
    {
        public override Element Render()
        {
            Props.Tally.Count++;
            return VStack(
                TextBlock($"child-data:{Props.Data}"),
                // Reads the live callback off Props at *dispatch* time — the button
                // element is built once (the child memo-skips on later reconciles),
                // but its handler resolves Props.Cb.Value when the click fires.
                Button("ChildTap", () => Props.Cb.Value.OnTap()));
        }
    }

    /// <summary>
    /// Parent bumps a counter that its child-callback closes over, while the child's
    /// data stays constant. The child must memo-skip (tally stays 1) yet, when tapped,
    /// fire the delegate that captured the latest counter value.
    /// </summary>
    internal class SkipRefreshesLiveDelegate(Harness h) : SelfTestFixtureBase(h)
    {
        public override async Task RunAsync()
        {
            var tally = new RenderTally();

            var host = H.CreateHost();
            host.Mount(ctx =>
            {
                var (tick, setTick) = ctx.UseState(0);
                var (fired, setFired) = ctx.UseState(-1);

                // Fresh delegate every parent render, capturing the CURRENT tick.
                // The child's data is constant, so the child memo-skips — the only
                // thing that changes across renders is this excluded callbacks slot.
                var childCb = new ChildCallbacks(() => setFired(tick));

                return VStack(
                    TextBlock($"fired:{fired}"),
                    Button("BumpTick", () => setTick(tick + 1)),
                    Component<MemoChild, MemoChildProps>(
                        new MemoChildProps("constant", tally, childCb)));
            });

            await Harness.Render();
            H.Check("MemoSkip_ChildRenderedOnce", tally.Count == 1);
            H.Check("MemoSkip_FiredInitial", H.FindText("fired:-1") is not null);

            // Parent re-renders with a NEW delegate (captures tick=1); child data is
            // unchanged so the real reconciler memo-skips the child render...
            H.ClickButton("BumpTick");
            await Harness.Render();
            H.Check("MemoSkip_ChildDidNotReRender", tally.Count == 1);

            // ...but dispatching from the (un-re-rendered) child must invoke the
            // CURRENT delegate (tick=1 → fired:1), proving the skip branch refreshed
            // the child's live Props. A stale delegate would yield fired:0.
            H.ClickButton("ChildTap");
            await Harness.Render();
            H.Check("MemoSkip_CurrentDelegateInvoked", H.FindText("fired:1") is not null);
            H.Check("MemoSkip_StaleDelegateNotInvoked", H.FindText("fired:0") is null);
        }
    }
}
