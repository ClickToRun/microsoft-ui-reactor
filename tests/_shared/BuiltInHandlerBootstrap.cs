// Spec-048 §3.4 test bootstrap.
//
// `Reconciler.RegisterV1BuiltInHandlers()` was removed so the trimmer can drop
// unreferenced WinUI controls in shipping apps. Production code is expected to
// either (a) call a factory (e.g. `TextBlock("hi")`) which auto-registers via
// its closed-generic `Reg<>` cctor latch, or (b) call
// `ControlRegistry.Register<,>` explicitly.
//
// Test assemblies, however, exercise direct-record-ctor patterns extensively
// (`new TextBlockElement("hi")` — see issue #486). Forcing every test to call
// a factory first would be invasive and would mask genuine "missing handler"
// regressions. Instead, this file registers every built-in handler globally
// via a `[ModuleInitializer]` — equivalent to the legacy
// `RegisterV1BuiltInHandlers` body, but rooted in the test assembly so the
// shipping Reactor.dll trimmer story is preserved (the spec forbids
// `[ModuleInitializer]` in `Reactor.dll` itself precisely because it would
// unconditionally root every handler).
//
// Mirrors `Reconciler.RegisterV1BuiltInHandlers` 1:1 (order, contents) as of
// the §3.4 removal commit. Keep in sync with `Dsl.cs` whenever a new built-in
// handler/descriptor is added.

using System.Runtime.CompilerServices;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Hooks;
using V1 = Microsoft.UI.Reactor.Core.V1Protocol;
using Desc = Microsoft.UI.Reactor.Core.V1Protocol.Descriptor.Descriptors;
using WinUI = Microsoft.UI.Xaml.Controls;
using WinPrim = Microsoft.UI.Xaml.Controls.Primitives;
using WinShapes = Microsoft.UI.Xaml.Shapes;
using SemanticPanel = Microsoft.UI.Reactor.Accessibility.SemanticPanel;

namespace Reactor.Tests.Bootstrap;

