using Microsoft.UI.Reactor.Analyzers;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;
using System.Threading.Tasks;
using Xunit;

namespace Microsoft.UI.Reactor.Tests.AnalyzerTests;

/// <summary>
/// Tests for <see cref="PoolResetSetAnalyzer"/> (<c>REACTOR_MOD_002</c>).
/// <para>
/// The gating tests are the point of this file. <c>ApplyModifiers</c> applies
/// <c>Padding</c>/<c>CornerRadius</c>/<c>BorderThickness</c>/<c>BorderBrush</c>/<c>Background</c>
/// only to specific runtime control types, while WinUI declares those DPs on more types than
/// that. Suggesting the modifier on a receiver the reconciler skips would produce a rewrite
/// that compiles and silently does nothing — the regression that had to be reverted from
/// ValueList.cs (a Grid) and CellComponent.cs (a TextBlock).
/// </para>
/// </summary>
public class ModifierAvailableAnalyzerTests
{
    private const string Stubs = @"
using System;
using Microsoft.UI.Reactor;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Microsoft.UI.Xaml
{
    public class UIElement { public bool IsHitTestVisible { get; set; } }
    public class FrameworkElement : UIElement { }
    public struct Thickness { public Thickness(double u) { } }
    public struct CornerRadius { public CornerRadius(double u) { } }
    public enum HorizontalAlignment { Left, Center, Right, Stretch }
}

namespace Microsoft.UI.Xaml.Media
{
    public class Brush { }
}

namespace Microsoft.UI.Xaml.Controls
{
    using Microsoft.UI.Xaml;
    using Microsoft.UI.Xaml.Media;

    // Padding/CornerRadius/Border* live on Control and Border in the reconciler's
    // allow-list, and additionally on Panel subclasses in WinUI (which it skips).
    public class Control : FrameworkElement
    {
        public Thickness Padding { get; set; }
        public CornerRadius CornerRadius { get; set; }
        public Thickness BorderThickness { get; set; }
        public Brush BorderBrush { get; set; }
        public Brush Background { get; set; }
        public bool IsEnabled { get; set; }
        public HorizontalAlignment HorizontalContentAlignment { get; set; }
    }

    public class Border : FrameworkElement
    {
        public Thickness Padding { get; set; }
        public CornerRadius CornerRadius { get; set; }
        public Brush Background { get; set; }
    }

    public class Panel : FrameworkElement
    {
        public Brush Background { get; set; }
    }

    // Grid is a Panel: Background applies, Padding/CornerRadius/Border* DO NOT.
    public class Grid : Panel
    {
        public Thickness Padding { get; set; }
        public CornerRadius CornerRadius { get; set; }
        public Thickness BorderThickness { get; set; }
    }

    // StackPanel is in Padding's allow-list but NOT CornerRadius's.
    public class StackPanel : Panel
    {
        public Thickness Padding { get; set; }
        public CornerRadius CornerRadius { get; set; }
    }

    public class Button : Control { }
}

namespace Microsoft.UI.Reactor
{
    using System;
    using Microsoft.UI.Xaml;
    using Microsoft.UI.Xaml.Controls;

    public record ButtonElement;
    public record GridElement;
    public record StackElement;
    public record BorderElement;

    public static class Ext
    {
        public static ButtonElement Set(this ButtonElement el, Action<Button> configure) => el;
        public static GridElement Set(this GridElement el, Action<Grid> configure) => el;
        public static StackElement Set(this StackElement el, Action<StackPanel> configure) => el;
        public static BorderElement Set(this BorderElement el, Action<Border> configure) => el;

        // Modifier stubs so the code-fix tests' FixedCode compiles.
        public static T IsEnabled<T>(this T el, bool enabled = true) => el;
        public static T Padding<T>(this T el, double uniform) => el;
        public static T Padding<T>(this T el, double l, double t, double r, double b) => el;
        public static T Background<T>(this T el, Microsoft.UI.Xaml.Media.Brush brush) => el;
        public static T HorizontalContentAlignment<T>(this T el, Microsoft.UI.Xaml.HorizontalAlignment a) => el;
    }
}
";

    // ---- ungated properties ----

