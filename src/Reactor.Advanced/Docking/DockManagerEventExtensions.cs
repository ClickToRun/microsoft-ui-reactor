using System;

namespace Microsoft.UI.Reactor.Docking;

// DockManager event-callback fluent extensions. Spec 039 §0.1 + §14 #1;
// spec 045 §2 (docking events); relocated from the core ElementExtensions
// partial by spec 062 §7 Track B (B1) when docking moved into
// Reactor.Advanced. These live in the Microsoft.UI.Reactor.Docking namespace —
// docking's own public surface — so a consumer that already imports the
// docking namespace (mandatory to name DockManager) keeps resolving every
// fluent with no source change; only the owning assembly moved, not the API.
//
// Every cancellable `*ing` and post-event `*ed` callback exposed by
// DockManager mirrors here as a fluent that drops the leading "On" (the C#
// property-as-delegate clash that motivates the rename is documented on the
// core ElementExtensions partial). Passing null clears any previously-set
// handler, matching the `el with { OnX = null }` contract (spec §15 Q2).
public static class DockManagerEventExtensions
{
    /// <summary>Wires the cancellable layout-changing handler. Passing <c>null</c> clears.</summary>
    public static DockManager LayoutChanging(this DockManager el, Action<DockLayoutChangingEventArgs>? handler) =>
        el with { OnLayoutChanging = handler };

    /// <summary>Wires the post-mutation layout-changed handler. Passing <c>null</c> clears.</summary>
    public static DockManager LayoutChanged(this DockManager el, Action<DockLayoutChangedEventArgs>? handler) =>
        el with { OnLayoutChanged = handler };

    /// <summary>Wires the cancellable document-closing handler. Passing <c>null</c> clears.</summary>
    public static DockManager DocumentClosing(this DockManager el, Action<DockDocumentClosingEventArgs>? handler) =>
        el with { OnDocumentClosing = handler };

    /// <summary>Wires the document-closed handler. Passing <c>null</c> clears.</summary>
    public static DockManager DocumentClosed(this DockManager el, Action<DockDocumentClosedEventArgs>? handler) =>
        el with { OnDocumentClosed = handler };

    /// <summary>Wires the cancellable tool-window-hiding handler. Passing <c>null</c> clears.</summary>
    public static DockManager ToolWindowHiding(this DockManager el, Action<DockToolWindowHidingEventArgs>? handler) =>
        el with { OnToolWindowHiding = handler };

    /// <summary>Wires the tool-window-hidden handler. Passing <c>null</c> clears.</summary>
    public static DockManager ToolWindowHidden(this DockManager el, Action<DockToolWindowHiddenEventArgs>? handler) =>
        el with { OnToolWindowHidden = handler };

    /// <summary>Wires the cancellable tool-window-closing handler. Passing <c>null</c> clears.</summary>
    public static DockManager ToolWindowClosing(this DockManager el, Action<DockToolWindowClosingEventArgs>? handler) =>
        el with { OnToolWindowClosing = handler };

    /// <summary>Wires the tool-window-closed handler. Passing <c>null</c> clears.</summary>
    public static DockManager ToolWindowClosed(this DockManager el, Action<DockToolWindowClosedEventArgs>? handler) =>
        el with { OnToolWindowClosed = handler };

    /// <summary>Wires the cancellable content-floating (about-to-tear-out) handler. Passing <c>null</c> clears.</summary>
    public static DockManager ContentFloating(this DockManager el, Action<DockContentFloatingEventArgs>? handler) =>
        el with { OnContentFloating = handler };

    /// <summary>Wires the content-floated handler. Passing <c>null</c> clears.</summary>
    public static DockManager ContentFloated(this DockManager el, Action<DockContentFloatedEventArgs>? handler) =>
        el with { OnContentFloated = handler };

    /// <summary>Wires the cancellable content-docking handler. Passing <c>null</c> clears.</summary>
    public static DockManager ContentDocking(this DockManager el, Action<DockContentDockingEventArgs>? handler) =>
        el with { OnContentDocking = handler };

    /// <summary>Wires the content-docked handler. Passing <c>null</c> clears.</summary>
    public static DockManager ContentDocked(this DockManager el, Action<DockContentDockedEventArgs>? handler) =>
        el with { OnContentDocked = handler };

    /// <summary>Wires the active-content-changed handler. Passing <c>null</c> clears.</summary>
    public static DockManager ActiveContentChanged(this DockManager el, Action<DockActiveContentChangedEventArgs>? handler) =>
        el with { OnActiveContentChanged = handler };

    /// <summary>Wires the floating-window-created handler. Passing <c>null</c> clears.</summary>
    public static DockManager FloatingWindowCreated(this DockManager el, Action<DockFloatingWindowCreatedEventArgs>? handler) =>
        el with { OnFloatingWindowCreated = handler };

    /// <summary>Wires the floating-window-closed handler. Passing <c>null</c> clears.</summary>
    public static DockManager FloatingWindowClosed(this DockManager el, Action<DockFloatingWindowClosedEventArgs>? handler) =>
        el with { OnFloatingWindowClosed = handler };

    /// <summary>Wires the drop-target hover handler. Receives the hovered target (null = none). Passing <c>null</c> clears.</summary>
    public static DockManager DropTargetHovered(this DockManager el, Action<DockTarget?>? handler) =>
        el with { OnDropTargetHovered = handler };

    /// <summary>Wires the drop-target confirm handler. Receives the confirmed target. Passing <c>null</c> clears.</summary>
    public static DockManager DropTargetConfirmed(this DockManager el, Action<DockTarget>? handler) =>
        el with { OnDropTargetConfirmed = handler };

    /// <summary>Wires the drop-targets-dismissed handler (overlay closed without a target). Passing <c>null</c> clears.</summary>
    public static DockManager DropTargetsDismissed(this DockManager el, Action? handler) =>
        el with { OnDropTargetsDismissed = handler };

    /// <summary>Wires the live (mid-drag) layout-changed handler. Passing <c>null</c> clears.</summary>
    public static DockManager LiveLayoutChanged(this DockManager el, Action<DockNode?>? handler) =>
        el with { OnLiveLayoutChanged = handler };

    /// <summary>Wires the splitter-drag-completed handler. Passing <c>null</c> clears.</summary>
    public static DockManager SplitterDragCompleted(this DockManager el, Action? handler) =>
        el with { OnSplitterDragCompleted = handler };
}
