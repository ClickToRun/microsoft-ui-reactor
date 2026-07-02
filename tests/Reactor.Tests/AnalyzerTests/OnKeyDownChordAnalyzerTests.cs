using Microsoft.UI.Reactor.Analyzers;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;
using System.Threading.Tasks;
using Xunit;

namespace Microsoft.UI.Reactor.Tests.AnalyzerTests;

/// <summary>
/// Tests for <see cref="OnKeyDownChordAnalyzer"/> (<c>REACTOR_INPUT_001</c>) and its template
/// <see cref="OnKeyDownChordCodeFix"/>. Stubs a minimal Reactor-shaped <c>.OnKeyDown((s, e) =&gt; …)</c>
/// fluent modifier plus the real <c>Windows.System.VirtualKey</c> / <c>VirtualKeyModifiers</c> enum
/// shapes so the analyzer's syntactic match and its <c>VirtualKeyModifiers</c> semantic guard fire
/// without pulling the framework in.
/// </summary>
public class OnKeyDownChordAnalyzerTests
{
    private const string Stubs = @"
using System;
using Windows.System;
using Microsoft.UI.Xaml.Input;

namespace Windows.System
{
    public enum VirtualKey { None, S, F, Enter, Shift, Control }
    [Flags] public enum VirtualKeyModifiers { None = 0, Control = 1, Menu = 2, Shift = 4, Windows = 8 }
}

namespace Microsoft.UI.Xaml.Input
{
    public class KeyRoutedEventArgs { public Windows.System.VirtualKey Key { get; set; } }
}

public class FakeElement { }

public static class FakeElementExtensions
{
    // Mirrors ElementExtensions.OnKeyDown: (sender, args) shape, returns the element for chaining.
    public static FakeElement OnKeyDown(this FakeElement el, Action<object, KeyRoutedEventArgs> handler) => el;
    public static FakeElement OnKeyUp(this FakeElement el, Action<object, KeyRoutedEventArgs> handler) => el;
    public static FakeElement Margin(this FakeElement el, double v) => el;
}
";

    // ── Positive ────────────────────────────────────────────────────────

