using Microsoft.UI.Reactor.Analyzers;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;
using System.Threading.Tasks;
using Xunit;

namespace Microsoft.UI.Reactor.Tests.AnalyzerTests;

/// <summary>
/// Tests for <see cref="Win2DSharedDeviceAnalyzer"/> (<c>REACTOR_WIN2D_001</c>) and its
/// <see cref="Win2DSharedDeviceCodeFix"/>. Stubs the minimum Reactor.Advanced Win2D surface —
/// the three canvas element records + factories, the <c>UseCanvasResources</c> hook, and the
/// <c>.UseSharedDevice()</c>/<c>.ClearColor()</c>/<c>.Set()</c> modifiers — so the analyzer's
/// return-type + hook-initializer semantics fire without pulling the framework in.
/// </summary>
public class Win2DSharedDeviceAnalyzerTests
{
    // Mirrors the real shape: each canvas element exposes BOTH a `UseSharedDevice` init property
    // and a same-named extension modifier. C# resolves `.UseSharedDevice()` to the extension (the
    // bool property is not invocable), exactly as in Reactor.Advanced.
    private const string Stubs = @"
using System;

namespace System.Runtime.CompilerServices { public static class IsExternalInit { } }

namespace Microsoft.UI.Reactor.Core
{
    public sealed class RenderContext { }
    public abstract record Element { }
    public sealed class Ref<T> { public T Current => default!; }

    public static class Factories
    {
        public static Element VStack(params Element[] children) => children.Length > 0 ? children[0] : null!;
        public static Element TextBlock(string text) => null!;
    }
}

namespace Microsoft.UI.Reactor.Advanced.Win2D
{
    using Microsoft.UI.Reactor.Core;

    public sealed record Win2DCanvasElement : Element { public bool UseSharedDevice { get; init; } }
    public sealed record Win2DAnimatedCanvasElement : Element { public bool UseSharedDevice { get; init; } }
    public sealed record Win2DVirtualCanvasElement : Element { public bool UseSharedDevice { get; init; } }

    public static class UseCanvasResourcesHook
    {
        public static Ref<T> UseCanvasResources<T>(this RenderContext ctx, Func<object, T> create) => new();
    }

    public static class Win2DCanvasModifiers
    {
        public static Win2DCanvasElement UseSharedDevice(this Win2DCanvasElement el, bool use = true) => el with { UseSharedDevice = use };
        public static Win2DAnimatedCanvasElement UseSharedDevice(this Win2DAnimatedCanvasElement el, bool use = true) => el with { UseSharedDevice = use };
        public static Win2DVirtualCanvasElement UseSharedDevice(this Win2DVirtualCanvasElement el, bool use = true) => el with { UseSharedDevice = use };

        public static Win2DCanvasElement ClearColor(this Win2DCanvasElement el, int color) => el;
        public static Win2DAnimatedCanvasElement ClearColor(this Win2DAnimatedCanvasElement el, int color) => el;
        public static Win2DCanvasElement Set(this Win2DCanvasElement el, Action<object> setter) => el;
    }
}

namespace Microsoft.UI.Reactor.Advanced
{
    using Microsoft.UI.Reactor.Advanced.Win2D;

    public static class Factories
    {
        public static Win2DCanvasElement Win2DCanvas(Action onDraw) => new();
        public static Win2DAnimatedCanvasElement Win2DAnimatedCanvas(Action onDraw) => new();
        public static Win2DVirtualCanvasElement Win2DVirtualCanvas(Action onDraw) => new();
    }
}
";

    private static string Wrap(string body) => Stubs + @"
namespace TestApp
{
    using Microsoft.UI.Reactor.Core;
    using Microsoft.UI.Reactor.Advanced.Win2D;
    using static Microsoft.UI.Reactor.Advanced.Factories;
    using static Microsoft.UI.Reactor.Core.Factories;

    public class C
    {
        static void Use(Ref<object> r) { }
        static void DoNothing() { }

        public Element Render(RenderContext ctx)
        {
" + body + @"
        }
    }
}";

    private static Task Analyze(string body) =>
        new CSharpAnalyzerTest<Win2DSharedDeviceAnalyzer, DefaultVerifier>
        {
            TestCode = Wrap(body),
        }.RunAsync(TestContext.Current.CancellationToken);

    private static Task Fix(string before, string after) =>
        new CSharpCodeFixTest<Win2DSharedDeviceAnalyzer, Win2DSharedDeviceCodeFix, DefaultVerifier>
        {
            TestCode = Wrap(before),
            FixedCode = Wrap(after),
            CodeActionEquivalenceKey = Win2DSharedDeviceAnalyzer.DiagnosticId,
        }.RunAsync(TestContext.Current.CancellationToken);

