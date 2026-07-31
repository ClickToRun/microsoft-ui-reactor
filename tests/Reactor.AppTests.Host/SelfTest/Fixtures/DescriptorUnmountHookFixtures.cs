using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Core.V1Protocol;
using Microsoft.UI.Reactor.Core.V1Protocol.Descriptor;
using Microsoft.UI.Xaml.Controls;
using WinUI = Microsoft.UI.Xaml.Controls;

namespace Microsoft.UI.Reactor.AppTests.Host.SelfTest.Fixtures;

/// <summary>
/// Issue #949 — pins the mount-time half of the descriptor teardown seam
/// (<c>ControlDescriptor.OnUnmount</c> / <c>.WithUnmount(...)</c>).
///
/// <para><b>Why this needs a live control.</b> The engine's unmount dispatch is tag-gated:
/// <c>Reconciler.UnmountRecursive</c> only reaches the V1 handler when <c>GetElementTag</c>
/// returns an element, and <c>SetElementTagIfNeeded</c> allocates <c>ReactorState</c> only for
/// elements carrying callbacks, a key, extensions, or reference modifiers. So
/// <c>DescriptorHandler.Mount</c> forces the state when a descriptor declares <c>OnUnmount</c>,
/// or the hook would fire for callback-bearing elements of a type and silently not for
/// callback-free ones.</para>
///
/// <para><b>Why the TeachingTip fixture does not cover this.</b> A TeachingTip always ends up
/// with <c>ReactorState</c> anyway — its <c>Target</c> reference entry calls
/// <c>WireReferenceEdge</c> on every mount, and arming the deferred open allocates the payload
/// box. Both mask the tag-forcing. The probe element below is deliberately barren: no callbacks,
/// no key, no extensions, no modifiers, and a descriptor whose ONLY entry is the unmount hook —
/// so the forcing in <c>DescriptorHandler.Mount</c> is the single reason the hook can fire.
/// Delete that line and this fixture fails.</para>
/// </summary>
internal static class DescriptorUnmountHookFixtures
{
    /// <summary>Barren by design — see the class doc. Adding a callback, key, or modifier here
    /// would make <c>NeedsTag</c> true on its own and quietly defeat the test.</summary>
    private sealed record UnmountHookProbe : Element;

    private static int _unmountCalls;

    private sealed class ProbeHandler : DescriptorHandler<UnmountHookProbe, TextBlock>
    {
        public ProbeHandler() : base(Descriptor) { }

        private static readonly ControlDescriptor<UnmountHookProbe, TextBlock> Descriptor =
            new ControlDescriptor<UnmountHookProbe, TextBlock>()
                .WithUnmount(static (in UnmountContext _, TextBlock _) => _unmountCalls++);
    }

    internal sealed class HookFiresForCallbackFreeElement(Harness h) : SelfTestFixtureBase(h)
    {
        public override async Task RunAsync()
        {
            ControlRegistry.Register<UnmountHookProbe, TextBlock>(static () => new ProbeHandler());

            var rec = new Reconciler();
            var parent = new Grid();
            H.SetContent(parent);

            _unmountCalls = 0;
            var element = new UnmountHookProbe();

            if (rec.Mount(element, static () => { }) is not TextBlock control)
            {
                H.Check("DescriptorUnmount_ProbeMounted", false);
                return;
            }

            parent.Children.Add(control);
            await Harness.Render();

            // Precondition, folded into the assertions below so neither can pass vacuously: the
            // element really is tag-free by its own merits, so the state on the control can only
            // have come from the OnUnmount forcing.
            var wouldBeUntagged = element.Key is null && element.Extensions is null && element.Modifiers is null;

            H.Check("DescriptorUnmount_HookNotCalledBeforeUnmount",
                wouldBeUntagged && _unmountCalls == 0);

            rec.UnmountChild(control);
            await Harness.Render();

            H.Check("DescriptorUnmount_HookFiresOnceForCallbackFreeElement",
                wouldBeUntagged && _unmountCalls == 1);

            parent.Children.Clear();
        }
    }

    /// <summary>A descriptor that declares no hook must not pay for one, and must not somehow
    /// invoke another descriptor's. Differential control for the fixture above.</summary>
    internal sealed class NoHookDeclaredStaysSilent(Harness h) : SelfTestFixtureBase(h)
    {
        private sealed record NoHookProbe : Element;

        private sealed class NoHookHandler : DescriptorHandler<NoHookProbe, TextBlock>
        {
            public NoHookHandler() : base(new ControlDescriptor<NoHookProbe, TextBlock>()) { }
        }

        public override async Task RunAsync()
        {
            ControlRegistry.Register<NoHookProbe, TextBlock>(static () => new NoHookHandler());

            var rec = new Reconciler();
            var parent = new Grid();
            H.SetContent(parent);

            _unmountCalls = 0;

            if (rec.Mount(new NoHookProbe(), static () => { }) is not TextBlock control)
            {
                H.Check("DescriptorUnmount_NoHookProbeMounted", false);
                return;
            }

            parent.Children.Add(control);
            await Harness.Render();
            rec.UnmountChild(control);
            await Harness.Render();

            H.Check("DescriptorUnmount_NoHookDeclaredInvokesNothing", _unmountCalls == 0);

            parent.Children.Clear();
        }
    }
}
