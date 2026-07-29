using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls.Primitives;
using Xunit;
using static Microsoft.UI.Reactor.Factories;

namespace Microsoft.UI.Reactor.Tests;

/// <summary>
/// Tests for the <c>ToolTipService</c> attached-property modifiers —
/// <c>.ToolTipPlacement()</c>, <c>.ToolTipPlacementTarget()</c> and the
/// placement-bearing <c>.ToolTip()</c> / <c>.WithToolTip()</c> overloads.
/// Pure record/equality assertions — <c>PlacementMode</c> is a WinRT enum, so no
/// WinUI object is constructed and these stay headless.
/// </summary>
public class ToolTipPlacementModifierTests
{
    // ── Fluent surface ──────────────────────────────────────────────

    [Fact]
    public void ToolTipPlacement_Sets_Only_Placement()
    {
        var el = Button("hover", () => { }).ToolTipPlacement(PlacementMode.Right);

        Assert.Equal(PlacementMode.Right, el.Modifiers?.ToolTipPlacement);
        // The standalone modifier must not invent tooltip content — it composes
        // with whichever of .ToolTip()/.WithToolTip() the caller also applied.
        Assert.Null(el.Modifiers?.ToolTip);
        Assert.Null(el.Modifiers?.RichToolTip);
    }

    [Fact]
    public void ToolTipPlacement_Preserves_Concrete_Element_Type()
    {
        var el = Button("hover", () => { }).ToolTipPlacement(PlacementMode.Left);
        Assert.IsType<ButtonElement>(el);
    }

    [Fact]
    public void ToolTip_With_Placement_Sets_Both_Text_And_Placement()
    {
        var el = Button("save", () => { }).ToolTip("Save (Ctrl+S)", PlacementMode.Bottom);

        Assert.Equal("Save (Ctrl+S)", el.Modifiers?.ToolTip);
        Assert.Equal(PlacementMode.Bottom, el.Modifiers?.ToolTipPlacement);
    }

    [Fact]
    public void ToolTip_Without_Placement_Leaves_Placement_Unset()
    {
        // Guards the single-arg overload against accidentally hard-coding a
        // placement: unset must stay unset so WinUI's own default applies.
        var el = Button("save", () => { }).ToolTip("Save");

        Assert.Equal("Save", el.Modifiers?.ToolTip);
        Assert.Null(el.Modifiers?.ToolTipPlacement);
    }

    [Fact]
    public void ToolTip_Placement_Overload_Round_Trips_Each_Value_Distinctly()
    {
        // Differential isolation: if the overload dropped its argument and always
        // stored one constant, these two would compare equal.
        var left = Button("a", () => { }).ToolTip("tip", PlacementMode.Left);
        var right = Button("a", () => { }).ToolTip("tip", PlacementMode.Right);

        Assert.NotEqual(left.Modifiers?.ToolTipPlacement, right.Modifiers?.ToolTipPlacement);
        Assert.Equal(PlacementMode.Left, left.Modifiers?.ToolTipPlacement);
        Assert.Equal(PlacementMode.Right, right.Modifiers?.ToolTipPlacement);
    }

    [Fact]
    public void WithToolTip_With_Placement_Sets_Rich_Content_And_Placement()
    {
        var content = VStack(TextBlock("title"), TextBlock("detail"));
        var el = Button("hover", () => { }).WithToolTip(content, PlacementMode.Top);

        Assert.Same(content, el.Modifiers?.RichToolTip);
        Assert.Equal(PlacementMode.Top, el.Modifiers?.ToolTipPlacement);
        Assert.Null(el.Modifiers?.ToolTip);
    }

    [Fact]
    public void ToolTipPlacementTarget_Sets_Reference_Slot()
    {
        var typed = TypedElementRef.Create<FrameworkElement>();
        ElementRef target = typed;

        var el = Button("hover", () => { }).ToolTipPlacementTarget(typed);

        Assert.Same(target, el.Modifiers?.ToolTipPlacementTargetRef);
    }