    // ── Positive — fires ────────────────────────────────────────────────

    [Fact]
    public Task Fires_On_Manual_Canvas_Drawing_Resource_Without_SharedDevice() => Analyze(@"
            var sprite = ctx.UseCanvasResources<object>(d => new object());
            return {|REACTOR_WIN2D_001:Win2DCanvas|}(() => Use(sprite));");

    [Fact]
    public Task Fires_On_Animated_Canvas_Nested_In_Layout_Container() => Analyze(@"
            var sprite = ctx.UseCanvasResources<object>(d => new object());
            return VStack(TextBlock(""x""), {|REACTOR_WIN2D_001:Win2DAnimatedCanvas|}(() => Use(sprite)).ClearColor(0));");

    [Fact]
    public Task Fires_On_Virtual_Canvas_Drawing_Resource_Without_SharedDevice() => Analyze(@"
            var sprite = ctx.UseCanvasResources<object>(d => new object());
            return {|REACTOR_WIN2D_001:Win2DVirtualCanvas|}(() => Use(sprite));");

    // ── Negative — does not fire ────────────────────────────────────────

    [Fact]
    public Task No_Diagnostic_When_UseSharedDevice_Present() => Analyze(@"
            var sprite = ctx.UseCanvasResources<object>(d => new object());
            return Win2DCanvas(() => Use(sprite)).UseSharedDevice();");

    [Fact]
    public Task No_Diagnostic_When_Other_Canvas_Does_Not_Draw_The_Resource() => Analyze(@"
            var sprite = ctx.UseCanvasResources<object>(d => new object());
            return VStack(
                Win2DCanvas(() => Use(sprite)).UseSharedDevice(),
                Win2DCanvas(() => DoNothing()));");

    [Fact]
    public Task No_Diagnostic_When_No_UseCanvasResources_Hook() => Analyze(@"
            return Win2DCanvas(() => DoNothing());");

    [Fact]
    public Task No_Diagnostic_When_Hook_But_No_Canvas_Returned() => Analyze(@"
            var sprite = ctx.UseCanvasResources<object>(d => new object());
            return TextBlock(sprite.Current.ToString());");

    // ── Near-miss — almost trips the fast path, but bails ───────────────

    [Fact]
    public Task No_Diagnostic_When_Canvas_Captured_In_Variable() => Analyze(@"
            var sprite = ctx.UseCanvasResources<object>(d => new object());
            var canvas = Win2DCanvas(() => Use(sprite));
            return canvas;");

    [Fact]
    public Task No_Diagnostic_When_Chain_Has_Raw_Set() => Analyze(@"
            var sprite = ctx.UseCanvasResources<object>(d => new object());
            return Win2DCanvas(() => Use(sprite)).Set(c => { });");

    [Fact]
    public Task No_Diagnostic_When_With_Opts_Into_SharedDevice() => Analyze(@"
            var sprite = ctx.UseCanvasResources<object>(d => new object());
            return Win2DCanvas(() => Use(sprite)) with { UseSharedDevice = true };");

    [Fact]
    public Task No_Diagnostic_When_Parenthesized_Canvas_Has_SharedDevice() => Analyze(@"
            var sprite = ctx.UseCanvasResources<object>(d => new object());
            return (Win2DCanvas(() => Use(sprite))).UseSharedDevice();");

    [Fact]
    public Task No_Diagnostic_When_Parenthesized_Canvas_Captured() => Analyze(@"
            var sprite = ctx.UseCanvasResources<object>(d => new object());
            var canvas = (Win2DCanvas(() => Use(sprite)));
            return canvas;");

    // ── Code-fix round-trips ────────────────────────────────────────────

    [Fact]
    public Task CodeFix_Appends_UseSharedDevice_To_Bare_Canvas() => Fix(
        before: @"
            var sprite = ctx.UseCanvasResources<object>(d => new object());
            return {|REACTOR_WIN2D_001:Win2DCanvas|}(() => Use(sprite));",
        after: @"
            var sprite = ctx.UseCanvasResources<object>(d => new object());
            return Win2DCanvas(() => Use(sprite)).UseSharedDevice();");

    [Fact]
    public Task CodeFix_Appends_UseSharedDevice_After_Existing_Modifier() => Fix(
        before: @"
            var sprite = ctx.UseCanvasResources<object>(d => new object());
            return VStack({|REACTOR_WIN2D_001:Win2DAnimatedCanvas|}(() => Use(sprite)).ClearColor(0));",
        after: @"
            var sprite = ctx.UseCanvasResources<object>(d => new object());
            return VStack(Win2DAnimatedCanvas(() => Use(sprite)).ClearColor(0).UseSharedDevice());");
}
