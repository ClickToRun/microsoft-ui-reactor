using System.Collections.Generic;

namespace Microsoft.UI.Reactor.Analyzers;

/// <summary>
/// How a <c>.Set(x =&gt; x.PROP = v)</c> write maps onto a Reactor fluent modifier, and
/// under what conditions suggesting that modifier is actually sound.
/// </summary>
internal sealed class ModifierInfo
{
    internal ModifierInfo(
        string modifier,
        bool poolReset = false,
        string[]? controlGate = null,
        string[]? elementTypes = null)
    {
        Modifier = modifier;
        PoolReset = poolReset;
        ControlGate = controlGate;
        ElementTypes = elementTypes;
    }

    /// <summary>Name of the fluent modifier method to suggest.</summary>
    public string Modifier { get; }

    /// <summary>
    /// True when <c>ElementPool.CleanElement</c> resets this property, so an imperative
    /// <c>.Set</c> write is silently lost on pool reuse. Selects the higher-severity
    /// <c>REACTOR_POOL_001</c>; everything else reports <c>REACTOR_MOD_002</c>.
    /// </summary>
    public bool PoolReset { get; }

    /// <summary>
    /// WinUI control types that <c>ApplyModifiers</c> actually writes this modifier to, or
    /// <c>null</c> when it is applied unconditionally to the <c>FrameworkElement</c>.
    /// <para>
    /// Only needed where WinUI declares the dependency property on <em>more</em> types than
    /// the reconciler handles. On anything outside this list the modifier compiles and
    /// silently does nothing, so the suggestion must be withheld.
    /// </para>
    /// </summary>
    public string[]? ControlGate { get; }

    /// <summary>
    /// Reactor element types that declare a type-specific overload of this modifier, or
    /// <c>null</c> when the modifier is a generic <c>T Foo&lt;T&gt;(this T el, …)</c>.
    /// <para>
    /// A name-keyed rewrite would emit a call that does not compile on any other receiver,
    /// so the element type is checked before the fix is offered.
    /// </para>
    /// <para>
    /// When <see cref="ControlGate"/> is also set the two are <b>OR'd</b>, not AND'd: they
    /// describe two independent routes to a sound rewrite — the generic modifier reaching this
    /// receiver at runtime, or a type-specific overload existing for this element type. Fonts
    /// need both, because <c>ApplyModifiers</c> only writes the generic path to
    /// <c>Control</c>/<c>TextBlock</c> while <c>RichTextBlockElement</c> carries its own
    /// overloads.
    /// </para>
    /// </summary>
    public string[]? ElementTypes { get; }
}

/// <summary>
/// The single source of truth for "this property has a fluent modifier, prefer it over
/// <c>.Set</c>" — consumed by <see cref="PoolResetSetAnalyzer"/> and
/// <see cref="PoolResetSetCodeFix"/>.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately one table rather than one per diagnostic. Two parallel lists is how the
/// original pool-reset list went stale: a modifier was added, nobody thought to update the
/// analyzer, and the rule silently stopped covering the properties people were actually
/// writing through <c>.Set</c>. Entries carry their own metadata so a new property is one
/// row here rather than an edit in several places.
/// </para>
/// <para>
/// <c>ModifierTableIntegrityTests</c> reflects over this table and over
/// <c>Microsoft.UI.Reactor.ElementExtensions</c> to keep it honest — every entry must name
/// a modifier that exists, every element type must really declare it, and any new generic
/// modifier matching a settable WinUI dependency property must be either listed here or
/// explicitly excluded with a reason.
/// </para>
/// <para><b>Two reasons a property belongs here.</b> Both make <c>.Set</c> the wrong tool,
/// but they differ in severity:</para>
/// <list type="number">
/// <item><description><see cref="ModifierInfo.PoolReset"/> — <c>ElementPool.CleanElement</c>
/// clears the property on return, so the imperative write is <em>lost</em> on pool reuse.
/// A real bug with a visible symptom → <c>REACTOR_POOL_001</c>, Warning.</description></item>
/// <item><description>Everything else — the write works, but <c>Element.SettersEqual</c> is
/// <c>ReferenceEquals(a,b) || both-empty</c>, so any element carrying setters is forced onto
/// the reconciler's update path every render, and the value is never unwound when a later
/// render drops it → <c>REACTOR_MOD_002</c>, Info.</description></item>
/// </list>
/// </remarks>
internal static class ModifierTable
{
    // Type groups, named once so the intent is legible at each use site.
    private static readonly string[] ControlBorderStack = { "Control", "Border", "StackPanel" };
    private static readonly string[] ControlBorder = { "Control", "Border" };
    private static readonly string[] PanelControlBorder = { "Panel", "Control", "Border" };
    private static readonly string[] ControlOrTextBlock = { "Control", "TextBlock" };
    private static readonly string[] RichTextBlockOnly = { "RichTextBlockElement" };
    private static readonly string[] TextOrRichTextBlock = { "TextBlockElement", "RichTextBlockElement" };

