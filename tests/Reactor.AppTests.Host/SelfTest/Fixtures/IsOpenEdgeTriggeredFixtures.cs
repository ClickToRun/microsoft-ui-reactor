using System;
using System.Threading.Tasks;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using WinUI = Microsoft.UI.Xaml.Controls;

namespace Microsoft.UI.Reactor.AppTests.Host.SelfTest.Fixtures;

/// <summary>
/// Locks in Reactor's contract for a declared <c>bool</c> that the NATIVE
/// control can also mutate (issue R7 — <c>InfoBar.IsOpen</c> after the user
/// dismisses the bar with its built-in ✕).
///
/// <para><b>The contract.</b> Such a value is <b>edge-triggered</b>: the element
/// declares a <i>transition</i>, not a mirror. Reactor writes the control only
/// when the declared value <i>changes</i>. App state is kept in sync with a
/// native dismissal by wiring the control's change callback
/// (<c>OnClosed</c>).</para>
///
/// <para><b>Why both halves are asserted.</b> The two failure modes point in
/// opposite directions, so no single assertion can pin the contract:</para>
/// <list type="bullet">
///   <item><c>RisingEdgeReopens</c> fails if the rising edge ever stops
///   writing — that is the recovery path a caller needs after a dismissal.</item>
///   <item><c>SameDeclaredValueDoesNotReopen</c> fails if the engine is ever
///   changed to re-assert the declared value against the live control. That
///   "fix" looks attractive until you notice <c>InfoBarElement.IsOpen</c>
///   defaults to <c>true</c>: it would make every InfoBar written without an
///   <c>OnClosed</c> handler undismissable, because the next unrelated
///   re-render would bring the bar back.</item>
/// </list>
///
/// <para><b>Element identity matters here.</b> Every re-render below passes a
/// <i>freshly constructed</i> element, because that is what a real render loop
/// produces — <c>InfoBar(...)</c> allocates a new record each pass. Reusing one
/// instance as both old and new would trip <c>Element.ShallowEquals</c>'s
/// <c>ReferenceEquals</c> fast path and skip the descriptor entirely, so these
/// checks would pass without ever exercising the prop entry.</para>
/// </summary>
internal static class IsOpenEdgeTriggeredFixtures
{
    private static readonly Action _noOp = static () => { };

    /// <summary>
    /// Full mount → native dismissal → re-render matrix against a real
    /// <see cref="WinUI.InfoBar"/>.
    /// </summary>
    internal sealed class InfoBarEdgeTriggered(Harness h) : SelfTestFixtureBase(h)
    {
        public override async Task RunAsync()
        {
            var rec = new Reconciler();
            var parent = new Grid { Background = new SolidColorBrush(Colors.Transparent) };
            H.SetContent(parent);

            int closed = 0;

            // A fresh instance per render, exactly like a real render pass.
            InfoBarElement Declared(bool isOpen) => new("Edge", "message")
            {
                IsOpen = isOpen,
                IsClosable = true,
                OnClosed = () => closed++,
            };

            var mounted = Declared(isOpen: true);
            if (rec.Mount(mounted, _noOp) is not WinUI.InfoBar bar)
            {
                H.Check("IsOpenEdge_InfoBar_Mounted", false);
                return;
            }

            parent.Children.Add(bar);
            await Harness.Render();
            H.Check("IsOpenEdge_InfoBar_MountAppliesDeclaredOpen", bar.IsOpen);
            H.Check("IsOpenEdge_InfoBar_MountDoesNotFireOnClosed", closed == 0);

            // The native ✕ — WinUI sets IsOpen = false on the live control and
            // raises Closed. Reactor's declared value is untouched, still true.
            bar.IsOpen = false;
            await Harness.Render();
            H.Check("IsOpenEdge_InfoBar_NativeDismissFiresOnClosedOnce", closed == 1);

            // CONTRACT HALF 1 — re-rendering the same declared value is not an
            // edge, so the dismissal stands. Fails if the engine is changed to
            // re-assert the declared value against the live control (which would
            // make every default-`true` InfoBar undismissable).
            var previous = mounted;
            for (int i = 0; i < 3; i++)
            {
                var next = Declared(isOpen: true);
                rec.UpdateChild(previous, next, bar, _noOp);
                await Harness.Render();
                previous = next;
            }
            H.Check("IsOpenEdge_InfoBar_SameDeclaredValueDoesNotReopen", !bar.IsOpen);
            H.Check("IsOpenEdge_InfoBar_SameDeclaredValueRaisesNoCallback", closed == 1);

            // The documented sync step: OnClosed -> setState(false). The control
            // is already closed, so this must be a silent no-op rather than a
            // second Closed event.
            var syncedClosed = Declared(isOpen: false);
            rec.UpdateChild(previous, syncedClosed, bar, _noOp);
            await Harness.Render();
            H.Check("IsOpenEdge_InfoBar_FallingEdgeOnClosedControlRaisesNoCallback", !bar.IsOpen && closed == 1);

            // CONTRACT HALF 2 — the rising edge re-opens. This is the recovery
            // path R7 claimed was impossible; fails if the edge write is lost.
            var reopened = Declared(isOpen: true);
            rec.UpdateChild(syncedClosed, reopened, bar, _noOp);
            await Harness.Render();
            H.Check("IsOpenEdge_InfoBar_RisingEdgeReopens", bar.IsOpen);
            H.Check("IsOpenEdge_InfoBar_RisingEdgeRaisesNoCallback", closed == 1);

            // A programmatic close from the OPEN state must still close the
            // control. The precondition is folded into the same check so this
            // cannot pass vacuously on an already-closed bar.
            var wasOpenBeforeClose = bar.IsOpen;
            rec.UpdateChild(reopened, Declared(isOpen: false), bar, _noOp);
            await Harness.Render();
            H.Check("IsOpenEdge_InfoBar_ProgrammaticCloseClosesOpenControl", wasOpenBeforeClose && !bar.IsOpen);

            rec.UnmountChild(bar);
            parent.Children.Clear();
        }
    }

