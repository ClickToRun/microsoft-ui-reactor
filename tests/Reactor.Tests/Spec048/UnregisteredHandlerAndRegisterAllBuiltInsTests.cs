using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Core.V1Protocol;
using Reactor.Tests.Bootstrap;
using ValidationNs = Microsoft.UI.Reactor.Controls.Validation;
using HooksNs = Microsoft.UI.Reactor.Hooks;
using HostingNs = Microsoft.UI.Reactor.Hosting;
using Xunit;

namespace Microsoft.UI.Reactor.Tests.Spec048;

/// <summary>
/// Spec-048 §3.4 / issue #486 — clear failure mode + the public opt-in
/// <see cref="ReactorApp.RegisterAllBuiltIns"/> catalog registration.
///
/// <para>Once the eager <c>RegisterV1BuiltInHandlers</c> bootstrap was
/// deleted, an element record whose handler was never registered (e.g. a
/// direct-record construction that bypassed its factory) would otherwise
/// silently no-op-mount as <c>null</c>. The reconciler now throws an
/// actionable <see cref="InvalidOperationException"/> instead.</para>
/// </summary>
public sealed class UnregisteredHandlerAndRegisterAllBuiltInsTests
{
    private static readonly Action NoOp = static () => { };

    // A bespoke element type that no factory and no registration path ever
    // touches — so it misses all dispatch arms and trips the throw. (The test
    // assembly's module-initializer registers every *built-in* handler, so a
    // built-in element type could not exercise this path.)
    private sealed record UnregisteredProbeElement(string Label) : Element;

    [Fact]
    public void Mount_Of_Unregistered_Element_Throws_Actionable_InvalidOperationException()
    {
        var reconciler = new Reconciler();

        var ex = Assert.Throws<InvalidOperationException>(
            () => reconciler.Mount(new UnregisteredProbeElement("x"), NoOp));

        // Names the concrete element type.
        Assert.Contains(nameof(UnregisteredProbeElement), ex.Message);
        // Points at both/all remediation paths required by issue #486.
        Assert.Contains("factory", ex.Message);
        Assert.Contains("RegisterAllBuiltIns", ex.Message);
        Assert.Contains("ControlRegistry.Register", ex.Message);
    }

    [Fact]
    public void Mount_Of_EmptyElement_Does_Not_Throw()
    {
        // EmptyElement is a legitimately handler-less sentinel — the throw
        // must not fire for it.
        var reconciler = new Reconciler();
        var control = reconciler.Mount(new EmptyElement(), NoOp);
        Assert.Null(control);
    }

    // ── Issue #486 catalog-drift guard ──────────────────────────────────────
    //
    // `ReactorApp.RegisterAllBuiltIns()` is a hand-maintained imperative list of
    // every built-in handler/descriptor (see ReactorApp.BuiltIns.cs). The real
    // failure mode is that list silently falling out of sync with the framework's
    // actual built-in controls — a built-in dropped from the catalog (or one of
    // its registration touches silently no-op'ing) would let a direct-record
    // mount throw at runtime with no compile/test signal.
    //
    // `ExpectedBuiltInCatalog` below is the explicit, reviewed mirror of that
    // catalog: one entry per registration touch (element record type, or the
    // base type for the base-derived `RegisterForDerivedTypes` registrations).
    // The guards make drift fail the build, not pass silently:
    //   (1) RegisterAllBuiltIns_Registers_Exactly_The_Mirror_Catalog — the real
    //       production-drift guard. It compares this mirror against the set of
    //       built-in element types `RegisterAllBuiltIns()` *actually* registered,
    //       as captured by the test bootstrap at module-init time
    //       (BuiltInHandlerBootstrap.RegisteredBuiltInElementTypes). That snapshot
    //       is taken immediately after the single bulk-registration call and
    //       before any test runs, so a later factory touch can't backfill a
    //       dropped built-in and mask the drift. Adding a new built-in to
    //       ReactorApp.BuiltIns.cs without updating the mirror, or removing one
    //       from production while the mirror still lists it, fails loudly with the
    //       exact symmetric difference. The registry holds only handler-backed
    //       built-ins — component-style built-ins (DataGrid, PropertyGrid,
    //       MaskedTextBox, VirtualList, …) render via Component and are never
    //       registered — so the comparison is exact.
    //   (2) RegisterAllBuiltIns_Registers_Every_Catalog_Entry — every mirror
    //       entry must resolve a handler through some dispatch arm (exercises the
    //       base-walk resolution semantics, not just registry presence), so a
    //       registration that records but fails to resolve also fails loudly.
    //
    // Keep this list in lockstep with `ReactorApp.BuiltIns.cs`.

