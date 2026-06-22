using Microsoft.UI.Dispatching;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using V1 = Microsoft.UI.Reactor.Core.V1Protocol;
using Desc = Microsoft.UI.Reactor.Core.V1Protocol.Descriptor.Descriptors;
using WinUI = Microsoft.UI.Xaml.Controls;

namespace Microsoft.UI.Reactor.Hooks;

/// <summary>
/// Handle returned by <see cref="UseAnnounceExtensions.UseAnnounce(Microsoft.UI.Reactor.Core.RenderContext)"/>.
/// Provides an imperative <see cref="Announce(string)"/> method for screen reader
/// live-region announcements plus a zero-size <see cref="Region"/> element
/// that must be included somewhere in the component's rendered tree.
/// </summary>
public sealed class AnnounceHandle
{
    // Both are written together in SetTextBlock on the UI thread (reconciler
    // mount) and read from arbitrary threads in Announce — volatile makes that
    // publication well-defined so a cross-thread caller can't observe a torn pair.
    private volatile TextBlock? _textBlock;
    private volatile DispatcherQueue? _dispatcherQueue;

    /// <summary>
    /// A zero-size, invisible Reactor element that acts as the live-region anchor.
    /// Include this anywhere in your component tree (it renders nothing visible).
    /// </summary>
    public Element Region { get; }

    internal AnnounceHandle()
    {
        // Spec 058 §15 (P5.23) — AnnounceRegion is a generated descriptor (the bespoke live-region
        // setup + AnnounceHandle wiring is its Customize .Imperative). Fire the Pattern-A static
        // cctor so the global path registers it.
        global::System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(AnnounceRegionElement).TypeHandle);
        Region = new AnnounceRegionElement(this);
    }

    internal void SetTextBlock(TextBlock tb)
    {
        _textBlock = tb;
        // Captured on the UI thread (reconciler mount) so Announce can marshal
        // back here when called from a background thread.
        _dispatcherQueue = tb.DispatcherQueue;
    }

    /// <summary>
    /// Announces a message to screen readers (polite — queued after current speech).
    /// </summary>
    public void Announce(string message) => Announce(message, assertive: false);

    /// <summary>
    /// Announces a message to screen readers.
    /// </summary>
    /// <param name="message">The text to announce.</param>
    /// <param name="assertive">
    /// If true, interrupts current speech immediately.
    /// If false (default), queued after current speech finishes.
    /// </param>
    /// <remarks>
    /// Thread-safe. When called on the UI thread the announcement is raised
    /// synchronously. When called from any other thread it is marshalled to the
    /// captured UI <see cref="DispatcherQueue"/> and runs asynchronously
    /// (fire-and-forget) — it may complete after this method returns. Calls made
    /// before the live-region <see cref="Region"/> has mounted are ignored.
    /// </remarks>
    // <snippet:announce-live-region>
    public void Announce(string message, bool assertive)
    {
        // _dispatcherQueue is the readiness signal: it is assigned together with
        // _textBlock in SetTextBlock on the UI thread. Gating on it (rather than
        // _textBlock) means an off-thread caller can never observe a wired text
        // block with no dispatcher and fall through to AnnounceCore off-thread.
        if (_dispatcherQueue is not { } dq) return;

        // WinUI XAML APIs throw RPC_E_WRONG_THREAD (0x8001010E) off the UI thread.
        // Marshal back to the captured DispatcherQueue when called cross-thread; the
        // UI-thread fast path is a single HasThreadAccess bool check.
        if (!dq.HasThreadAccess)
        {
            dq.TryEnqueue(() => AnnounceCore(message, assertive));
            return;
        }

        AnnounceCore(message, assertive);
    }

    private void AnnounceCore(string message, bool assertive)
    {
        if (_textBlock is null) return;

        // Primary path: RaiseNotificationEvent (WinUI 1.4+, best Narrator/NVDA support).
        var peer = FrameworkElementAutomationPeer.FromElement(_textBlock);
        if (peer is not null)
        {
            peer.RaiseNotificationEvent(
                AutomationNotificationKind.ActionCompleted,
                assertive
                    ? AutomationNotificationProcessing.ImportantAll
                    : AutomationNotificationProcessing.ImportantMostRecent,
                message,
                "ReactorAnnounce");
            return;
        }

        // Fallback: update the live-region TextBlock text. Screen readers that
        // monitor LiveSetting changes will pick this up.
        _textBlock.Text = message;
    }
    // </snippet:announce-live-region>
}

/// <summary>
/// Internal Reactor element that mounts a hidden TextBlock with LiveSetting for announcements.
/// </summary>
// Spec 058 §15 (P5.23) — generated descriptor. The whole control is one bespoke .Imperative
// mount (hidden zero-size live region + AnnounceHandle wiring); Handle is [WrapManual]. Replaces
// the hand-written AnnounceRegionDescriptor.
[global::Microsoft.UI.Reactor.Wrappers.GenerateReactorDescriptor(typeof(WinUI.TextBlock))]
[global::Microsoft.UI.Reactor.Wrappers.WrapManual("Handle")]
internal partial record AnnounceRegionElement(AnnounceHandle Handle) : Element
{
    internal Action<WinUI.TextBlock>[] Setters { get; init; } = [];

    private static partial global::Microsoft.UI.Reactor.Core.V1Protocol.Descriptor.ControlDescriptor<AnnounceRegionElement, WinUI.TextBlock> Customize(
        global::Microsoft.UI.Reactor.Core.V1Protocol.Descriptor.ControlDescriptor<AnnounceRegionElement, WinUI.TextBlock> d)
        => d.Imperative(
            mount: static (tb, ann) =>
            {
                tb.Width = 0;
                tb.Height = 0;
                tb.Opacity = 0;
                tb.IsHitTestVisible = false;
                tb.IsTabStop = false;
                global::Microsoft.UI.Xaml.Automation.AutomationProperties.SetLiveSetting(tb, global::Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
                global::Microsoft.UI.Xaml.Automation.AutomationProperties.SetAccessibilityView(tb, global::Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
                ann.Handle.SetTextBlock(tb);
            },
            update: static (tb, oldAnn, newAnn) => { });
}

/// <summary>
/// Extension methods for the UseAnnounce hook.
/// </summary>
public static class UseAnnounceExtensions
{
    /// <summary>
    /// Creates an <see cref="AnnounceHandle"/> for making screen reader announcements.
    /// The handle persists across re-renders.
    ///
    /// You must include <see cref="AnnounceHandle.Region"/> in your rendered tree:
    /// <code>
    /// var announce = UseAnnounce();
    /// return VStack(
    ///     announce.Region,
    ///     Button("Save", () => { Save(); announce.Announce("Document saved"); }),
    /// );
    /// </code>
    /// </summary>
    public static AnnounceHandle UseAnnounce(this RenderContext ctx)
    {
        var (handle, _) = ctx.UseState(new AnnounceHandle());
        return handle;
    }

    /// <summary>
    /// Creates an <see cref="AnnounceHandle"/> for making screen reader announcements.
    /// </summary>
    public static AnnounceHandle UseAnnounce(this Component component)
        => component.Context.UseAnnounce();
}
