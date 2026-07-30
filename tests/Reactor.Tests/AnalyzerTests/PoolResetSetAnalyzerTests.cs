using Microsoft.UI.Reactor.Analyzers;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;
using System.Threading.Tasks;
using Xunit;

namespace Microsoft.UI.Reactor.Tests.AnalyzerTests;

/// <summary>
/// Tests for <see cref="PoolResetSetAnalyzer"/> (<c>REACTOR_POOL_001</c>) and its
/// <see cref="PoolResetSetCodeFix"/>. Stubs a minimal Reactor-shaped fluent
/// element so the analyzer's syntactic match against <c>.Set(fe =&gt; fe.PROP = ...)</c>
/// fires without pulling the framework in.
/// </summary>
public class PoolResetSetAnalyzerTests
{
    // Mirrors the real Reactor shape: FakeElement carries the raw FE properties
    // that .Set writes to, and the modifiers (MaxHeight/Margin/HorizontalAlignment/...)
    // are extension methods — same as ElementExtensions.cs in src/Reactor.
    private const string Stubs = @"
using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Reactor;

namespace Microsoft.UI.Xaml
{
    public enum HorizontalAlignment { Left, Center, Right, Stretch }
    public enum VerticalAlignment { Top, Center, Bottom, Stretch }
    public struct Thickness
    {
        public Thickness(double u) {}
        public Thickness(double l, double t, double r, double b) {}
    }
}

namespace Microsoft.UI.Reactor
{
using Microsoft.UI.Xaml;

public class FakeElement
{
    public double MaxHeight;
    public double MinHeight;
    public double MaxWidth;
    public double MinWidth;
    public double Width;
    public double Height;
    public double Opacity;
    public Thickness Margin;
    public HorizontalAlignment HorizontalAlignment;
    public VerticalAlignment VerticalAlignment;

    // Unrelated property — should never trigger.
    public string Text = string.Empty;

    public FakeElement Set(Action<FakeElement> configure) { configure(this); return this; }
    public FakeElement Apply(Action<FakeElement> configure) { configure(this); return this; }
}

public static class FakeElementExtensions
{
    public static FakeElement MaxHeight(this FakeElement el, double v) => el;
    public static FakeElement MinHeight(this FakeElement el, double v) => el;
    public static FakeElement MaxWidth(this FakeElement el, double v) => el;
    public static FakeElement MinWidth(this FakeElement el, double v) => el;
    public static FakeElement Width(this FakeElement el, double v) => el;
    public static FakeElement Height(this FakeElement el, double v) => el;
    public static FakeElement Opacity(this FakeElement el, double v) => el;
    public static FakeElement Margin(this FakeElement el, double u) => el;
    public static FakeElement Margin(this FakeElement el, double l, double t, double r, double b) => el;
    public static FakeElement HorizontalAlignment(this FakeElement el, HorizontalAlignment a) => el;
    public static FakeElement VerticalAlignment(this FakeElement el, VerticalAlignment a) => el;
}
}
";