    private static readonly Type[] ExpectedBuiltInCatalog =
    {
        // Descriptor-backed value controls + explicit Reg<> handlers.
        typeof(ToggleSwitchElement),
        typeof(SliderElement),
        typeof(TextBoxElement),
        typeof(BorderElement),
        typeof(ViewboxElement),
        typeof(ProgressRingElement),
        typeof(ProgressElement),
        typeof(ListViewElement),
        typeof(NavigationHostElement),
        typeof(GridViewElement),

        // Overlay / modal decorator handlers.
        typeof(ContentDialogElement),
        typeof(FlyoutElement),
        typeof(MenuBarElement),
        typeof(CommandBarElement),
        typeof(MenuFlyoutElement),
        typeof(PopupElement),
        typeof(CommandBarFlyoutElement),
        typeof(ButtonElement),

        // Composite / validation decorators.
        typeof(CommandHostElement),
        typeof(ValidationNs.FormFieldElement),
        typeof(ValidationNs.ValidationVisualizerElement),
        typeof(ValidationNs.ValidationRuleElement),

        // Base-derived (RegisterForDerivedTypes) registrations — resolved via
        // ControlRegistry.ContainsBase on the base type.
        typeof(TemplatedListElementBase),
        typeof(LazyStackElementBase),
        typeof(TemplatedTreeViewElementBase),
        typeof(ItemsRepeaterElementBase),
        typeof(ItemsViewElementBase),

        // Standard concrete descriptors (alphabetical, mirrors BuiltIns.cs).
        typeof(AnimatedIconElement),
        typeof(AnimatedVisualPlayerElement),
        typeof(AnnotatedScrollBarElement),
        typeof(HooksNs.AnnounceRegionElement),
        typeof(AutoSuggestBoxElement),
        typeof(CalendarDatePickerElement),
        typeof(CalendarViewElement),
        typeof(CanvasElement),
        typeof(CheckBoxElement),
        typeof(ColorPickerElement),
        typeof(ComboBoxElement),
        typeof(BreadcrumbBarElement),
        typeof(SelectorBarElement),
        typeof(SwipeControlElement),
        typeof(SemanticZoomElement),
        typeof(DatePickerElement),
        typeof(DropDownButtonElement),
        typeof(EllipseElement),
        typeof(ExpanderElement),
        typeof(FlexElement),
        typeof(FlipViewElement),
        typeof(FrameElement),
        typeof(GridElement),
        typeof(HyperlinkButtonElement),
        typeof(ImageElement),
        typeof(InfoBadgeElement),
        typeof(InfoBarElement),
        typeof(ItemContainerElement),
        typeof(LineElement),
        typeof(ListBoxElement),
        typeof(MapControlElement),
        typeof(MediaPlayerElementElement),
        typeof(NavigationViewElement),
        typeof(NumberBoxElement),
        typeof(ParallaxViewElement),
        typeof(PasswordBoxElement),
        typeof(PathElement),
        typeof(PersonPictureElement),
        typeof(PipsPagerElement),
        typeof(PivotElement),
        typeof(RadioButtonElement),
        typeof(RadioButtonsElement),
        typeof(RatingControlElement),
        typeof(RectangleElement),
        typeof(RefreshContainerElement),
        typeof(RelativePanelElement),
        typeof(RepeatButtonElement),
        typeof(RichEditBoxElement),
        typeof(RichTextBlockElement),
        typeof(ScrollViewElement),
        typeof(ScrollViewerElement),
        typeof(SemanticElement),
        typeof(SplitButtonElement),
        typeof(SplitViewElement),
        typeof(StackElement),
        typeof(TabViewElement),
        typeof(TeachingTipElement),
        typeof(TextBlockElement),
        typeof(TimePickerElement),
        typeof(TitleBarElement),
        typeof(ToggleButtonElement),
        typeof(ToggleSplitButtonElement),
        typeof(TreeViewElement),
        typeof(WebView2Element),
        typeof(WrapGridElement),

        // Polymorphic / generated decorators that self-register on type load.
        typeof(IconElement),
        typeof(HostingNs.XamlPageElement),
        typeof(HostingNs.XamlHostElement),
    };