    /// <summary>
    /// Property name → modifier mapping. Keyed by the WinUI property name as written inside
    /// the <c>.Set</c> lambda.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, ModifierInfo> Properties =
        new Dictionary<string, ModifierInfo>(System.StringComparer.Ordinal)
        {
            // ── Pool-reset (REACTOR_POOL_001, Warning) ───────────────────────────────
            // Reset in ElementPool.CleanElement; all applied unconditionally to `fe`.
            { "Margin",              new ModifierInfo("Margin",              poolReset: true) },
            { "Width",               new ModifierInfo("Width",               poolReset: true) },
            { "Height",              new ModifierInfo("Height",              poolReset: true) },
            { "MinWidth",            new ModifierInfo("MinWidth",            poolReset: true) },
            { "MinHeight",           new ModifierInfo("MinHeight",           poolReset: true) },
            { "MaxWidth",            new ModifierInfo("MaxWidth",            poolReset: true) },
            { "MaxHeight",           new ModifierInfo("MaxHeight",           poolReset: true) },
            { "HorizontalAlignment", new ModifierInfo("HorizontalAlignment", poolReset: true) },
            { "VerticalAlignment",   new ModifierInfo("VerticalAlignment",   poolReset: true) },
            { "Opacity",             new ModifierInfo("Opacity",             poolReset: true) },
            { "AccessKey",           new ModifierInfo("AccessKey",           poolReset: true) },
            { "IsTabStop",           new ModifierInfo("IsTabStop",           poolReset: true) },

            // ── Generic modifier, no runtime gate (REACTOR_MOD_002, Info) ────────────
            // IsEnabled and the content-alignment pair ARE Control-gated in ApplyModifiers,
            // but WinUI declares those DPs only on Control — if the .Set lambda compiles the
            // receiver already qualifies, so no predicate is needed.
            { "IsEnabled",                  new ModifierInfo("IsEnabled") },
            { "HorizontalContentAlignment", new ModifierInfo("HorizontalContentAlignment") },
            { "VerticalContentAlignment",   new ModifierInfo("VerticalContentAlignment") },

            // ── Generic modifier, control-gated (REACTOR_MOD_002, Info) ──────────────
            // WinUI declares these on Panel subclasses too, which ApplyModifiers skips. The
            // allow-lists genuinely differ: StackPanel takes Padding but not CornerRadius;
            // Grid takes Background but not Padding.
            { "Padding",         new ModifierInfo("Padding",         controlGate: ControlBorderStack) },
            { "CornerRadius",    new ModifierInfo("CornerRadius",    controlGate: ControlBorder) },
            { "BorderThickness", new ModifierInfo("BorderThickness", controlGate: ControlBorder) },
            { "BorderBrush",     new ModifierInfo("BorderBrush",     controlGate: ControlBorder) },
            { "Background",      new ModifierInfo("Background",      controlGate: PanelControlBorder) },

            // Fonts have BOTH a generic modifier and type-specific overloads, and the two
            // cover different receivers — so the gates are OR'd (see ModifierInfo.ElementTypes).
            // The generic path only reaches Control|TextBlock in ApplyModifiers; RichTextBlock
            // is neither, yet exposes the same DPs, so `.FontSize(n)` there would bind the
            // generic modifier and write nothing. The RichTextBlockElement overloads are what
            // make the suggestion sound on that receiver — FontSize's was added alongside this
            // table for exactly that reason.
            { "FontFamily", new ModifierInfo("FontFamily", controlGate: ControlOrTextBlock, elementTypes: TextOrRichTextBlock) },
            { "FontSize",   new ModifierInfo("FontSize",   controlGate: ControlOrTextBlock, elementTypes: TextOrRichTextBlock) },
            { "FontWeight", new ModifierInfo("FontWeight", controlGate: ControlOrTextBlock, elementTypes: RichTextBlockOnly) },
            { "Foreground", new ModifierInfo("Foreground", controlGate: ControlOrTextBlock, elementTypes: RichTextBlockOnly) },

            // ── Type-specific modifiers (REACTOR_MOD_002, Info) ──────────────────────
            // No generic overload exists, so the rewrite only compiles on these element
            // types. Lists are verified against ElementExtensions*.cs by
            // ModifierTableIntegrityTests.
            { "TextWrapping", new ModifierInfo("TextWrapping",
                elementTypes: new[] { "TextBlockElement", "TextBoxElement", "RichEditBoxElement" }) },
            { "TextTrimming", new ModifierInfo("TextTrimming",
                elementTypes: new[] { "TextBlockElement", "RichTextBlockElement" }) },
            { "MaxLines", new ModifierInfo("MaxLines",
                elementTypes: new[] { "TextBlockElement", "RichTextBlockElement" }) },
            { "LineHeight", new ModifierInfo("LineHeight",
                elementTypes: new[] { "TextBlockElement", "RichTextBlockElement" }) },
            { "CharacterSpacing", new ModifierInfo("CharacterSpacing",
                elementTypes: new[] { "TextBlockElement", "RichTextBlockElement" }) },
            { "FontStyle", new ModifierInfo("FontStyle",
                elementTypes: new[] { "TextBlockElement", "RichTextBlockElement" }) },
            { "TextAlignment", new ModifierInfo("TextAlignment",
                elementTypes: new[] { "TextBlockElement", "RichTextBlockElement", "TextBoxElement" }) },
            { "IsTextSelectionEnabled", new ModifierInfo("IsTextSelectionEnabled",
                elementTypes: new[] { "TextBlockElement" }) },
            { "AcceptsReturn", new ModifierInfo("AcceptsReturn",
                elementTypes: new[] { "TextBoxElement", "RichEditBoxElement" }) },
            { "IsSpellCheckEnabled", new ModifierInfo("IsSpellCheckEnabled",
                elementTypes: new[] { "TextBoxElement", "RichEditBoxElement" }) },
            { "MaxLength", new ModifierInfo("MaxLength",
                elementTypes: new[] { "TextBoxElement", "RichEditBoxElement", "PasswordBoxElement" }) },
            { "IsReadOnly", new ModifierInfo("IsReadOnly",
                elementTypes: new[] { "TextBoxElement", "RatingControlElement" }) },
            { "CharacterCasing", new ModifierInfo("CharacterCasing",
                elementTypes: new[] { "TextBoxElement" }) },
            { "PasswordRevealMode", new ModifierInfo("PasswordRevealMode",
                elementTypes: new[] { "PasswordBoxElement" }) },
            { "PlaceholderText", new ModifierInfo("PlaceholderText",
                elementTypes: new[]
                {
                    "TextBoxElement", "PasswordBoxElement", "NumberBoxElement", "ComboBoxElement",
                    "AutoSuggestBoxElement", "CalendarDatePickerElement", "RichEditBoxElement",
                }) },
            { "SelectionMode", new ModifierInfo("SelectionMode",
                elementTypes: new[] { "ListViewElement", "GridViewElement" }) },

            // Rich-text typography. Surfaced by the type-specific staleness test — each has a
            // modifier whose parameter type is the property's own type, so the rewrite is a
            // straight pass-through.
            { "FontStretch", new ModifierInfo("FontStretch",
                elementTypes: new[] { "RichTextBlockElement", "RichTextParagraph", "RichTextRun", "RichTextHyperlink" }) },
            { "TextDecorations", new ModifierInfo("TextDecorations",
                elementTypes: new[] { "TextBlockElement", "RichTextBlockElement", "RichTextParagraph", "RichTextRun", "RichTextHyperlink" }) },
            { "Language", new ModifierInfo("Language",
                elementTypes: new[] { "RichTextParagraph", "RichTextRun", "RichTextHyperlink" }) },
            { "HorizontalTextAlignment", new ModifierInfo("HorizontalTextAlignment",
                elementTypes: new[] { "RichTextBlockElement", "RichTextParagraph" }) },
            { "LineStackingStrategy", new ModifierInfo("LineStackingStrategy",
                elementTypes: new[] { "RichTextBlockElement", "RichTextParagraph" }) },
            { "SelectionHighlightColor", new ModifierInfo("SelectionHighlightColor",
                elementTypes: new[] { "RichTextBlockElement", "RichEditBoxElement" }) },
            { "IsColorFontEnabled", new ModifierInfo("IsColorFontEnabled",
                elementTypes: new[] { "RichTextBlockElement" }) },
            { "OpticalMarginAlignment", new ModifierInfo("OpticalMarginAlignment",
                elementTypes: new[] { "RichTextBlockElement" }) },
            { "TextLineBounds", new ModifierInfo("TextLineBounds",
                elementTypes: new[] { "RichTextBlockElement" }) },
            { "TextReadingOrder", new ModifierInfo("TextReadingOrder",
                elementTypes: new[] { "RichTextBlockElement" }) },
            { "ContentTransitions", new ModifierInfo("ContentTransitions",
                elementTypes: new[] { "ExpanderElement" }) },
        };

