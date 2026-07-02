using Microsoft.UI.Reactor.Analyzers;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;
using System.Threading.Tasks;
using Xunit;

namespace Microsoft.UI.Reactor.Tests.AnalyzerTests;

/// <summary>
/// Tests for <see cref="RawCommandCallbackAnalyzer"/> (<c>REACTOR_CMD_001</c>) and its
/// <see cref="RawCommandCallbackCodeFix"/>. Stubs the command-capable element records (Button /
/// SplitButton / ToggleButton) plus a plain <c>MenuFlyoutItemData</c> data record and a same-named
/// element in another namespace, so the analyzer's per-element map + namespace guard resolve
/// without pulling the framework in.
/// </summary>
public class RawCommandCallbackAnalyzerTests
{
    private const string Stubs = @"
namespace System.Runtime.CompilerServices
{
    public static class IsExternalInit { }
}

namespace Microsoft.UI.Reactor.Core
{
    public sealed record Command
    {
        public string Label { get; init; }
        public System.Action Execute { get; init; }
    }

    public abstract record Element { }

    public sealed record ButtonElement(string Label, System.Action OnClick = null) : Element
    {
        public Command Command { get; init; }
    }

    public sealed record SplitButtonElement(string Label, System.Action OnClick = null, Element Flyout = null) : Element
    {
        public Command Command { get; init; }
    }

    public sealed record ToggleButtonElement(string Label, bool IsChecked = false, System.Action<bool> OnIsCheckedChanged = null) : Element
    {
        public bool? CheckedState { get; init; }
        public System.Action<bool?> OnCheckedStateChanged { get; init; }
        public Command Command { get; init; }
    }

    // A plain data record (mirrors the real MenuFlyoutItemData/AppBarButtonData that
    // CommandDebounceAnalyzer's factory list includes). It is NOT a command-capable Element, so
    // REACTOR_CMD_001 must never fire on it — this is the near-miss that proves the two rules
    // cannot share a list.
    public sealed record MenuFlyoutItemData(string Text)
    {
        public Command Command { get; init; }
        public System.Action OnClick { get; init; }
    }
}

namespace Other
{
    // Same simple name as a real element, but a different namespace — the namespace guard must
    // keep the analyzer from firing here.
    public sealed record ButtonElement(string Label, System.Action OnClick = null)
    {
        public Microsoft.UI.Reactor.Core.Command Command { get; init; }
    }
}
";

    private static CSharpAnalyzerTest<RawCommandCallbackAnalyzer, DefaultVerifier> Analyzer(string source) =>
        new() { TestCode = Stubs + source };

    private static CSharpCodeFixTest<RawCommandCallbackAnalyzer, RawCommandCallbackCodeFix, DefaultVerifier> Fix(string before, string after) =>
        new() { TestCode = Stubs + before, FixedCode = Stubs + after };

    // ── Positive ────────────────────────────────────────────────────────

