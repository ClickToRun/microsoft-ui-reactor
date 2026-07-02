using Microsoft.UI.Reactor.Analyzers;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;
using System.Threading.Tasks;
using Xunit;

namespace Microsoft.UI.Reactor.Tests.AnalyzerTests;

/// <summary>
/// Tests for <see cref="SetEventSubscriptionAnalyzer"/> (<c>REACTOR_LIFECYCLE_001</c>) and
/// its <see cref="SetEventSubscriptionCodeFix"/>. Stubs a FrameworkElement-derived control
/// with real events (plus a numeric field and a non-event delegate field for the FP
/// guards), and the <c>.OnMount</c>/<c>.OnUnmount</c> modifiers the fix targets.
/// </summary>
public class SetEventSubscriptionAnalyzerTests
{
    private const string Stubs = @"
using System;
using Microsoft.UI.Reactor;
using Microsoft.UI.Xaml.Controls;
#pragma warning disable CS0067 // event declared but never raised (stub controls)

namespace Microsoft.UI.Xaml
{
    public class UIElement { }
    public class FrameworkElement : UIElement { public event EventHandler Loaded; }
}

namespace Microsoft.UI.Xaml.Controls
{
    public class Button : Microsoft.UI.Xaml.FrameworkElement
    {
        public event EventHandler Click;
        public double Opacity;         // numeric compound-assignment near-miss
        public EventHandler Callback;  // non-event delegate FIELD near-miss
    }
}

namespace Microsoft.UI.Reactor
{
    using System;
    using Microsoft.UI.Xaml;
    using Microsoft.UI.Xaml.Controls;

    public record ButtonElement;

    public static class Ext
    {
        public static ButtonElement Set(this ButtonElement el, Action<Button> configure) => el;
        public static T OnMount<T>(this T el, Action<FrameworkElement> action) => el;
        public static T OnUnmount<T>(this T el, Action<FrameworkElement> action) => el;
    }
}
";

    [Fact]
    public async Task Fires_For_Loaded_Subscription()
    {
        var source = Stubs + @"
class C
{
    ButtonElement M(ButtonElement b) => {|REACTOR_LIFECYCLE_001:b.Set(c => c.Loaded += (s, e) => { })|};
}";
        await new CSharpAnalyzerTest<SetEventSubscriptionAnalyzer, DefaultVerifier>
        {
            TestCode = source,
        }.RunAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task No_Diagnostic_For_Numeric_Compound_Assignment()
    {
        // Opacity += 0.1 is a numeric compound assignment, not an event subscription.
        var source = Stubs + @"
class C
{
    ButtonElement M(ButtonElement b) => b.Set(c => c.Opacity += 0.1);
}";
        await new CSharpAnalyzerTest<SetEventSubscriptionAnalyzer, DefaultVerifier>
        {
            TestCode = source,
        }.RunAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task No_Diagnostic_For_NonEvent_Delegate_Field()
    {
        // Callback is a delegate FIELD, not an event — the mandatory event-symbol check
        // must keep the rule from firing (a fix would not compile).
        var source = Stubs + @"
class C
{
    static void OnClick(object s, EventArgs e) { }
    ButtonElement M(ButtonElement b) => b.Set(c => c.Callback += OnClick);
}";
        await new CSharpAnalyzerTest<SetEventSubscriptionAnalyzer, DefaultVerifier>
        {
            TestCode = source,
        }.RunAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task CodeFix_Rewrites_To_OnMount_OnUnmount_For_Static_Handler()
    {
        var before = Stubs + @"
class C
{
    static void OnClick(object s, EventArgs e) { }
    ButtonElement M(ButtonElement b) => {|REACTOR_LIFECYCLE_001:b.Set(c => c.Click += OnClick)|};
}";
        var after = Stubs + @"
class C
{
    static void OnClick(object s, EventArgs e) { }
    ButtonElement M(ButtonElement b) => b.OnMount(c => ((Button)c).Click += OnClick).OnUnmount(c => ((Button)c).Click -= OnClick);
}";
        await new CSharpCodeFixTest<SetEventSubscriptionAnalyzer, SetEventSubscriptionCodeFix, DefaultVerifier>
        {
            TestCode = before,
            FixedCode = after,
        }.RunAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task CodeFix_Rewrites_For_Field_Handler()
    {
        var before = Stubs + @"
class C
{
    System.EventHandler _handler;
    ButtonElement M(ButtonElement b) => {|REACTOR_LIFECYCLE_001:b.Set(c => c.Click += _handler)|};
}";
        var after = Stubs + @"
class C
{
    System.EventHandler _handler;
    ButtonElement M(ButtonElement b) => b.OnMount(c => ((Button)c).Click += _handler).OnUnmount(c => ((Button)c).Click -= _handler);
}";
        await new CSharpCodeFixTest<SetEventSubscriptionAnalyzer, SetEventSubscriptionCodeFix, DefaultVerifier>
        {
            TestCode = before,
            FixedCode = after,
        }.RunAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Fires_But_No_Fix_For_Lambda_Handler()
    {
        // Inline lambda handler is unstable across renders: the analyzer fires (nudge),
        // but no OnMount/OnUnmount rewrite is offered (TestCode == FixedCode).
        var code = Stubs + @"
class C
{
    ButtonElement M(ButtonElement b) => {|REACTOR_LIFECYCLE_001:b.Set(c => c.Click += (s, e) => { })|};
}";
        await new CSharpCodeFixTest<SetEventSubscriptionAnalyzer, SetEventSubscriptionCodeFix, DefaultVerifier>
        {
            TestCode = code,
            FixedCode = code,
        }.RunAsync(TestContext.Current.CancellationToken);
    }
}
