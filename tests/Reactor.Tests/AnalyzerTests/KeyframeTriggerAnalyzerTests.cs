using Microsoft.UI.Reactor.Analyzers;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;
using System.Threading.Tasks;
using Xunit;

namespace Microsoft.UI.Reactor.Tests.AnalyzerTests;

/// <summary>
/// Tests for <see cref="KeyframeTriggerAnalyzer"/> (<c>REACTOR_ANIM_002</c>).
/// Stubs the minimum <c>.Keyframes(name, trigger, configure)</c> surface so the
/// analyzer's syntactic gate fires without pulling the framework in.
/// </summary>
public class KeyframeTriggerAnalyzerTests
{
    // `IsExternalInit` is required for `record` types under older runtime
    // metadata — supply a stub so test sources can use records freely.
    private const string Stubs = @"
namespace System.Runtime.CompilerServices
{
    public static class IsExternalInit { }
}

namespace Microsoft.UI.Reactor.Core
{
    public abstract record Element { }
    public sealed record BorderElement : Element { }

    public sealed class KeyframeBuilder
    {
        public KeyframeBuilder Opacity(double from, double to) => this;
    }

    public static class Factories
    {
        public static BorderElement Border() => new();
    }

    public static class ElementExtensions
    {
        // The real 3-arg modifier: name, trigger, configure.
        public static T Keyframes<T>(this T el, string name, object? trigger,
            System.Func<KeyframeBuilder, KeyframeBuilder> configure) where T : Element => el;

        // A 2-arg near-miss overload used to prove the arity gate.
        public static T Keyframes<T>(this T el, string name, object? trigger) where T : Element => el;
    }
}
";

    private static Task Verify(string body) =>
        new CSharpAnalyzerTest<KeyframeTriggerAnalyzer, DefaultVerifier>
        {
            TestCode = Stubs + @"
namespace TestApp
{
    using System;
    using System.Collections.Generic;
    using Microsoft.UI.Reactor.Core;
    using static Microsoft.UI.Reactor.Core.Factories;

    public static class C
    {
        public static Element Build(int stableCounter, string name)
        {
            var stableKey = stableCounter;
" + body + @"
        }
    }
}",
        }.RunAsync(TestContext.Current.CancellationToken);

    // ── Positive: unstable triggers fire ────────────────────────────────

    [Fact]
    public Task Fires_On_DateTime_Now() =>
        Verify(@"            return Border().Keyframes(""pulse"", {|REACTOR_ANIM_002:DateTime.Now|}, kf => kf.Opacity(0, 1));");

    [Fact]
    public Task Fires_On_DateTime_UtcNow() =>
        Verify(@"            return Border().Keyframes(""pulse"", {|REACTOR_ANIM_002:DateTime.UtcNow|}, kf => kf.Opacity(0, 1));");

    [Fact]
    public Task Fires_On_Guid_NewGuid() =>
        Verify(@"            return Border().Keyframes(""pulse"", {|REACTOR_ANIM_002:Guid.NewGuid()|}, kf => kf.Opacity(0, 1));");

    [Fact]
    public Task Fires_On_Environment_TickCount() =>
        Verify(@"            return Border().Keyframes(""pulse"", {|REACTOR_ANIM_002:Environment.TickCount|}, kf => kf.Opacity(0, 1));");

    [Fact]
    public Task Fires_On_Fresh_Object_Allocation() =>
        Verify(@"            return Border().Keyframes(""pulse"", {|REACTOR_ANIM_002:new List<int>()|}, kf => kf.Opacity(0, 1));");

    [Fact]
    public Task Fires_On_Fresh_Array_Allocation() =>
        Verify(@"            return Border().Keyframes(""pulse"", {|REACTOR_ANIM_002:new int[] { 1, 2, 3 }|}, kf => kf.Opacity(0, 1));");

    [Fact]
    public Task Fires_On_Named_Trigger_Argument_Reordered() =>
        // Named args in a different order — the analyzer must still find `trigger`.
        Verify(@"            return Border().Keyframes(configure: kf => kf.Opacity(0, 1), name: ""pulse"", trigger: {|REACTOR_ANIM_002:DateTime.Now|});");

    // ── Negative: stable triggers do not fire ───────────────────────────

    [Fact]
    public Task No_Diagnostic_On_Stable_Local() =>
        Verify(@"            return Border().Keyframes(""pulse"", stableKey, kf => kf.Opacity(0, 1));");

    [Fact]
    public Task No_Diagnostic_On_Stable_Parameter() =>
        Verify(@"            return Border().Keyframes(""pulse"", stableCounter, kf => kf.Opacity(0, 1));");

    // ── Near-miss: almost trips the syntactic fast path, but must not ───

    [Fact]
    public Task No_Diagnostic_When_Unstable_Value_Is_In_Name_Arg_Not_Trigger() =>
        // Allocation/unstable in the NAME slot; the trigger itself is stable.
        // Proves the analyzer inspects only the trigger argument (index 1).
        Verify(@"            return Border().Keyframes(Guid.NewGuid().ToString(), stableKey, kf => kf.Opacity(0, 1));");

    [Fact]
    public Task No_Diagnostic_On_Two_Arg_Overload() =>
        // Wrong arity — the 2-arg overload isn't the trigger-based modifier.
        Verify(@"            return Border().Keyframes(""pulse"", DateTime.Now);");
}