    [Fact]
    public async Task Fires_For_Control_Chord()
    {
        var source = Stubs + @"
class C
{
    static VirtualKeyModifiers Mods() => VirtualKeyModifiers.None;
    void Save() {}
    void M()
    {
        var el = new FakeElement();
        {|REACTOR_INPUT_001:el.OnKeyDown((s, e) => { if (e.Key == VirtualKey.S && Mods().HasFlag(VirtualKeyModifiers.Control)) Save(); })|};
    }
}";

        await new CSharpAnalyzerTest<OnKeyDownChordAnalyzer, DefaultVerifier>
        {
            TestCode = source,
        }.RunAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Fires_For_Menu_Alt_Chord()
    {
        var source = Stubs + @"
class C
{
    static VirtualKeyModifiers Mods() => VirtualKeyModifiers.None;
    void Find() {}
    void M()
    {
        var el = new FakeElement();
        {|REACTOR_INPUT_001:el.OnKeyDown((s, e) => { if (e.Key == VirtualKey.F && Mods().HasFlag(VirtualKeyModifiers.Menu)) Find(); })|};
    }
}";

        await new CSharpAnalyzerTest<OnKeyDownChordAnalyzer, DefaultVerifier>
        {
            TestCode = source,
        }.RunAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Fires_For_Control_And_Menu_Chord()
    {
        var source = Stubs + @"
class C
{
    static VirtualKeyModifiers Mods() => VirtualKeyModifiers.None;
    void Do() {}
    void M()
    {
        var el = new FakeElement();
        {|REACTOR_INPUT_001:el.OnKeyDown((s, e) => { if (Mods().HasFlag(VirtualKeyModifiers.Control) && Mods().HasFlag(VirtualKeyModifiers.Menu)) Do(); })|};
    }
}";

        await new CSharpAnalyzerTest<OnKeyDownChordAnalyzer, DefaultVerifier>
        {
            TestCode = source,
        }.RunAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Fires_For_Qualified_Modifier_Reference()
    {
        // Windows.System.VirtualKeyModifiers.Control — the qualified receiver still resolves.
        var source = Stubs + @"
class C
{
    static VirtualKeyModifiers Mods() => VirtualKeyModifiers.None;
    void Save() {}
    void M()
    {
        var el = new FakeElement();
        {|REACTOR_INPUT_001:el.OnKeyDown((s, e) => { if (e.Key == VirtualKey.S && Mods().HasFlag(Windows.System.VirtualKeyModifiers.Control)) Save(); })|};
    }
}";

        await new CSharpAnalyzerTest<OnKeyDownChordAnalyzer, DefaultVerifier>
        {
            TestCode = source,
        }.RunAsync(TestContext.Current.CancellationToken);
    }

    // ── Negative ────────────────────────────────────────────────────────

    [Fact]
    public async Task No_Diagnostic_For_Handler_Without_Modifier()
    {
        // A plain key handler (no Ctrl/Alt chord) is a legitimate, focus-scoped use of .OnKeyDown.
        var source = Stubs + @"
class C
{
    void Activate() {}
    void M()
    {
        var el = new FakeElement();
        el.OnKeyDown((s, e) => { if (e.Key == VirtualKey.Enter) Activate(); });
    }
}";

        await new CSharpAnalyzerTest<OnKeyDownChordAnalyzer, DefaultVerifier>
        {
            TestCode = source,
        }.RunAsync(TestContext.Current.CancellationToken);
    }

    // ── Near-miss: almost trips the fast path ───────────────────────────

    [Fact]
    public async Task No_Diagnostic_For_Shift_Only_Chord()
    {
        // Near-miss: a .OnKeyDown lambda that DOES reference VirtualKeyModifiers, but Shift — not the
        // Ctrl/Alt app-accelerator footgun. Must not fire (spec: Control/Menu only).
        var source = Stubs + @"
class C
{
    static VirtualKeyModifiers Mods() => VirtualKeyModifiers.None;
    void Save() {}
    void M()
    {
        var el = new FakeElement();
        el.OnKeyDown((s, e) => { if (e.Key == VirtualKey.S && Mods().HasFlag(VirtualKeyModifiers.Shift)) Save(); });
    }
}";

        await new CSharpAnalyzerTest<OnKeyDownChordAnalyzer, DefaultVerifier>
        {
            TestCode = source,
        }.RunAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task No_Diagnostic_For_OnKeyUp_Chord()
    {
        // Near-miss: same chord shape but on .OnKeyUp, which is not the matched modifier.
        var source = Stubs + @"
class C
{
    static VirtualKeyModifiers Mods() => VirtualKeyModifiers.None;
    void Save() {}
    void M()
    {
        var el = new FakeElement();
        el.OnKeyUp((s, e) => { if (e.Key == VirtualKey.S && Mods().HasFlag(VirtualKeyModifiers.Control)) Save(); });
    }
}";

        await new CSharpAnalyzerTest<OnKeyDownChordAnalyzer, DefaultVerifier>
        {
            TestCode = source,
        }.RunAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task No_Diagnostic_For_Method_Group_Handler()
    {
        // A method-group handler is out of scope — the analyzer only inspects lambda bodies.
        var source = Stubs + @"
class C
{
    static VirtualKeyModifiers Mods() => VirtualKeyModifiers.None;
    void Save() {}
    void Handler(object s, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.S && Mods().HasFlag(VirtualKeyModifiers.Control)) Save();
    }
    void M()
    {
        var el = new FakeElement();
        el.OnKeyDown(Handler);
    }
}";

        await new CSharpAnalyzerTest<OnKeyDownChordAnalyzer, DefaultVerifier>
        {
            TestCode = source,
        }.RunAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task No_Diagnostic_For_Same_Named_LookAlike_Enum()
    {
        // Semantic guard: a look-alike enum also named 'VirtualKeyModifiers' but NOT in
        // Windows.System must not trip the rule. Stand-alone source (no Windows.System stub) so the
        // only 'VirtualKeyModifiers' in scope is the local look-alike.
        var source = @"
using System;

public enum VirtualKeyModifiers { None, Control, Menu }

public class FakeElement { }
public static class Ext
{
    public static FakeElement OnKeyDown(this FakeElement el, Action<object> handler) => el;
}

class C
{
    void M()
    {
        var el = new FakeElement();
        el.OnKeyDown(s => { if (VirtualKeyModifiers.Control == VirtualKeyModifiers.Menu) { } });
    }
}";

        await new CSharpAnalyzerTest<OnKeyDownChordAnalyzer, DefaultVerifier>
        {
            TestCode = source,
        }.RunAsync(TestContext.Current.CancellationToken);
    }

    // ── Template code fix (additive scaffold; diagnostic persists) ───────

    [Fact]
    public async Task CodeFix_Appends_Control_Template()
    {
        var before = Stubs + @"
class C
{
    static VirtualKeyModifiers Mods() => VirtualKeyModifiers.None;
    void Save() {}
    void M()
    {
        var el = new FakeElement();
        {|REACTOR_INPUT_001:el.OnKeyDown((s, e) => { if (e.Key == VirtualKey.S && Mods().HasFlag(VirtualKeyModifiers.Control)) Save(); })|};
    }
}";

        var after = Stubs + @"
class C
{
    static VirtualKeyModifiers Mods() => VirtualKeyModifiers.None;
    void Save() {}
    void M()
    {
        var el = new FakeElement();
        {|REACTOR_INPUT_001:el.OnKeyDown((s, e) => { if (e.Key == VirtualKey.S && Mods().HasFlag(VirtualKeyModifiers.Control)) Save(); })|} /* REACTOR_INPUT_001: .OnKeyDown is focus-scoped. Register this shortcut app-wide as a Command accelerator instead, e.g. new Command { Label = <name>, Execute = <handler>, Accelerator = Accelerator(VirtualKey.S, VirtualKeyModifiers.Control) }, then remove this .OnKeyDown chord. */;
    }
}";

        await new CSharpCodeFixTest<OnKeyDownChordAnalyzer, OnKeyDownChordCodeFix, DefaultVerifier>
        {
            TestCode = before,
            FixedCode = after,
            // Additive template preview: the fix annotates rather than resolves, so the warning
            // intentionally persists after the fix. MarkupMode.Allow keeps the (still-fixable)
            // diagnostic declared in FixedCode from being stripped; one incremental pass applies it.
            FixedState = { MarkupHandling = MarkupMode.Allow },
            NumberOfIncrementalIterations = 1,
        }.RunAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task CodeFix_Appends_Menu_Template_With_Extracted_Key()
    {
        var before = Stubs + @"
class C
{
    static VirtualKeyModifiers Mods() => VirtualKeyModifiers.None;
    void Find() {}
    void M()
    {
        var el = new FakeElement();
        {|REACTOR_INPUT_001:el.OnKeyDown((s, e) => { if (e.Key == VirtualKey.F && Mods().HasFlag(VirtualKeyModifiers.Menu)) Find(); })|};
    }
}";

        var after = Stubs + @"
class C
{
    static VirtualKeyModifiers Mods() => VirtualKeyModifiers.None;
    void Find() {}
    void M()
    {
        var el = new FakeElement();
        {|REACTOR_INPUT_001:el.OnKeyDown((s, e) => { if (e.Key == VirtualKey.F && Mods().HasFlag(VirtualKeyModifiers.Menu)) Find(); })|} /* REACTOR_INPUT_001: .OnKeyDown is focus-scoped. Register this shortcut app-wide as a Command accelerator instead, e.g. new Command { Label = <name>, Execute = <handler>, Accelerator = Accelerator(VirtualKey.F, VirtualKeyModifiers.Menu) }, then remove this .OnKeyDown chord. */;
    }
}";

        await new CSharpCodeFixTest<OnKeyDownChordAnalyzer, OnKeyDownChordCodeFix, DefaultVerifier>
        {
            TestCode = before,
            FixedCode = after,
            FixedState = { MarkupHandling = MarkupMode.Allow },
            NumberOfIncrementalIterations = 1,
        }.RunAsync(TestContext.Current.CancellationToken);
    }
}