internal static class BuiltInHandlerBootstrap
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        // ── Descriptor-backed value controls ──
        System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(ToggleSwitchElement).TypeHandle);
        System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(SliderElement).TypeHandle);
        System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(TextBoxElement).TypeHandle);
        System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(BorderElement).TypeHandle);
        // Controls migrated to generated descriptors (spec 058 §15 / P5.3+) self-
        // register via their Pattern-A static cctor; fire it explicitly here
        // (tests construct records directly, not via factories).
        System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(ViewboxElement).TypeHandle);
        System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(ProgressRingElement).TypeHandle);
        System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(ProgressElement).TypeHandle);
        _ = V1.Reg<ListViewElement, WinUI.ListView, V1.Handlers.ListViewHandler>.Done;

        _ = V1.Reg<NavigationHostElement, WinUI.Grid, V1.Handlers.NavigationHostHandler>.Done;
        _ = V1.Reg<GridViewElement, WinUI.GridView, V1.Handlers.GridViewHandler>.Done;

        // ── Overlay / modal decorator handlers ──
        _ = V1.RegDecorator<ContentDialogElement, V1.Handlers.ContentDialogHandler>.Done;
        _ = V1.RegDecorator<FlyoutElement, V1.Handlers.FlyoutHandler>.Done;
        _ = V1.RegDecorator<MenuBarElement, V1.Handlers.MenuBarHandler>.Done;
        _ = V1.RegDecorator<CommandBarElement, V1.Handlers.CommandBarHandler>.Done;
        _ = V1.RegDecorator<MenuFlyoutElement, V1.Handlers.MenuFlyoutHandler>.Done;
        _ = V1.RegDecorator<PopupElement, V1.Handlers.PopupHandler>.Done;
        _ = V1.RegDecorator<CommandBarFlyoutElement, V1.Handlers.CommandBarFlyoutHandler>.Done;
        System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(ButtonElement).TypeHandle);

        // ── Composite / validation decorators ──
        _ = V1.RegDecorator<Microsoft.UI.Reactor.Core.CommandHostElement, V1.Handlers.CommandHostHandler>.Done;
        _ = V1.RegDecorator<Microsoft.UI.Reactor.Controls.Validation.FormFieldElement, V1.Handlers.FormFieldHandler>.Done;
        _ = V1.RegDecorator<Microsoft.UI.Reactor.Controls.Validation.ValidationVisualizerElement, V1.Handlers.ValidationVisualizerHandler>.Done;
        _ = V1.RegDecorator<Microsoft.UI.Reactor.Controls.Validation.ValidationRuleElement, V1.Handlers.ValidationRuleHandler>.Done;

        // ── Base-derived (typed templated lists / lazy stacks / typed templated tree views / items hosts) ──
        _ = V1.RegBaseDecorator<TemplatedListElementBase, V1.Handlers.TemplatedListHandler>.Done;
        _ = V1.RegBaseDecorator<LazyStackElementBase, V1.Handlers.LazyStackHandler>.Done;
        _ = V1.RegBaseDecorator<TemplatedTreeViewElementBase, V1.Handlers.TemplatedTreeViewHandler>.Done;
        _ = Desc.ItemsRepeaterDescriptor.Registration.Done;
        _ = Desc.ItemsViewDescriptor.Registration.Done;

        // ── Standard concrete descriptors (alphabetical, mirrors RegisterV1BuiltInHandlers) ──
        System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(AnimatedIconElement).TypeHandle);
        System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(AnimatedVisualPlayerElement).TypeHandle);
        System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(AnnotatedScrollBarElement).TypeHandle);
        System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(AnnounceRegionElement).TypeHandle);
        System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(AutoSuggestBoxElement).TypeHandle);
        
        System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(CalendarDatePickerElement).TypeHandle);
        System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(CalendarViewElement).TypeHandle);
        System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(CanvasElement).TypeHandle);
        System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(CheckBoxElement).TypeHandle);
        System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(ColorPickerElement).TypeHandle);
        System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(ComboBoxElement).TypeHandle);
        System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(BreadcrumbBarElement).TypeHandle);
        System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(SelectorBarElement).TypeHandle);
        System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(SwipeControlElement).TypeHandle);
        System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(SemanticZoomElement).TypeHandle);
        System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(DatePickerElement).TypeHandle);
        System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(DropDownButtonElement).TypeHandle);
        System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(EllipseElement).TypeHandle);
        _ = V1.RegDecorator<ExpanderElement, V1.Handlers.ExpanderHandler>.Done;
        System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(FlexElement).TypeHandle);
        System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(FlipViewElement).TypeHandle);
        System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(FrameElement).TypeHandle);
        System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(GridElement).TypeHandle);
        System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(HyperlinkButtonElement).TypeHandle);
        System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(ImageElement).TypeHandle);
        System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(InfoBadgeElement).TypeHandle);
        System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(InfoBarElement).TypeHandle);
        System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(ItemContainerElement).TypeHandle);
        System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(LineElement).TypeHandle);
        System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(ListBoxElement).TypeHandle);
        System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(MapControlElement).TypeHandle);
        System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(MediaPlayerElementElement).TypeHandle);
        System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(NavigationViewElement).TypeHandle);
        System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(NumberBoxElement).TypeHandle);
        System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(ParallaxViewElement).TypeHandle);
        System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(PasswordBoxElement).TypeHandle);
        System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(PathElement).TypeHandle);
        System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(PersonPictureElement).TypeHandle);
        System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(PipsPagerElement).TypeHandle);
        System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(PivotElement).TypeHandle);
        // ProgressBar migrated to a generated descriptor (spec 058 §15 / P5.4) —
        // its Pattern-A static cctor is fired near the top of this initializer.
        // ProgressRing migrated to a generated descriptor (spec 058 §15 / P5.4) —
        // its Pattern-A static cctor is fired near the top of this initializer.
        System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(RadioButtonElement).TypeHandle);
        System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(RadioButtonsElement).TypeHandle);
        System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(RatingControlElement).TypeHandle);
        System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(RectangleElement).TypeHandle);
        System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(RefreshContainerElement).TypeHandle);
        System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(RelativePanelElement).TypeHandle);
        System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(RepeatButtonElement).TypeHandle);
        System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(RichEditBoxElement).TypeHandle);
        System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(RichTextBlockElement).TypeHandle);
        System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(ScrollViewElement).TypeHandle);
        System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(ScrollViewerElement).TypeHandle);
        
        System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(SemanticElement).TypeHandle);
        
        System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(SplitButtonElement).TypeHandle);
        System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(SplitViewElement).TypeHandle);
        System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(StackElement).TypeHandle);
        
        System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(TabViewElement).TypeHandle);
        System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(TeachingTipElement).TypeHandle);
        System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(TextBlockElement).TypeHandle);
        System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(TimePickerElement).TypeHandle);
        System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(TitleBarElement).TypeHandle);
        System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(ToggleButtonElement).TypeHandle);
        System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(ToggleSplitButtonElement).TypeHandle);
        System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(TreeViewElement).TypeHandle);
        // Viewbox migrated to a generated descriptor (spec 058 §15 / P5.3) — its
        // Pattern-A static cctor is fired near the top of this initializer.
        System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(WebView2Element).TypeHandle);
        System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(WrapGridElement).TypeHandle);

        // IconElement migrated to a generated polymorphic descriptor (spec 058
        // §15 / P5.27): its Pattern-A static cctor self-registers the decorator.
        System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(Microsoft.UI.Reactor.Core.IconElement).TypeHandle);

        // XamlPageElement / XamlHostElement migrated to generated monomorphic
        // decorators (spec 058 §15 / P5.28): their Pattern-A static cctors
        // self-register on first type load.
        System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(Microsoft.UI.Reactor.Hosting.XamlPageElement).TypeHandle);
        System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(Microsoft.UI.Reactor.Hosting.XamlHostElement).TypeHandle);
    }
}
