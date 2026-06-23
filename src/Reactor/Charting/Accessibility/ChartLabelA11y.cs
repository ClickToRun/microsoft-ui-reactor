using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Microsoft.UI.Reactor.Charting.Accessibility;

/// <summary>
/// Hides a caller-supplied chart label / axis-tick <see cref="Core.Element"/> and its
/// <em>entire realized subtree</em> from assistive technology and keyboard focus
/// traversal, so the chart's own <see cref="IChartAccessibilityData"/> descriptor stays
/// the single accessible representation of slice / tick data.
///
/// <para>
/// Setting <c>AutomationProperties.AccessibilityView = Raw</c> on only the outer wrapper
/// the chart applies does <b>not</b> reliably remove inner peers — a caller's
/// <see cref="Core.Element"/> may be a structured composite (a Reactor
/// <see cref="Core.Component"/> with focusable children, a <see cref="TextBlock"/>, an
/// icon, …) whose interior peers still surface to UIA / focus depending on how the
/// platform composes peer trees under a <c>Raw</c>-marked parent. That produces the
/// double-announcement and stray-focus-stop problems described in issue #162. Walking
/// the subtree and force-<c>Raw</c>-ing every descendant FE (plus clearing
/// <see cref="Control"/>.<c>IsTabStop</c>) removes them from both the UIA tree and the tab order.
/// </para>
/// </summary>
internal static class ChartLabelA11y
{
    /// <summary>
    /// <c>OnMount</c> hook for a non-interactive custom label / tick element. Blocks
    /// pointer hit-testing and recursively hides the element's subtree from UIA and the
    /// tab order — once immediately (covers panel children already attached at mount
    /// time) and again on <see cref="FrameworkElement.Loaded"/> (covers templated-control
    /// inner peers that only realize after the element enters the live visual tree).
    /// </summary>
    internal static void HideSubtreeOnMount(FrameworkElement fe)
    {
        fe.IsHitTestVisible = false;

        // Immediate pass: panel children added to a Children collection are visual
        // children right away, even before the element is loaded.
        HideSubtree(fe);

        // Deferred pass: a templated Control (Button, etc.) only expands its template —
        // and therefore its inner peers — once it is loaded and measured. Re-walk then.
        if (fe.IsLoaded)
            return;

        void OnLoaded(object sender, RoutedEventArgs e)
        {
            fe.Loaded -= OnLoaded;
            ApplyDeferredHide(fe);
        }

        fe.Loaded += OnLoaded;
    }

    /// <summary>
    /// Deferred (<see cref="FrameworkElement.Loaded"/>) arm of <see cref="HideSubtreeOnMount"/>,
    /// with a stale-handler guard (issue #162 review). If the element was unmounted and
    /// returned to the pool <em>before it ever loaded</em>, this one-shot handler survives
    /// into a later reuse. The pool reset restores <c>IsHitTestVisible</c> to <c>true</c> (see
    /// <c>ElementPool.CleanElement</c>), and an interactive (or unrelated) re-renter never
    /// re-hides it — so only apply the deferred hide when the element is still in the
    /// non-interactive hidden state this hook established (<c>IsHitTestVisible == false</c>).
    /// </summary>
    internal static void ApplyDeferredHide(FrameworkElement fe)
    {
        if (!fe.IsHitTestVisible)
            HideSubtree(fe);
    }

    /// <summary>
    /// Recursively forces <c>AccessibilityView.Raw</c> on every descendant
    /// <see cref="FrameworkElement"/> (removing each inner peer from the UIA Content and
    /// Control views) and clears <see cref="Control"/>.<c>IsTabStop</c> on every descendant
    /// <see cref="Control"/> (removing inner focusable children from the keyboard tab
    /// order). Uses only the public <see cref="VisualTreeHelper"/> /
    /// <see cref="AutomationProperties"/> API — no reflection — so it stays AOT / trim safe.
    /// </summary>
    internal static void HideSubtree(DependencyObject root)
    {
        if (root is FrameworkElement fe)
        {
            AutomationProperties.SetAccessibilityView(fe, AccessibilityView.Raw);
            if (fe is Control control)
                control.IsTabStop = false;
        }

        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
            HideSubtree(VisualTreeHelper.GetChild(root, i));
    }
}