    [Fact]
    public async Task Fires_For_IsEnabled()
    {
        var source = Stubs + @"
class C
{
    ButtonElement M(ButtonElement b) => {|REACTOR_MOD_002:b.Set(c => c.IsEnabled = false)|};
}";
        await new CSharpAnalyzerTest<PoolResetSetAnalyzer, DefaultVerifier>
        {
            TestCode = source,
        }.RunAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Fires_For_Each_Modifier_Backed_Write_In_A_Block()
    {
        // Two reportable writes in one body -> two diagnostics on the same invocation.
        var source = Stubs + @"
class C
{
    ButtonElement M(ButtonElement b) => {|REACTOR_MOD_002:{|REACTOR_MOD_002:b.Set(c => { c.IsEnabled = false; c.Padding = new Thickness(4); })|}|};
}";
        await new CSharpAnalyzerTest<PoolResetSetAnalyzer, DefaultVerifier>
        {
            TestCode = source,
        }.RunAsync(TestContext.Current.CancellationToken);
    }

    // ---- gating: the reason this analyzer needs a receiver check ----

    [Fact]
    public async Task Fires_For_Padding_On_Control()
    {
        var source = Stubs + @"
class C
{
    ButtonElement M(ButtonElement b) => {|REACTOR_MOD_002:b.Set(c => c.Padding = new Thickness(8))|};
}";
        await new CSharpAnalyzerTest<PoolResetSetAnalyzer, DefaultVerifier>
        {
            TestCode = source,
        }.RunAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Does_Not_Fire_For_Padding_On_Grid()
    {
        // ApplyModifiers applies Padding to Control/Border/StackPanel only. Grid is a Panel,
        // so '.Padding(...)' would compile and silently do nothing — staying on .Set is
        // correct here. This is the exact ValueList.cs regression.
        var source = Stubs + @"
class C
{
    GridElement M(GridElement g) => g.Set(x => x.Padding = new Thickness(8));
}";
        await new CSharpAnalyzerTest<PoolResetSetAnalyzer, DefaultVerifier>
        {
            TestCode = source,
        }.RunAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Does_Not_Fire_For_BorderThickness_On_Grid()
    {
        var source = Stubs + @"
class C
{
    GridElement M(GridElement g) => g.Set(x => x.BorderThickness = new Thickness(1));
}";
        await new CSharpAnalyzerTest<PoolResetSetAnalyzer, DefaultVerifier>
        {
            TestCode = source,
        }.RunAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Fires_For_Background_On_Grid()
    {
        // Background's allow-list DOES include Panel, so Grid is fine here. Proves the
        // gate is per-property rather than one shared predicate. Uses a non-null value so
        // the null guard cannot be what makes this pass.
        var source = Stubs + @"
class C
{
    GridElement M(GridElement g, Microsoft.UI.Xaml.Media.Brush brush)
        => {|REACTOR_MOD_002:g.Set(x => x.Background = brush)|};
}";
        await new CSharpAnalyzerTest<PoolResetSetAnalyzer, DefaultVerifier>
        {
            TestCode = source,
        }.RunAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Fires_For_Padding_On_StackPanel_But_Not_CornerRadius()
    {
        // StackPanel is in Padding's allow-list and not in CornerRadius's — the single
        // most confusing asymmetry in ApplyModifiers, and the reason a shared predicate
        // would be wrong.
        var source = Stubs + @"
class C
{
    StackElement A(StackElement s) => {|REACTOR_MOD_002:s.Set(x => x.Padding = new Thickness(4))|};
    StackElement B(StackElement s) => s.Set(x => x.CornerRadius = new CornerRadius(4));
}";
        await new CSharpAnalyzerTest<PoolResetSetAnalyzer, DefaultVerifier>
        {
            TestCode = source,
        }.RunAsync(TestContext.Current.CancellationToken);
    }

    // ---- near misses ----

    [Fact]
    public async Task Does_Not_Fire_For_Null_Assignment()
    {
        // `.Background(null)` is not equivalent to `.Set(x => x.Background = null)`:
        // ApplyModifiers reads a null modifier value as "not supplied" and only clears the
        // property when the previous render had one. Suggesting the rewrite would change
        // behaviour, so a null/default RHS is skipped. Real site:
        // samples/ReactorGallery/ControlPages/Media/ParallaxViewPage.cs.
        var source = Stubs + @"
class C
{
    ButtonElement M(ButtonElement b) => b.Set(c => c.Background = null);
    ButtonElement N(ButtonElement b) => b.Set(c => c.Background = default);
}";
        await new CSharpAnalyzerTest<PoolResetSetAnalyzer, DefaultVerifier>
        {
            TestCode = source,
        }.RunAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Fires_For_ContentAlignment_And_Background_On_Control()
    {
        // Guards that the map lookup is by exact property name and that a Control
        // receiver satisfies both the ungated and the gated arms.
        var source = Stubs + @"
class C
{
    ButtonElement M(ButtonElement b) => {|REACTOR_MOD_002:b.Set(c => c.HorizontalContentAlignment = HorizontalAlignment.Left)|};
    ButtonElement N(ButtonElement b, Microsoft.UI.Xaml.Media.Brush brush) => {|REACTOR_MOD_002:b.Set(c => c.Background = brush)|};
}";
        await new CSharpAnalyzerTest<PoolResetSetAnalyzer, DefaultVerifier>
        {
            TestCode = source,
        }.RunAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Does_Not_Fire_For_Unmapped_Property()
    {
        // Name has no modifier today — must stay silent (it is the single most common
        // .Set property in the repo, so a false positive here would be very loud).
        var source = Stubs.Replace(
            "public class Button : Control { }",
            "public class Button : Control { public string Name { get; set; } }") + @"
class C
{
    ButtonElement M(ButtonElement b) => b.Set(c => c.Name = ""x"");
}";
        await new CSharpAnalyzerTest<PoolResetSetAnalyzer, DefaultVerifier>
        {
            TestCode = source,
        }.RunAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Does_Not_Fire_For_RequestedTheme()
    {
        // Owned by RequestedThemeSetAnalyzer (REACTOR_THEME_003) — must not double-report.
        var source = Stubs.Replace(
            "public class Button : Control { }",
            "public class Button : Control { public int RequestedTheme { get; set; } }") + @"
class C
{
    ButtonElement M(ButtonElement b) => b.Set(c => c.RequestedTheme = 1);
}";
        await new CSharpAnalyzerTest<PoolResetSetAnalyzer, DefaultVerifier>
        {
            TestCode = source,
        }.RunAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Does_Not_Fire_For_Assignment_To_Captured_Object()
    {
        // Only the lambda's own parameter is the configured control.
        var source = Stubs + @"
class C
{
    ButtonElement M(ButtonElement b, Microsoft.UI.Xaml.Controls.Button other)
        => b.Set(c => other.IsEnabled = false);
}";
        await new CSharpAnalyzerTest<PoolResetSetAnalyzer, DefaultVerifier>
        {
            TestCode = source,
        }.RunAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Does_Not_Fire_For_Unrelated_Set_Helper()
    {
        // A user-defined .Set with the same shape on a non-Reactor type must be ignored.
        var source = @"
using System;

class Thing { public bool IsEnabled { get; set; } }
static class Ext2 { public static T Set<T>(this T t, Action<Thing> f) => t; }

class C
{
    string M(string s) => s.Set(t => t.IsEnabled = false);
}";
        await new CSharpAnalyzerTest<PoolResetSetAnalyzer, DefaultVerifier>
        {
            TestCode = source,
        }.RunAsync(TestContext.Current.CancellationToken);
    }

    // ---- code fix ----
    //
    // PoolResetSetCodeFix declares both REACTOR_POOL_001 and REACTOR_MOD_002 as fixable.
    // These prove the MOD_002 half actually rewrites, which was previously wired but never
    // exercised — the fix looked up the shared ModifierTable, so a mistake there would have
    // surfaced only in a consumer's IDE.

    [Fact]
    public async Task CodeFix_Rewrites_Ungated_Property()
    {
        var before = Stubs + @"
class C
{
    ButtonElement M(ButtonElement b) => {|REACTOR_MOD_002:b.Set(c => c.IsEnabled = false)|};
}";
        var after = Stubs + @"
class C
{
    ButtonElement M(ButtonElement b) => b.IsEnabled(false);
}";
        await new CSharpCodeFixTest<PoolResetSetAnalyzer, PoolResetSetCodeFix, DefaultVerifier>
        {
            TestCode = before,
            FixedCode = after,
        }.RunAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task CodeFix_Translates_Thickness_For_Gated_Padding()
    {
        // Padding is Thickness-typed but the modifier takes doubles, so the fix has to
        // unpack the constructor arguments rather than pass the RHS through. Receiver is a
        // Button (a Control), so the gate admits it.
        var before = Stubs + @"
class C
{
    ButtonElement M(ButtonElement b) => {|REACTOR_MOD_002:b.Set(c => c.Padding = new Thickness(8))|};
}";
        var after = Stubs + @"
class C
{
    ButtonElement M(ButtonElement b) => b.Padding(8);
}";
        await new CSharpCodeFixTest<PoolResetSetAnalyzer, PoolResetSetCodeFix, DefaultVerifier>
        {
            TestCode = before,
            FixedCode = after,
        }.RunAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task CodeFix_Does_Not_Offer_Rewrite_For_Multi_Statement_Block()
    {
        // The analyzer reports every modifier-backed write in a block, but a multi-statement
        // body has no mechanical rewrite — the other statements must stay in .Set while only
        // one moves. The fix must leave the code untouched rather than guess, so FixedCode
        // is identical to TestCode.
        var source = Stubs + @"
class C
{
    ButtonElement M(ButtonElement b) => {|REACTOR_MOD_002:{|REACTOR_MOD_002:b.Set(c => { c.IsEnabled = false; c.Padding = new Thickness(4); })|}|};
}";
        await new CSharpCodeFixTest<PoolResetSetAnalyzer, PoolResetSetCodeFix, DefaultVerifier>
        {
            TestCode = source,
            FixedCode = source,
        }.RunAsync(TestContext.Current.CancellationToken);
    }
}