    [Fact]
    public async Task Fires_For_MaxHeight()
    {
        var source = Stubs + @"
class C
{
    void M()
    {
        var el = new FakeElement();
        {|REACTOR_POOL_001:el.Set(fe => fe.MaxHeight = 260)|};
    }
}";

        await new CSharpAnalyzerTest<PoolResetSetAnalyzer, DefaultVerifier>
        {
            TestCode = source,
        }.RunAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Fires_For_HorizontalAlignment()
    {
        var source = Stubs + @"
class C
{
    void M()
    {
        var el = new FakeElement();
        {|REACTOR_POOL_001:el.Set(fe => fe.HorizontalAlignment = HorizontalAlignment.Center)|};
    }
}";

        await new CSharpAnalyzerTest<PoolResetSetAnalyzer, DefaultVerifier>
        {
            TestCode = source,
        }.RunAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Fires_With_Parenthesized_Lambda()
    {
        var source = Stubs + @"
class C
{
    void M()
    {
        var el = new FakeElement();
        {|REACTOR_POOL_001:el.Set((fe) => fe.MinWidth = 100)|};
    }
}";

        await new CSharpAnalyzerTest<PoolResetSetAnalyzer, DefaultVerifier>
        {
            TestCode = source,
        }.RunAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task No_Diagnostic_For_Untrapped_Property()
    {
        // .Text is not in ElementPool.CleanElement's FE-prop reset list and has
        // no equivalent modifier — .Set is legitimate here.
        var source = Stubs + @"
class C
{
    void M()
    {
        var el = new FakeElement();
        el.Set(fe => fe.Text = ""hi"");
    }
}";

        await new CSharpAnalyzerTest<PoolResetSetAnalyzer, DefaultVerifier>
        {
            TestCode = source,
        }.RunAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task No_Diagnostic_For_Non_Set_Method()
    {
        var source = Stubs + @"
class C
{
    void M()
    {
        var el = new FakeElement();
        el.Apply(fe => fe.MaxHeight = 260);
    }
}";

        await new CSharpAnalyzerTest<PoolResetSetAnalyzer, DefaultVerifier>
        {
            TestCode = source,
        }.RunAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task CodeFix_Rewrites_MaxHeight()
    {
        var before = Stubs + @"
class C
{
    void M()
    {
        var el = new FakeElement();
        {|REACTOR_POOL_001:el.Set(fe => fe.MaxHeight = 260)|};
    }
}";

        var after = Stubs + @"
class C
{
    void M()
    {
        var el = new FakeElement();
        el.MaxHeight(260);
    }
}";

        await new CSharpCodeFixTest<PoolResetSetAnalyzer, PoolResetSetCodeFix, DefaultVerifier>
        {
            TestCode = before,
            FixedCode = after,
        }.RunAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task CodeFix_Rewrites_HorizontalAlignment()
    {
        var before = Stubs + @"
class C
{
    void M()
    {
        var el = new FakeElement();
        {|REACTOR_POOL_001:el.Set(fe => fe.HorizontalAlignment = HorizontalAlignment.Center)|};
    }
}";

        var after = Stubs + @"
class C
{
    void M()
    {
        var el = new FakeElement();
        el.HorizontalAlignment(HorizontalAlignment.Center);
    }
}";

        await new CSharpCodeFixTest<PoolResetSetAnalyzer, PoolResetSetCodeFix, DefaultVerifier>
        {
            TestCode = before,
            FixedCode = after,
        }.RunAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task No_Diagnostic_When_Assigning_A_Captured_Objects_Property()
    {
        // The trapped property is set on a *captured* object, not the .Set lambda
        // parameter, so the pooled-control modifier rewrite would not apply — must not fire.
        var source = Stubs + @"
class C
{
    void M(FakeElement other)
    {
        var el = new FakeElement();
        el.Set(fe => other.MaxHeight = 260);
    }
}";

        await new CSharpAnalyzerTest<PoolResetSetAnalyzer, DefaultVerifier>
        {
            TestCode = source,
        }.RunAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task No_Diagnostic_For_NonReactor_Set_Helper()
    {
        // A '.Set' that isn't a Reactor DSL setter (different namespace) must not fire even
        // for a trapped property — the '.Margin(...)' etc. modifiers only exist for Reactor
        // elements, so the fix would not compile.
        var source = Stubs + @"
class C
{
    void M(RawThing r)
    {
        r.Set(x => x.MaxHeight = 260);
    }
}

public class RawThing
{
    public double MaxHeight;
    public RawThing Set(System.Action<RawThing> configure) { configure(this); return this; }
}";

        await new CSharpAnalyzerTest<PoolResetSetAnalyzer, DefaultVerifier>
        {
            TestCode = source,
        }.RunAsync(TestContext.Current.CancellationToken);
    }

    // ── Block-bodied lambdas ────────────────────────────────────────────

    [Fact]
    public async Task Fires_For_Block_Bodied_Lambda_With_Single_Statement()
    {
        var source = Stubs + @"
class C
{
    void M()
    {
        var el = new FakeElement();
        {|REACTOR_POOL_001:el.Set(fe => { fe.MaxHeight = 260; })|};
    }
}";

        await new CSharpAnalyzerTest<PoolResetSetAnalyzer, DefaultVerifier>
        {
            TestCode = source,
        }.RunAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Diagnostic_For_Block_Bodied_Lambda_With_Multiple_Statements()
    {
        // Flipped from No_Diagnostic_For_Block_Bodied_Lambda_With_Multiple_Statements, which
        // scoped detection to the codefix's reach and said: "If a future PR adds multi-stmt
        // support, this test should flip to a positive case." This is that change.
        //
        // Both halves of that support landed: the analyzer reports every modifier-backed
        // assignment in the body, and PoolResetSetCodeFix rewrites the whole body into a
        // modifier chain when every statement is convertible (see
        // ModifierAvailableAnalyzerTests.CodeFix_Rewrites_Multi_Statement_Block_Into_A_Chain).
        //
        // Detection is still deliberately wider than the fix: a body that mixes convertible
        // and non-convertible statements is reported but not auto-fixed, because a partial
        // extraction would reorder the extracted write against the ones left in .Set. That
        // asymmetry matters — this shape hid live bugs, and the widening immediately surfaced
        // MaxWidth/MaxHeight writes in minesweeper's App.cs that were silently lost on pool
        // reuse.
        var source = Stubs + @"
class C
{
    void M()
    {
        var el = new FakeElement();
        {|REACTOR_POOL_001:{|REACTOR_POOL_001:el.Set(fe => { fe.MaxHeight = 260; fe.MinHeight = 100; })|}|};
    }
}";

        await new CSharpAnalyzerTest<PoolResetSetAnalyzer, DefaultVerifier>
        {
            TestCode = source,
        }.RunAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task CodeFix_Rewrites_Block_Bodied_Lambda()
    {
        var before = Stubs + @"
class C
{
    void M()
    {
        var el = new FakeElement();
        {|REACTOR_POOL_001:el.Set(fe => { fe.MaxHeight = 260; })|};
    }
}";

        var after = Stubs + @"
class C
{
    void M()
    {
        var el = new FakeElement();
        el.MaxHeight(260);
    }
}";

        await new CSharpCodeFixTest<PoolResetSetAnalyzer, PoolResetSetCodeFix, DefaultVerifier>
        {
            TestCode = before,
            FixedCode = after,
        }.RunAsync(TestContext.Current.CancellationToken);
    }

    // ── Margin / Thickness translation ──────────────────────────────────

    [Fact]
    public async Task CodeFix_Rewrites_Margin_Uniform_Thickness()
    {
        var before = Stubs + @"
class C
{
    void M()
    {
        var el = new FakeElement();
        {|REACTOR_POOL_001:el.Set(fe => fe.Margin = new Thickness(8))|};
    }
}";

        var after = Stubs + @"
class C
{
    void M()
    {
        var el = new FakeElement();
        el.Margin(8);
    }
}";

        await new CSharpCodeFixTest<PoolResetSetAnalyzer, PoolResetSetCodeFix, DefaultVerifier>
        {
            TestCode = before,
            FixedCode = after,
        }.RunAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task CodeFix_Rewrites_Margin_FourArg_Thickness()
    {
        var before = Stubs + @"
class C
{
    void M()
    {
        var el = new FakeElement();
        {|REACTOR_POOL_001:el.Set(fe => fe.Margin = new Thickness(1, 2, 3, 4))|};
    }
}";

        var after = Stubs + @"
class C
{
    void M()
    {
        var el = new FakeElement();
        el.Margin(1, 2, 3, 4);
    }
}";

        await new CSharpCodeFixTest<PoolResetSetAnalyzer, PoolResetSetCodeFix, DefaultVerifier>
        {
            TestCode = before,
            FixedCode = after,
        }.RunAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Analyzer_Fires_But_CodeFix_Suppressed_For_Opaque_Margin_RHS()
    {
        // RHS is a variable reference, not a Thickness constructor literal —
        // we can't safely translate, so the analyzer fires (the trap is real)
        // but no codefix is offered. The verifier confirms this by leaving
        // TestCode == FixedCode: the warning persists, and no rewrite occurs.
        var code = Stubs + @"
class C
{
    void M(Thickness margin)
    {
        var el = new FakeElement();
        {|REACTOR_POOL_001:el.Set(fe => fe.Margin = margin)|};
    }
}";

        await new CSharpCodeFixTest<PoolResetSetAnalyzer, PoolResetSetCodeFix, DefaultVerifier>
        {
            TestCode = code,
            FixedCode = code,
        }.RunAsync(TestContext.Current.CancellationToken);
    }

    // ── Attached properties ─────────────────────────────────────────────
    //
    // The second syntactic shape behind REACTOR_POOL_001: an attached-property write is
    // `Owner.SetPROP(x, v)` — an invocation, not an assignment — so none of the tests above
    // exercise any of this path.

    private const string AttachedStubs = @"
using System;
using Microsoft.UI.Reactor;

namespace Microsoft.UI.Xaml.Automation
{
    public static class AutomationProperties
    {
        public static void SetName(object target, string value) { }
        public static void SetHelpText(object target, string value) { }
        public static void SetPositionInSet(object target, int value) { }
    }
}

namespace Microsoft.UI.Xaml.Controls
{
    public static class ToolTipService
    {
        public static void SetToolTip(object target, object value) { }
        public static void SetPlacement(object target, int value) { }
    }

    public static class TitleBar
    {
        public static void SetIsDragRegion(object target, bool value) { }
    }

    // Attached owners with no pool-reset entry — the real-world call sites in
    // docs/_pipeline/apps/layout and samples/apps/widget-creator. Must stay silent.
    public static class Canvas
    {
        public static void SetLeft(object target, double value) { }
        public static void SetTop(object target, double value) { }
    }

    public static class ScrollViewer
    {
        public static void SetVerticalScrollBarVisibility(object target, int value) { }
        public static void SetVerticalScrollMode(object target, int value) { }
    }
}

namespace Microsoft.UI.Reactor.Layout
{
    public static class FlexPanel
    {
        public static void SetGrow(object target, double value) { }
    }
}

namespace Contoso.Ui
{
    // Same simple name, unrelated namespace — the modifier rewrite has nothing to do
    // with this type, so it must stay silent.
    public static class AutomationProperties
    {
        public static void SetName(object target, string value) { }
    }
}

namespace Microsoft.UI.Reactor
{
    public class FakeElement
    {
        public double Width;
        public FakeElement Child;
        public FakeElement Set(Action<FakeElement> configure) { configure(this); return this; }
    }

    public static class FakeElementExtensions
    {
        public static FakeElement Width(this FakeElement el, double v) => el;
        public static FakeElement AutomationName(this FakeElement el, string v) => el;
        public static FakeElement HelpText(this FakeElement el, string v) => el;
        public static FakeElement PositionInSet(this FakeElement el, int position, int size) => el;
        public static FakeElement ToolTip(this FakeElement el, string v) => el;
        public static FakeElement ToolTipPlacement(this FakeElement el, int v) => el;
        public static FakeElement IsDragRegion(this FakeElement el, bool v) => el;
        public static FakeElement Flex(this FakeElement el, double grow = 0) => el;
    }
}
";

    [Theory]
    // One per owner represented in ModifierTable.AttachedProperties, so a regression that
    // drops a whole owner (e.g. the semantic namespace pin rejecting it) is caught here
    // rather than only by the table-driven theory in PoolResetSetConsistencyTests.
    [InlineData(@"Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(fe, ""Save"")")]
    [InlineData(@"Microsoft.UI.Xaml.Controls.ToolTipService.SetToolTip(fe, ""Save (Ctrl+S)"")")]
    [InlineData(@"Microsoft.UI.Xaml.Controls.TitleBar.SetIsDragRegion(fe, false)")]
    [InlineData(@"Microsoft.UI.Reactor.Layout.FlexPanel.SetGrow(fe, 1)")]
    public async Task Fires_For_Attached_Setter_On_The_Lambda_Parameter(string call)
    {
        var source = AttachedStubs + $@"
class C
{{
    void M()
    {{
        var el = new FakeElement();
        {{|REACTOR_POOL_001:el.Set(fe => {call})|}};
    }}
}}";

        await new CSharpAnalyzerTest<PoolResetSetAnalyzer, DefaultVerifier>
        {
            TestCode = source,
        }.RunAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Fires_For_Attached_Setter_Through_A_Cast()
    {
        // The WinUI setters are typed on DependencyObject/UIElement, so real call sites
        // sometimes cast the lambda parameter (docs/_pipeline/apps/layout does exactly this
        // for Canvas). A cast does not change which object is written to, so it must not
        // become an escape hatch from the rule.
        var source = AttachedStubs + @"
class C
{
    void M()
    {
        var el = new FakeElement();
        {|REACTOR_POOL_001:el.Set(fe => Microsoft.UI.Xaml.Automation.AutomationProperties.SetName((object)fe, ""Save""))|};
    }
}";

        await new CSharpAnalyzerTest<PoolResetSetAnalyzer, DefaultVerifier>
        {
            TestCode = source,
        }.RunAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Fires_For_Attached_Setter_In_A_Block_Body_Alongside_Other_Statements()
    {
        // Detection is wider than the fix: an attached write is no less lost for sharing a
        // block with a statement the fix cannot convert.
        var source = AttachedStubs + @"
class C
{
    void M()
    {
        var el = new FakeElement();
        {|REACTOR_POOL_001:el.Set(fe =>
        {
            var label = ""Save"";
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(fe, label);
        })|};
    }
}";

        await new CSharpCodeFixTest<PoolResetSetAnalyzer, PoolResetSetCodeFix, DefaultVerifier>
        {
            TestCode = source,
            FixedCode = source,
        }.RunAsync(TestContext.Current.CancellationToken);
    }

    [Theory]
    // The regression corpus. Each of these is a real, legitimate call site shape that the
    // invocation-matching must leave alone.
    //
    // Different target — the write does not reach the pooled control the .Set configures.
    [InlineData(@"Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(other, ""Save"")")]
    [InlineData(@"Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(fe.Child, ""Save"")")]
    // Same simple name, unrelated namespace.
    [InlineData(@"Contoso.Ui.AutomationProperties.SetName(fe, ""Save"")")]
    // Attached owners with no pool-reset entry (docs/_pipeline/apps/layout,
    // samples/ReactorGallery, samples/apps/widget-creator).
    [InlineData(@"Microsoft.UI.Xaml.Controls.Canvas.SetLeft((object)fe, 10)")]
    [InlineData(@"Microsoft.UI.Xaml.Controls.Canvas.SetTop((object)fe, 10)")]
    [InlineData(@"Microsoft.UI.Xaml.Controls.ScrollViewer.SetVerticalScrollBarVisibility(fe, 1)")]
    [InlineData(@"Microsoft.UI.Xaml.Controls.ScrollViewer.SetVerticalScrollMode(fe, 1)")]
    // A null write is not expressible through the modifier — ApplyModifiers skips a null
    // value, so suggesting the rewrite would change behaviour.
    [InlineData(@"Microsoft.UI.Xaml.Controls.ToolTipService.SetToolTip(fe, null)")]
    public async Task No_Diagnostic_For_Attached_Setter(string call)
    {
        var source = AttachedStubs + $@"
class C
{{
    void M(FakeElement other)
    {{
        var el = new FakeElement();
        el.Set(fe => {call});
    }}
}}";

        await new CSharpAnalyzerTest<PoolResetSetAnalyzer, DefaultVerifier>
        {
            TestCode = source,
        }.RunAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task No_Diagnostic_For_Attached_Setter_In_A_NonReactor_Set_Helper()
    {
        // Same guard as the assignment arm: the '.AutomationName(...)' modifiers only exist
        // for Reactor elements, so a lookalike '.Set' must not be reported.
        var source = AttachedStubs + @"
class C
{
    void M(RawAttachedThing r)
    {
        r.Set(x => Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(x, ""Save""));
    }
}

public class RawAttachedThing
{
    public RawAttachedThing Set(System.Action<RawAttachedThing> configure) { configure(this); return this; }
}";

        await new CSharpAnalyzerTest<PoolResetSetAnalyzer, DefaultVerifier>
        {
            TestCode = source,
        }.RunAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task CodeFix_Rewrites_Attached_ToolTip()
    {
        var before = AttachedStubs + @"
class C
{
    void M()
    {
        var el = new FakeElement();
        {|REACTOR_POOL_001:el.Set(b => Microsoft.UI.Xaml.Controls.ToolTipService.SetToolTip(b, ""This is a native tooltip""))|};
    }
}";

        var after = AttachedStubs + @"
class C
{
    void M()
    {
        var el = new FakeElement();
        el.ToolTip(""This is a native tooltip"");
    }
}";

        await new CSharpCodeFixTest<PoolResetSetAnalyzer, PoolResetSetCodeFix, DefaultVerifier>
        {
            TestCode = before,
            FixedCode = after,
        }.RunAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task CodeFix_Rewrites_A_Block_Mixing_Instance_And_Attached_Writes()
    {
        var before = AttachedStubs + @"
class C
{
    void M()
    {
        var el = new FakeElement();
        {|REACTOR_POOL_001:{|REACTOR_POOL_001:el.Set(fe => { fe.Width = 10; Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(fe, ""Save""); })|}|};
    }
}";

        var after = AttachedStubs + @"
class C
{
    void M()
    {
        var el = new FakeElement();
        el.Width(10).AutomationName(""Save"");
    }
}";

        await new CSharpCodeFixTest<PoolResetSetAnalyzer, PoolResetSetCodeFix, DefaultVerifier>
        {
            TestCode = before,
            FixedCode = after,
        }.RunAsync(TestContext.Current.CancellationToken);
    }

    [Theory]
    // Reported, but deliberately not auto-fixed. TestCode == FixedCode asserts the diagnostic
    // survives AND that no rewrite is offered — flipping any of these to AutoFix: true in
    // ModifierTable would break this test.
    //
    // Arity: SetPositionInSet writes one DP, .PositionInSet(position, size) writes two.
    [InlineData(@"Microsoft.UI.Xaml.Automation.AutomationProperties.SetPositionInSet(fe, 2)")]
    // N:1: every FlexPanel property funnels into one .Flex(...) that replaces the whole
    // FlexAttached record.
    [InlineData(@"Microsoft.UI.Reactor.Layout.FlexPanel.SetGrow(fe, 1)")]
    // Type: SetToolTip takes object, .ToolTip takes string — `tip` is an object here, so the
    // rewrite would not compile.
    [InlineData(@"Microsoft.UI.Xaml.Controls.ToolTipService.SetToolTip(fe, tip)")]
    public async Task Analyzer_Fires_But_CodeFix_Suppressed_For_Attached_Setter(string call)
    {
        var code = AttachedStubs + $@"
class C
{{
    void M(object tip)
    {{
        var el = new FakeElement();
        {{|REACTOR_POOL_001:el.Set(fe => {call})|}};
    }}
}}";

        await new CSharpCodeFixTest<PoolResetSetAnalyzer, PoolResetSetCodeFix, DefaultVerifier>
        {
            TestCode = code,
            FixedCode = code,
        }.RunAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task CodeFix_Suppressed_When_A_Block_Mixes_Fixable_And_Unfixable_Attached_Writes()
    {
        // All-or-nothing: converting only the fixable half would leave a residual .Set and
        // move the extracted write from the setter phase into the modifier phase.
        var code = AttachedStubs + @"
class C
{
    void M()
    {
        var el = new FakeElement();
        {|REACTOR_POOL_001:{|REACTOR_POOL_001:el.Set(fe => { Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(fe, ""Save""); Microsoft.UI.Reactor.Layout.FlexPanel.SetGrow(fe, 1); })|}|};
    }
}";

        await new CSharpCodeFixTest<PoolResetSetAnalyzer, PoolResetSetCodeFix, DefaultVerifier>
        {
            TestCode = code,
            FixedCode = code,
        }.RunAsync(TestContext.Current.CancellationToken);
    }
}