    [Fact]
    public void Placement_Composes_With_Rich_ToolTip_Across_Separate_Calls()
    {
        var content = TextBlock("rich");
        var el = Button("hover", () => { })
            .WithToolTip(content)
            .ToolTipPlacement(PlacementMode.Mouse);

        Assert.Same(content, el.Modifiers?.RichToolTip);
        Assert.Equal(PlacementMode.Mouse, el.Modifiers?.ToolTipPlacement);
    }

    // ── Merge ───────────────────────────────────────────────────────

    [Fact]
    public void Merge_Prefers_Other_Placement_And_Fills_Gaps_From_Base()
    {
        var baseMods = new ElementModifiers { ToolTip = "base tip", ToolTipPlacement = PlacementMode.Left };
        var other = new ElementModifiers { ToolTipPlacement = PlacementMode.Right };

        var merged = baseMods.Merge(other);

        Assert.Equal(PlacementMode.Right, merged.ToolTipPlacement);  // other wins
        Assert.Equal("base tip", merged.ToolTip);                    // base fills the gap
    }

    [Fact]
    public void Merge_Preserves_Base_Placement_When_Other_Leaves_It_Unset()
    {
        var baseMods = new ElementModifiers { ToolTipPlacement = PlacementMode.Bottom };
        var other = new ElementModifiers { Width = 100 };

        var merged = baseMods.Merge(other);

        Assert.Equal(PlacementMode.Bottom, merged.ToolTipPlacement);
        Assert.Equal(100, merged.Width);
    }

    [Fact]
    public void Merge_Prefers_Other_PlacementTarget_And_Fills_Gaps_From_Base()
    {
        var baseTarget = new ElementRef();
        var otherTarget = new ElementRef();

        var fromOther = new ElementModifiers { ToolTipPlacementTargetRef = baseTarget }
            .Merge(new ElementModifiers { ToolTipPlacementTargetRef = otherTarget });
        var fromBase = new ElementModifiers { ToolTipPlacementTargetRef = baseTarget }
            .Merge(new ElementModifiers { Height = 20 });

        Assert.Same(otherTarget, fromOther.ToolTipPlacementTargetRef);
        Assert.Same(baseTarget, fromBase.ToolTipPlacementTargetRef);
    }

    // ── Skip-path equality ──────────────────────────────────────────

    [Fact]
    public void ModifiersEqual_Distinguishes_Placement()
    {
        // Isolation: the two records differ only by ToolTipPlacement. If the
        // field is dropped from ModifiersEqual the reconciler would take the
        // skip path and never re-apply ToolTipService.Placement.
        var a = new ElementModifiers { ToolTip = "tip", ToolTipPlacement = PlacementMode.Left };
        var b = new ElementModifiers { ToolTip = "tip", ToolTipPlacement = PlacementMode.Right };
        var same = new ElementModifiers { ToolTip = "tip", ToolTipPlacement = PlacementMode.Left };

        Assert.False(Element.ModifiersEqual(a, b));
        Assert.True(Element.ModifiersEqual(a, same));
    }

    [Fact]
    public void ModifiersEqual_Distinguishes_Placement_Set_Versus_Unset()
    {
        var set = new ElementModifiers { ToolTip = "tip", ToolTipPlacement = PlacementMode.Top };
        var unset = new ElementModifiers { ToolTip = "tip" };

        Assert.False(Element.ModifiersEqual(set, unset));
        Assert.False(Element.ModifiersEqual(unset, set));
    }

    [Fact]
    public void ModifiersEqual_Distinguishes_PlacementTarget_Identity()
    {
        var first = new ElementRef();
        var second = new ElementRef();

        var a = new ElementModifiers { ToolTipPlacementTargetRef = first };
        var b = new ElementModifiers { ToolTipPlacementTargetRef = second };
        var same = new ElementModifiers { ToolTipPlacementTargetRef = first };

        Assert.False(Element.ModifiersEqual(a, b));
        Assert.False(Element.ModifiersEqual(a, new ElementModifiers()));
        Assert.True(Element.ModifiersEqual(a, same));
    }
}