    public static IEnumerable<object[]> CatalogTypes()
        => ExpectedBuiltInCatalog.Select(static t => new object[] { t });

    [Fact]
    public void RegisterAllBuiltIns_Registers_Exactly_The_Mirror_Catalog()
    {
        // The mirror must have no accidental duplicate entries.
        Assert.Equal(ExpectedBuiltInCatalog.Length, ExpectedBuiltInCatalog.Distinct().Count());

        // Compare the mirror against what RegisterAllBuiltIns() actually
        // registered, as snapshotted by the test bootstrap at module-init time —
        // before any test exercised a factory. Reading that early snapshot
        // (instead of the live process-wide registry at test-run time) is what
        // closes the masking gap: a built-in dropped from RegisterAllBuiltIns()
        // can't be hidden by an unrelated test having since lazily registered the
        // same built-in via its factory. Component-style built-ins render via
        // Component and never register, so the snapshot is exactly the
        // handler-backed built-in catalog.
        var actual = BuiltInHandlerBootstrap.RegisteredBuiltInElementTypes.ToHashSet();
        var expected = ExpectedBuiltInCatalog.ToHashSet();

        // Surface the exact drift so a failure is self-explaining.
        var missingFromMirror = actual.Except(expected)
            .Select(t => t.FullName).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        var staleInMirror = expected.Except(actual)
            .Select(t => t.FullName).OrderBy(n => n, StringComparer.Ordinal).ToArray();

        Assert.True(
            missingFromMirror.Length == 0,
            "ReactorApp.RegisterAllBuiltIns() registered built-in element type(s) not listed in " +
            "ExpectedBuiltInCatalog — a new built-in was added to ReactorApp.BuiltIns.cs without " +
            "updating this mirror. Add:\n  " + string.Join("\n  ", missingFromMirror));

        Assert.True(
            staleInMirror.Length == 0,
            "ExpectedBuiltInCatalog lists element type(s) that RegisterAllBuiltIns() no longer " +
            "registers — a built-in was removed/renamed in ReactorApp.BuiltIns.cs without updating " +
            "this mirror. Remove:\n  " + string.Join("\n  ", staleInMirror));
    }

    [Theory]
    [MemberData(nameof(CatalogTypes))]
    public void RegisterAllBuiltIns_Registers_Every_Catalog_Entry(Type elementType)
    {
        // Idempotent + process-wide: safe to call (the test bootstrap already
        // called it once via [ModuleInitializer]).
        ReactorApp.RegisterAllBuiltIns();

        // "Registered in any arm": an exact-type entry (Contains), a base-derived
        // entry on this base type (ContainsBase), or a type whose ancestor is a
        // base-derived entry (ContainsForType). A genuine catalog drop trips none
        // of these and fails loudly.
        var registered =
            ControlRegistry.Contains(elementType)
            || ControlRegistry.ContainsBase(elementType)
            || ControlRegistry.ContainsForType(elementType);

        Assert.True(
            registered,
            $"'{elementType.FullName}' is listed in the RegisterAllBuiltIns catalog but no handler " +
            "resolved for it after RegisterAllBuiltIns(). Either its registration touch in " +
            "ReactorApp.BuiltIns.cs was dropped/renamed, or this entry no longer belongs in the catalog.");
    }

    [Fact]
    public void RegisterAllBuiltIns_Is_Idempotent()
    {
        // Second call must be a cheap no-op, not a throw.
        ReactorApp.RegisterAllBuiltIns();
        ReactorApp.RegisterAllBuiltIns();

        // Representative spread of registration shapes: a descriptor value
        // control, a decorator, and a base-derived list type.
        Assert.True(ControlRegistry.ContainsForType(typeof(TextBlockElement)));
        Assert.True(ControlRegistry.ContainsForType(typeof(ButtonElement)));
        Assert.True(ControlRegistry.ContainsForType(typeof(ListViewElement)));
    }
}