    /// <summary>
    /// <see cref="WinUI.TeachingTip"/> shares <c>InfoBar</c>'s authoring shape
    /// (auto-mapped one-way <c>IsOpen</c> + a hand-coded <c>Closed</c> event),
    /// so the same edge contract must hold for it. This fixture pins that
    /// contract at the <c>IsOpen</c> property level.
    ///
    /// <para><b>Why there is no <c>OnClosed</c> oracle here</b> (the callback
    /// half is covered by <see cref="InfoBarEdgeTriggered"/> instead): a
    /// Reactor-mounted TeachingTip flips the <c>IsOpen</c> property but does not
    /// actually present. WinUI raises no <c>Closed</c> event on the way back
    /// down, so a callback count would assert the absence of a presentation bug
    /// rather than the edge contract. An identically-parented raw
    /// <c>WinUI.TeachingTip</c> does present and does raise <c>Closed</c>, so
    /// this is a Reactor-side defect — reported separately, deliberately not
    /// baked in here as an expectation either way.</para>
    ///
    /// <para>Scope note: post-mount edges only. A TeachingTip whose <b>first</b>
    /// render declares <c>IsOpen: true</c> does not open (same reported defect),
    /// so this fixture mounts closed rather than encoding that bug as expected
    /// behaviour.</para>
    /// </summary>
    internal sealed class TeachingTipEdgeTriggered(Harness h) : SelfTestFixtureBase(h)
    {
        public override async Task RunAsync()
        {
            var rec = new Reconciler();
            var parent = new Grid { Background = new SolidColorBrush(Colors.Transparent) };
            H.SetContent(parent);

            static TeachingTipElement Declared(bool isOpen) => new("Edge") { IsOpen = isOpen };

            var mounted = Declared(isOpen: false);
            if (rec.Mount(mounted, _noOp) is not WinUI.TeachingTip tip)
            {
                H.Check("IsOpenEdge_TeachingTip_Mounted", false);
                return;
            }

            parent.Children.Add(tip);
            await Harness.Render();
            H.Check("IsOpenEdge_TeachingTip_MountClosed", !tip.IsOpen);

            // Rising edge opens. TeachingTip's open/close are animated, so settle
            // on the observed state rather than a fixed delay.
            var opened = Declared(isOpen: true);
            rec.UpdateChild(mounted, opened, tip, _noOp);
            H.Check("IsOpenEdge_TeachingTip_RisingEdgeOpens",
                await Harness.WaitFor(() => tip.IsOpen, maxPasses: 40, perPassMs: 25));

            // Native light-dismiss.
            tip.IsOpen = false;
            H.Check("IsOpenEdge_TeachingTip_NativeDismissSettles",
                await Harness.WaitFor(() => !tip.IsOpen, maxPasses: 40, perPassMs: 25));

            // Same declared value is not an edge — the dismissal stands. Several
            // re-renders, so a delayed re-open would still be caught.
            var previous = opened;
            for (int i = 0; i < 3; i++)
            {
                var next = Declared(isOpen: true);
                rec.UpdateChild(previous, next, tip, _noOp);
                await Harness.Render(25);
                previous = next;
            }
            H.Check("IsOpenEdge_TeachingTip_SameDeclaredValueDoesNotReopen", !tip.IsOpen);

            // …and the rising edge after a sync-down still re-opens.
            var syncedClosed = Declared(isOpen: false);
            rec.UpdateChild(previous, syncedClosed, tip, _noOp);
            await Harness.Render();
            var reopened = Declared(isOpen: true);
            rec.UpdateChild(syncedClosed, reopened, tip, _noOp);
            H.Check("IsOpenEdge_TeachingTip_RisingEdgeReopensAfterDismiss",
                await Harness.WaitFor(() => tip.IsOpen, maxPasses: 40, perPassMs: 25));

            // Teardown: close first and let it settle. Today the tip never
            // actually presents (see the class doc), so this is a no-op — but if
            // that defect is fixed, unmounting an open tip would leave a live
            // overlay behind for the next fixture in this shared host process.
            rec.UpdateChild(reopened, Declared(isOpen: false), tip, _noOp);
            await Harness.WaitFor(() => !tip.IsOpen, maxPasses: 40, perPassMs: 25);

            rec.UnmountChild(tip);
            parent.Children.Clear();
        }
    }
}
