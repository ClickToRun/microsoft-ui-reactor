using Microsoft.UI.Xaml;
using Windows.Foundation;
using WinUI = Microsoft.UI.Xaml.Controls;
using Desc = Microsoft.UI.Reactor.Core.V1Protocol.Descriptor;
using V1 = Microsoft.UI.Reactor.Core.V1Protocol;

namespace Microsoft.UI.Reactor.Core;

// Spec 058 §15 (P5.25) — TabView's bespoke surface: the TabItemsHost (Tabs → TabViewItem
// containers with pinnable headers/icons), value-diff SelectedIndex, the TabStripHeader/Footer
// Element slots (.ImperativeBridged), and the 4 drag/close/add events. Reproduced verbatim from
// the deleted TabViewDescriptor. The 6 simple props auto-map (in Element.cs).
public partial record TabViewElement
{
    private static readonly WinUI.SelectionChangedEventHandler __SelectionChangedTrampoline = (s, _) =>
    {
        var t = (WinUI.TabView)s!;
        if (!Reconciler.TryGetReactorState(t, out var state)) return;
        if (ChangeEchoSuppressor.ShouldSuppressEcho(state, t.SelectedIndex)) return;
        (state.Element as TabViewElement)?.OnSelectedIndexChanged?.Invoke(t.SelectedIndex);
    };

    private static readonly TypedEventHandler<WinUI.TabView, WinUI.TabViewTabCloseRequestedEventArgs>
        __TabCloseRequestedTrampoline = (s, args) =>
        {
            var t = (WinUI.TabView)s!;
            var idx = t.TabItems.IndexOf(args.Tab);
            (Reconciler.GetElementTag(t) as TabViewElement)?.OnTabCloseRequested?.Invoke(idx);
        };

    private static readonly TypedEventHandler<WinUI.TabView, object>
        __AddTabButtonClickTrampoline = (s, _) =>
            (Reconciler.GetElementTag((UIElement)s!) as TabViewElement)?.OnAddTabButtonClick?.Invoke();

    private static readonly TypedEventHandler<WinUI.TabView, WinUI.TabViewTabDragStartingEventArgs>
        __TabDragStartingTrampoline = (s, args) =>
        {
            var t = (WinUI.TabView)s!;
            if (Reconciler.GetElementTag(t) is not TabViewElement el || el.OnTabDragStarting is null) return;
            var idx = t.TabItems.IndexOf(args.Tab);
            if (idx < 0) return;
            args.Data.RequestedOperation = global::Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move;
            args.Data.SetText("reactor-tabview-tab");
            el.OnTabDragStarting(idx);
        };

    private static readonly TypedEventHandler<WinUI.TabView, WinUI.TabViewTabDragCompletedEventArgs>
        __TabDragCompletedTrampoline = (s, args) =>
        {
            var t = (WinUI.TabView)s!;
            if (Reconciler.GetElementTag(t) is not TabViewElement el || el.OnTabDragCompleted is null) return;
            var idx = t.TabItems.IndexOf(args.Tab);
            var wasOutside = args.DropResult == global::Windows.ApplicationModel.DataTransfer.DataPackageOperation.None;
            el.OnTabDragCompleted(idx, wasOutside);
        };

