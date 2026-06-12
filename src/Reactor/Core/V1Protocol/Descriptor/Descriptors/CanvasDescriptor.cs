using System.Diagnostics.CodeAnalysis;
using Microsoft.UI.Xaml;
using WinUI = Microsoft.UI.Xaml.Controls;

namespace Microsoft.UI.Reactor.Core.V1Protocol.Descriptor.Descriptors;

/// <summary>
/// Spec 047 §14 Phase 3 (batch 8) — descriptor variant of the hand-coded
/// <c>MountCanvas</c> / <c>UpdateCanvas</c> arms in <see cref="Reconciler"/>.
///
/// <para><b>Coverage:</b> a zero-event panel container with three
/// conditional one-way props (<c>Width</c>, <c>Height</c>, <c>Background</c>).
/// Children are dispatched through the
/// <see cref="Panel{TElement,TControl}"/> strategy.</para>
///
/// <para><b>§14 Phase 3-final Batch E:</b> per-child
/// <see cref="CanvasAttached"/> (Canvas.Left / Canvas.Top, plus the
/// AnchorX / AnchorY post-layout offset) is now applied via
/// <see cref="Panel{TElement,TControl}.PerChildAttached"/> using descriptor-owned
/// anchor state and size-change subscription.</para>
/// </summary>
internal static class CanvasChildrenStrategy
{
    private sealed class CanvasAnchorState
    {
        public CanvasAttached Current = new();
        public bool Subscribed;
    }

    private static readonly global::System.Runtime.CompilerServices.ConditionalWeakTable<FrameworkElement, CanvasAnchorState> _canvasAnchorStates = new();

    public static readonly Panel<CanvasElement, WinUI.Canvas> Strategy =
        new Panel<CanvasElement, WinUI.Canvas>(
            GetChildren: static e => e.Children,
            GetCollection: static c => c.Children)
        {
            PerChildAttached = static (canvas, ui, childEl) =>
            {
                if (ui is not FrameworkElement fe) return;
                var ca = childEl.GetAttached<CanvasAttached>();
                if (ca is null)
                {
                    ClearCanvasPosition(fe);
                    return;
                }
                ApplyCanvasPosition(fe, ca);
            },
        };

    private static void ApplyCanvasPosition(FrameworkElement fe, CanvasAttached ca)
    {
        if (ca.AnchorX == 0 && ca.AnchorY == 0)
        {
            WinUI.Canvas.SetLeft(fe, ca.Left);
            WinUI.Canvas.SetTop(fe, ca.Top);
            if (_canvasAnchorStates.TryGetValue(fe, out var existing))
                existing.Current = ca;
            return;
        }

        var state = _canvasAnchorStates.GetValue(fe, static _ => new CanvasAnchorState());
        state.Current = ca;
        RecomputeCanvasAnchor(fe, state.Current);

        if (state.Subscribed) return;
        state.Subscribed = true;

        fe.SizeChanged += (_, _) =>
        {
            if (_canvasAnchorStates.TryGetValue(fe, out var s))
                RecomputeCanvasAnchor(fe, s.Current);
        };
        fe.Loaded += (_, _) =>
        {
            if (_canvasAnchorStates.TryGetValue(fe, out var s))
                RecomputeCanvasAnchor(fe, s.Current);
        };
    }

    private static void RecomputeCanvasAnchor(FrameworkElement fe, CanvasAttached ca)
    {
        WinUI.Canvas.SetLeft(fe, ca.Left - ca.AnchorX * fe.ActualWidth);
        WinUI.Canvas.SetTop(fe, ca.Top - ca.AnchorY * fe.ActualHeight);
    }

    // Reset retained anchor state when a reused child no longer carries CanvasAttached.
    private static void ClearCanvasPosition(FrameworkElement fe)
    {
        fe.ClearValue(WinUI.Canvas.LeftProperty);
        fe.ClearValue(WinUI.Canvas.TopProperty);
        if (_canvasAnchorStates.TryGetValue(fe, out var existing))
            existing.Current = new CanvasAttached();
    }
}