    /// <summary>
    /// Modifiers that <c>Reconciler.ApplyModifiers</c> gates on a control type but that carry
    /// <see cref="ModifierInfo.ControlGate"/> <see langword="null"/> here (or no
    /// <see cref="Properties"/> entry at all), with the reason.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A null <see cref="ModifierInfo.ControlGate"/> is ambiguous between "the reconciler applies
    /// this unconditionally" and "the reconciler gates it, but this rule's direction cannot reach a
    /// non-qualifying receiver anyway". <c>REACTOR_MOD_002</c> reads <c>.Set(x =&gt; x.IsEnabled = v)</c>,
    /// where the lambda parameter is already a <c>Control</c> because WinUI declares the dependency
    /// property only there — so no predicate is needed. <see cref="NoOpModifierAnalyzer"/> reads
    /// <c>.IsEnabled(v)</c>, a generic modifier callable on <em>any</em> element, where the same
    /// null would mean "never report" and quietly lose real findings.
    /// </para>
    /// <para>
    /// <c>ModifierTableIntegrityTests</c> requires every control gate it reads out of
    /// <c>ApplyModifiers</c> to match a declared <see cref="ModifierInfo.ControlGate"/> or appear
    /// here, so a newly gated modifier forces a deliberate decision in both directions instead of
    /// being invisible to one of them. The converse also holds: every row here must name a gate the
    /// reader actually finds, so the list cannot accumulate stale entries that silently suppress
    /// that check.
    /// </para>
    /// <para>
    /// One gate is deliberately absent: the content-alignment pair is written under a bare
    /// <c>if (fe is Control …)</c> with no <c>m.&lt;Prop&gt;</c> in the condition, so the gate reader —
    /// which ties a type test to the modifier guarding it — cannot attribute it to a property name.
    /// Recording it here would claim a gate the reader found, which it did not; the null-gate
    /// rationale for that pair is documented at its <see cref="Properties"/> entry instead.
    /// </para>
    /// </remarks>
    public static readonly IReadOnlyDictionary<string, string> GateOnlyInReconciler =
        new Dictionary<string, string>(System.StringComparer.Ordinal)
        {
            ["IsEnabled"] = "Control-gated in ApplyModifiers. Mapped with a null ControlGate because WinUI declares IsEnabled on Control only, so a .Set lambda that compiles already qualifies. REACTOR_MOD_003 therefore does not report .IsEnabled(...) — declaring the gate here would be the way to turn that on.",
            ["TabIndex"] = "Control-gated in ApplyModifiers but unmapped in Properties (see DeliberatelyExcluded) — WinUI also declares TabIndex on UIElement, so the gate needs verifying before either direction uses it.",
            ["ElementSoundMode"] = "Control-gated in ApplyModifiers and unmapped in Properties: there is no .ElementSoundMode modifier to suggest, and the generic .ElementSoundMode(...) path has not been audited for the reverse direction.",

            // Reactor-only BiDi logical modifiers. Both fold into a physical write
            // (PaddingInlineStart → Padding, BorderInlineStart → BorderThickness) and inherit that
            // write's control gate, so `.PaddingInlineStart(8)` on a Grid is dropped exactly like
            // `.Padding(8)` is. They are NOT WinUI property names, and Properties is keyed by the
            // name written inside a .Set lambda — so mapping them there would add rows REACTOR_MOD_002
            // can never match. Covering them in REACTOR_MOD_003 needs a modifier-keyed gate table;
            // recorded here so the omission is deliberate rather than invisible.
            ["PaddingInlineStart"] = "Reactor-only BiDi logical modifier; folds into the Padding write and inherits its Control/Border/StackPanel gate. Not a WinUI property name, so it has no home in Properties (which is keyed on those). REACTOR_MOD_003 coverage needs a modifier-keyed table.",
            ["PaddingInlineEnd"] = "Reactor-only BiDi logical modifier, the mirror of PaddingInlineStart; same guard, same gate, same reasoning.",
            ["BorderInlineStart"] = "Reactor-only BiDi logical modifier; folds into the BorderThickness write and inherits its Control/Border gate. Not a WinUI property name — same reasoning as PaddingInlineStart. (There is no BorderInlineEnd modifier.)",
        };