    private static partial Desc.ControlDescriptor<TabViewElement, WinUI.TabView> Customize(
        Desc.ControlDescriptor<TabViewElement, WinUI.TabView> d)
    {
        d.Children = new V1.TabItemsHost<TabViewElement, WinUI.TabView, TabViewItemData>(
            GetItems:        static e => e.Tabs,
            GetCollection:   static c => c.TabItems,
            GetContent:      static item => item.Content,
            CreateContainer: static (item, mounted) =>
            {
                var tvi = new WinUI.TabViewItem
                {
                    Header = Reconciler.BuildTabHeader(item),
                    IsClosable = item.IsClosable,
                    Content = mounted,
                };
                if (item.Icon is not null) tvi.IconSource = V1.IconResolver.ResolveIconSource(item.Icon);
                return tvi;
            },
            UpdateContainer: static (oldItem, newItem, container) =>
            {
                if (container is not WinUI.TabViewItem tvi) return;

                if (newItem.IsPinnable && oldItem.IsPinnable
                    && tvi.Header is WinUI.StackPanel existingHeader
                    && Reconciler.TryUpdatePinHeaderInPlace(existingHeader, oldItem, newItem))
                {
                    // In-place succeeded.
                }
                else if (newItem.IsPinnable || oldItem.IsPinnable)
                {
                    tvi.Header = Reconciler.BuildTabHeader(newItem);
                }
                else if (tvi.Header as string != newItem.Header)
                {
                    tvi.Header = newItem.Header;
                }

                if (tvi.IsClosable != newItem.IsClosable) tvi.IsClosable = newItem.IsClosable;
                if (!Equals(newItem.Icon, oldItem.Icon))
                    tvi.IconSource = newItem.Icon is null ? null : V1.IconResolver.ResolveIconSource(newItem.Icon);
            });
        return d
            .ImperativeBridged(
                mount: static (ctx, c, e) =>
                {
                    if (e.TabStripHeader is not null)
                        c.TabStripHeader = ctx.Reconciler.Mount(e.TabStripHeader, ctx.RequestRerender);
                },
                update: static (ctx, c, o, n) =>
                {
                    var next = ctx.Reconciler.ReconcileV1Child(
                        o.TabStripHeader, n.TabStripHeader, c.TabStripHeader as UIElement, ctx.RequestRerender);
                    if (!ReferenceEquals(c.TabStripHeader, next)) c.TabStripHeader = next;
                })
            .ImperativeBridged(
                mount: static (ctx, c, e) =>
                {
                    if (e.TabStripFooter is not null)
                        c.TabStripFooter = ctx.Reconciler.Mount(e.TabStripFooter, ctx.RequestRerender);
                },
                update: static (ctx, c, o, n) =>
                {
                    var next = ctx.Reconciler.ReconcileV1Child(
                        o.TabStripFooter, n.TabStripFooter, c.TabStripFooter as UIElement, ctx.RequestRerender);
                    if (!ReferenceEquals(c.TabStripFooter, next)) c.TabStripFooter = next;
                })
            .HandCodedControlled<V1.TabViewEventPayload, int, WinUI.SelectionChangedEventHandler>(
                get:         static e => e.SelectedIndex,
                set:         static (c, v) => c.SelectedIndex = v,
                readBack:    static c => c.SelectedIndex,
                subscribe:   static (c, h) => c.SelectionChanged += h,
                callback:    static e => e.OnSelectedIndexChanged,
                trampoline:  __SelectionChangedTrampoline,
                slotIsNull:  static p => p.SelectionChangedTrampoline is null,
                setSlot:     static (p, h) => p.SelectionChangedTrampoline = h,
                valueDiffEcho: true)
            .HandCodedEvent<V1.TabViewEventPayload,
                TypedEventHandler<WinUI.TabView, WinUI.TabViewTabCloseRequestedEventArgs>>(
                subscribe:        static (c, h) => c.TabCloseRequested += h,
                callbackPresent:  static e => e.OnTabCloseRequested,
                trampoline:       __TabCloseRequestedTrampoline,
                slotIsNull:       static p => p.TabCloseRequestedTrampoline is null,
                setSlot:          static (p, h) => p.TabCloseRequestedTrampoline = h)
            .HandCodedEvent<V1.TabViewEventPayload,
                TypedEventHandler<WinUI.TabView, object>>(
                subscribe:        static (c, h) => c.AddTabButtonClick += h,
                callbackPresent:  static e => e.OnAddTabButtonClick,
                trampoline:       __AddTabButtonClickTrampoline,
                slotIsNull:       static p => p.AddTabButtonClickTrampoline is null,
                setSlot:          static (p, h) => p.AddTabButtonClickTrampoline = h)
            .HandCodedEvent<V1.TabViewEventPayload,
                TypedEventHandler<WinUI.TabView, WinUI.TabViewTabDragStartingEventArgs>>(
                subscribe:        static (c, h) => c.TabDragStarting += h,
                callbackPresent:  static e => e.OnTabDragStarting,
                trampoline:       __TabDragStartingTrampoline,
                slotIsNull:       static p => p.TabDragStartingTrampoline is null,
                setSlot:          static (p, h) => p.TabDragStartingTrampoline = h)
            .HandCodedEvent<V1.TabViewEventPayload,
                TypedEventHandler<WinUI.TabView, WinUI.TabViewTabDragCompletedEventArgs>>(
                subscribe:        static (c, h) => c.TabDragCompleted += h,
                callbackPresent:  static e => e.OnTabDragCompleted,
                trampoline:       __TabDragCompletedTrampoline,
                slotIsNull:       static p => p.TabDragCompletedTrampoline is null,
                setSlot:          static (p, h) => p.TabDragCompletedTrampoline = h);
    }
}
