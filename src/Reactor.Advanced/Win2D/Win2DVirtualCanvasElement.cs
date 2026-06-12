using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Reactor.Core;
using Windows.Foundation;

namespace Microsoft.UI.Reactor.Advanced.Win2D;

/// <summary>
/// Reactor element for Win2D's tiled <see cref="CanvasVirtualControl"/>.
/// </summary>
public sealed record Win2DVirtualCanvasElement : Element
{
    /// <summary>
    /// Callback invoked for each invalidated virtual canvas region.
    /// </summary>
    public Action<CanvasDrawingSession, Rect>? OnRegionDraw { get; init; }

    /// <summary>
    /// Optional asynchronous resource creation callback tracked before region drawing.
    /// </summary>
    public Func<CanvasVirtualControl, Task>? OnCreateResources { get; init; }

    /// <summary>
    /// Logical size of the scrollable virtual surface, in DIPs.
    /// </summary>
    public Size ContentSize { get; init; } = new(800, 600);

    /// <summary>
    /// Whether the control draws with Win2D's process-wide shared <see cref="CanvasDevice"/>
    /// (<see cref="CanvasVirtualControl.UseSharedDevice"/>) instead of its own dedicated device.
    /// </summary>
    /// <remarks>
    /// This <b>must</b> be <c>true</c> when the canvas draws resources created by the
    /// <c>UseCanvasResources</c> hook (or any other resource built from
    /// <see cref="CanvasDevice.GetSharedDevice()"/>). Win2D resources are device-affine:
    /// drawing a shared-device resource with a control that owns a different device raises a
    /// cross-device error that surfaces as a fatal stowed exception. See
    /// <see href="docs/guide/win2d-canvas.md#shared-device">the shared-device guidance</see>.
    /// This is a device-construction setting evaluated once when the control mounts; to change it,
    /// remount the canvas (e.g. via a different key) rather than toggling it on a live control.
    /// </remarks>
    public bool UseSharedDevice { get; init; }

    /// <summary>
    /// Regions to invalidate when the list instance changes.
    /// </summary>
    public IReadOnlyList<Rect>? InvalidateRegions { get; init; }

    /// <summary>
    /// Raw Win2D control setters applied after typed properties.
    /// </summary>
    public Action<CanvasVirtualControl>[] Setters { get; init; } = Array.Empty<Action<CanvasVirtualControl>>();

    internal Win2DVirtualCanvasElement() { }
}