    [Fact]
    public async Task Fires_When_Initializer_Sets_Command_And_OnClick()
    {
        var source = @"
namespace TestApp
{
    using Microsoft.UI.Reactor.Core;

    class C
    {
        void M()
        {
            var cmd = new Command();
            System.Action h = () => { };
            var b = new ButtonElement(""Save"") { Command = cmd, {|REACTOR_CMD_001:OnClick = h|} };
        }
    }
}";
        await Analyzer(source).RunAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Fires_When_Constructor_Positional_Callback_And_Command()
    {
        // The positional Action? argument IS OnClick.
        var source = @"
namespace TestApp
{
    using Microsoft.UI.Reactor.Core;

    class C
    {
        static void DoThing() { }

        void M()
        {
            var cmd = new Command();
            var b = new ButtonElement(""Save"", {|REACTOR_CMD_001:DoThing|}) { Command = cmd };
        }
    }
}";
        await Analyzer(source).RunAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Fires_On_With_Expression()
    {
        var source = @"
namespace TestApp
{
    using Microsoft.UI.Reactor.Core;

    class C
    {
        void M()
        {
            var cmd = new Command();
            System.Action h = () => { };
            var b = new ButtonElement(""Save"");
            var b2 = b with { Command = cmd, {|REACTOR_CMD_001:OnClick = h|} };
        }
    }
}";
        await Analyzer(source).RunAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Fires_On_ToggleButton_OnIsCheckedChanged()
    {
        var source = @"
namespace TestApp
{
    using Microsoft.UI.Reactor.Core;

    class C
    {
        void M()
        {
            var cmd = new Command();
            System.Action<bool> t = _ => { };
            var b = new ToggleButtonElement(""T"") { Command = cmd, {|REACTOR_CMD_001:OnIsCheckedChanged = t|} };
        }
    }
}";
        await Analyzer(source).RunAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Fires_On_ToggleButton_OnCheckedStateChanged()
    {
        var source = @"
namespace TestApp
{
    using Microsoft.UI.Reactor.Core;

    class C
    {
        void M()
        {
            var cmd = new Command();
            System.Action<bool?> s = _ => { };
            var b = new ToggleButtonElement(""T"") { Command = cmd, {|REACTOR_CMD_001:OnCheckedStateChanged = s|} };
        }
    }
}";
        await Analyzer(source).RunAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Fires_On_SplitButton_Constructor_Positional_Callback()
    {
        var source = @"
namespace TestApp
{
    using Microsoft.UI.Reactor.Core;

    class C
    {
        static void DoThing() { }

        void M()
        {
            var cmd = new Command();
            var b = new SplitButtonElement(""S"", {|REACTOR_CMD_001:DoThing|}) { Command = cmd };
        }
    }
}";
        await Analyzer(source).RunAsync(TestContext.Current.CancellationToken);
    }

    // ── Negative ────────────────────────────────────────────────────────

    [Fact]
    public async Task No_Diagnostic_When_Only_Command_Set()
    {
        var source = @"
namespace TestApp
{
    using Microsoft.UI.Reactor.Core;

    class C
    {
        void M()
        {
            var cmd = new Command();
            var b = new ButtonElement(""Save"") { Command = cmd };
        }
    }
}";
        await Analyzer(source).RunAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task No_Diagnostic_When_Only_Callback_Set()
    {
        var source = @"
namespace TestApp
{
    using Microsoft.UI.Reactor.Core;

    class C
    {
        void M()
        {
            System.Action h = () => { };
            var b = new ButtonElement(""Save"", h);
            var c = new ButtonElement(""Save"") { OnClick = h };
        }
    }
}";
        await Analyzer(source).RunAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task No_Diagnostic_When_Callback_Explicitly_Null()
    {
        // OnClick = null leaves EffectiveCallback = Invokable(cmd): the command still runs.
        var source = @"
namespace TestApp
{
    using Microsoft.UI.Reactor.Core;

    class C
    {
        void M()
        {
            var cmd = new Command();
            var b = new ButtonElement(""Save"") { Command = cmd, OnClick = null };
        }
    }
}";
        await Analyzer(source).RunAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task No_Diagnostic_On_MenuItem_Data_Record()
    {
        // MenuFlyoutItemData is a plain data record, not a command-capable Element — a naive
        // property-name match would fire here, but our per-element map must not.
        var source = @"
namespace TestApp
{
    using Microsoft.UI.Reactor.Core;

    class C
    {
        void M()
        {
            var cmd = new Command();
            System.Action h = () => { };
            var m = new MenuFlyoutItemData(""Copy"") { Command = cmd, OnClick = h };
        }
    }
}";
        await Analyzer(source).RunAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task No_Diagnostic_On_ButtonElement_In_Other_Namespace()
    {
        var source = @"
namespace TestApp
{
    class C
    {
        void M()
        {
            var cmd = new Microsoft.UI.Reactor.Core.Command();
            System.Action h = () => { };
            var b = new Other.ButtonElement(""Save"") { Command = cmd, OnClick = h };
        }
    }
}";
        await Analyzer(source).RunAsync(TestContext.Current.CancellationToken);
    }

    // ── Code fix ────────────────────────────────────────────────────────

    [Fact]
    public async Task CodeFix_Removes_Initializer_Callback()
    {
        var before = @"
namespace TestApp
{
    using Microsoft.UI.Reactor.Core;

    class C
    {
        void M()
        {
            var cmd = new Command();
            System.Action h = () => { };
            var b = new ButtonElement(""Save"") { Command = cmd, {|REACTOR_CMD_001:OnClick = h|} };
        }
    }
}";
        var after = @"
namespace TestApp
{
    using Microsoft.UI.Reactor.Core;

    class C
    {
        void M()
        {
            var cmd = new Command();
            System.Action h = () => { };
            var b = new ButtonElement(""Save"") { Command = cmd };
        }
    }
}";
        await Fix(before, after).RunAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task CodeFix_Removes_Constructor_Positional_Callback()
    {
        var before = @"
namespace TestApp
{
    using Microsoft.UI.Reactor.Core;

    class C
    {
        static void DoThing() { }

        void M()
        {
            var cmd = new Command();
            var b = new ButtonElement(""Save"", {|REACTOR_CMD_001:DoThing|}) { Command = cmd };
        }
    }
}";
        var after = @"
namespace TestApp
{
    using Microsoft.UI.Reactor.Core;

    class C
    {
        static void DoThing() { }

        void M()
        {
            var cmd = new Command();
            var b = new ButtonElement(""Save"") { Command = cmd };
        }
    }
}";
        await Fix(before, after).RunAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task CodeFix_Removes_With_Expression_Callback()
    {
        var before = @"
namespace TestApp
{
    using Microsoft.UI.Reactor.Core;

    class C
    {
        void M()
        {
            var cmd = new Command();
            System.Action h = () => { };
            var b = new ButtonElement(""Save"");
            var b2 = b with { Command = cmd, {|REACTOR_CMD_001:OnClick = h|} };
        }
    }
}";
        var after = @"
namespace TestApp
{
    using Microsoft.UI.Reactor.Core;

    class C
    {
        void M()
        {
            var cmd = new Command();
            System.Action h = () => { };
            var b = new ButtonElement(""Save"");
            var b2 = b with { Command = cmd };
        }
    }
}";
        await Fix(before, after).RunAsync(TestContext.Current.CancellationToken);
    }
}