    /// <summary>
    /// Properties intentionally absent from <see cref="Properties"/>, with the reason.
    /// <c>ModifierTableIntegrityTests</c> requires every candidate modifier to appear in one
    /// of the two, so adding a modifier forces a deliberate choice instead of silently
    /// widening the gap between the DSL and the analyzer.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> DeliberatelyExcluded =
        new Dictionary<string, string>(System.StringComparer.Ordinal)
        {
            ["Visibility"] = "Owned by REACTOR_VIS_001 — the modifier is .IsVisible(bool), an enum→bool translation that needs its own code fix.",
            ["RequestedTheme"] = "Owned by REACTOR_THEME_003 (RequestedThemeSetAnalyzer), which ships its own fix.",
            ["ItemsSource"] = "Owned by REACTOR_ITEMS_001 — the guidance is to pass items through the factory, not to swap in a modifier.",
            ["SelectedItem"] = "Owned by REACTOR_CTRL_001 — the fix removes the .Set rather than replacing it.",
            ["SelectedValue"] = "Owned by REACTOR_CTRL_001, as above.",
            ["Style"] = "The .ApplyStyle(name)/.AccentButton() modifiers are OnMount-based, so they are not equivalent to a .Set that re-applies every update.",
            ["Name"] = "No modifier exists. 154 .Set sites, all in selftest/E2E fixtures — adding a .Name(string) modifier is tracked separately.",
            ["BackgroundTransition"] = "The modifier takes a TimeSpan? duration and builds the BrushTransition itself; the property's value is a BrushTransition, so the rewrite would not type-check.",
            ["Content"] = "The .Content(Element) modifiers take a Reactor Element, not the native content object a .Set assigns.",
            ["Header"] = "Header modifiers take a string; a .Set may assign an arbitrary object.",
            ["Orientation"] = "Modifiers exist only for Slider/DatePicker; StackElement (the common .Set receiver) has none.",
            ["Spacing"] = "StackElement-only modifier; the property is also on native panels Reactor does not map.",
            ["Stretch"] = "Viewbox-only modifier.",
            ["FlowDirection"] = "Modifier exists only for RichTextRun, not for elements generally.",
            ["DisplayMode"] = "CalendarView-only modifier; the common .Set receiver is a SplitView.",
            ["IsTextScaleFactorEnabled"] = "Modifier exists for RichText* types but not TextBlockElement, the usual .Set receiver.",

            // No modifier exists at all. IsHitTestVisible is reset by ElementPool alongside
            // IsTabStop but is framework-internal (chart label/tick hiding, #162) with no
            // user-facing modifier — PoolResetSetConsistencyTests excludes it for the same
            // reason. Recorded here because a sweep report claimed a modifier existed; the
            // integrity test above is what caught that it does not.
            ["IsHitTestVisible"] = "No modifier exists; framework-internal, reset for chart-label hiding (#162).",

            // Transition helpers, not property assignments. `.ScaleTransition()` enables an
            // implicit composition animation; assigning the matching WinUI property through
            // .Set is a different operation, so the rewrite would not be equivalent.
            ["OpacityTransition"] = "Enables an implicit animation rather than assigning the property.",
            ["ScaleTransition"] = "Enables an implicit animation rather than assigning the property.",
            ["RotationTransition"] = "Enables an implicit animation rather than assigning the property.",
            ["TranslationTransition"] = "Enables an implicit animation rather than assigning the property.",

            // Signature mismatch: the modifier takes three floats, the WinUI property is a
            // Vector3, so passing the .Set right-hand side through would not compile.
            ["Translation"] = "Modifier takes (float x, float y, float z); the property is a Vector3.",

            // The XYFocus* modifiers take an ElementRef, not the FrameworkElement a .Set
            // assigns — same reasoning as PoolResetSetConsistencyTests.
            ["XYFocusUp"] = "Modifier takes ElementRef, not FrameworkElement.",
            ["XYFocusDown"] = "Modifier takes ElementRef, not FrameworkElement.",
            ["XYFocusLeft"] = "Modifier takes ElementRef, not FrameworkElement.",
            ["XYFocusRight"] = "Modifier takes ElementRef, not FrameworkElement.",

            ["Resources"] = "Modifier takes an Action<ResourceBuilder>; the property is a ResourceDictionary.",

            // Candidates, deliberately unmapped pending verification of how VisualModifiers
            // reach the control. Mapping one wrongly ships a code fix that compiles and does
            // nothing, which is the failure this table exists to prevent — so they stay out
            // until the application path is confirmed the way ApplyModifiers was.
            ["Scale"] = "Candidate: routed through VisualModifiers; application path not yet verified against a control-type gate.",
            ["Rotation"] = "Candidate: routed through VisualModifiers; application path not yet verified.",
            ["CenterPoint"] = "Candidate: routed through VisualModifiers; application path not yet verified.",
            ["TabIndex"] = "Candidate: Control-gated in ApplyModifiers, but WinUI also declares TabIndex on UIElement; needs the same gate treatment as Padding before mapping.",
            ["TabNavigation"] = "Candidate: Control-only property; not yet verified against ApplyModifiers.",
            ["XYFocusKeyboardNavigation"] = "Candidate: UIElement property; not yet verified against ApplyModifiers.",
        };
}
