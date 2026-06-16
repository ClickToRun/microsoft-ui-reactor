using Microsoft.UI.Reactor.Analyzers;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;
using System.Threading.Tasks;
using Xunit;

namespace Microsoft.UI.Reactor.Tests.AnalyzerTests;

/// <summary>
/// Tests for <see cref="OnLiveLayoutRoundTripAnalyzer"/>
/// (<c>REACTOR_DOCK_001</c>). Stubs the real <c>DockManager</c> shape — a
/// <c>sealed record : Element</c> with <c>init</c>-only <c>Layout</c> and
/// <c>OnLiveLayoutChanged</c>, plus the fluent <c>LiveLayoutChanged</c> modifier
/// — so the analyzer's syntax-only heuristic fires without pulling the framework
/// in. Positive cases: forwarding the live layout into a state setter (raw
/// assignment, block body, parenthesized lambda, fluent modifier). Negative
/// cases: observation-only, logging, empty/multi-statement bodies, transformed
/// or extra arguments, method groups, and null handlers.
/// </summary>
public class OnLiveLayoutRoundTripAnalyzerTests
{
    // Faithful Reactor docking surface: DockManager is a record deriving from
    // Element with init-only Layout / OnLiveLayoutChanged, the DockNode shape,
    // the fluent LiveLayoutChanged modifier, and a LayoutInspector for
    // observation tests. `IsExternalInit` is required for records + init.
    private const string Stubs = @"
namespace System.Runtime.CompilerServices
{
    public static class IsExternalInit { }
}

namespace Microsoft.UI.Reactor.Core
{
    public abstract record Element { }
}

namespace Microsoft.UI.Reactor.Docking
{
    using System;
    using Microsoft.UI.Reactor.Core;

    public abstract record DockNode;

    public sealed record DockManager : Element
    {
        public DockNode? Layout { get; init; }
        public Action<DockNode?>? OnLiveLayoutChanged { get; init; }
    }

    public static class DockManagerExtensions
    {
        public static DockManager LiveLayoutChanged(this DockManager el, System.Action<DockNode?>? h)
            => el with { OnLiveLayoutChanged = h };
    }

    public sealed class LayoutInspector
    {
        public void Update(DockNode? layout) { }
    }

    // App-owned state mirror with a settable Layout — models storing the live
    // layout back into application state (the footgun the analyzer flags).
    public sealed class LayoutHolder
    {
        public DockNode? Layout { get; set; }
    }
}
";

    // ── Positive: setter forwarding ─────────────────────────────────────

    [Fact]
    public async Task Fires_When_Handler_Forwards_To_State_Setter()
    {
        var source = Stubs + @"
namespace TestApp
{
    using System;
    using Microsoft.UI.Reactor.Docking;

    public static class C
    {
        public static DockManager Build(Action<DockNode?> setLayout)
            => new DockManager
            {
                {|REACTOR_DOCK_001:OnLiveLayoutChanged = next => setLayout(next)|}
            };
    }
}";

        await new CSharpAnalyzerTest<OnLiveLayoutRoundTripAnalyzer, DefaultVerifier>
        {
            TestCode = source,
        }.RunAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Fires_When_Handler_Forwards_Via_Block_Body()
    {
        var source = Stubs + @"
namespace TestApp
{
    using System;
    using Microsoft.UI.Reactor.Docking;

    public static class C
    {
        public static DockManager Build(Action<DockNode?> setLayout)
            => new DockManager
            {
                {|REACTOR_DOCK_001:OnLiveLayoutChanged = next => { setLayout(next); }|}
            };
    }
}";

        await new CSharpAnalyzerTest<OnLiveLayoutRoundTripAnalyzer, DefaultVerifier>
        {
            TestCode = source,
        }.RunAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Fires_When_Handler_Assigns_Back_To_Layout()
    {
        var source = Stubs + @"
namespace TestApp
{
    using Microsoft.UI.Reactor.Docking;

    public static class C
    {
        public static DockManager Build(LayoutHolder target)
            => new DockManager
            {
                {|REACTOR_DOCK_001:OnLiveLayoutChanged = next => target.Layout = next|}
            };
    }
}";

        await new CSharpAnalyzerTest<OnLiveLayoutRoundTripAnalyzer, DefaultVerifier>
        {
            TestCode = source,
        }.RunAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Fires_For_Parenthesized_Lambda()
    {
        var source = Stubs + @"
namespace TestApp
{
    using System;
    using Microsoft.UI.Reactor.Docking;

    public static class C
    {
        public static DockManager Build(Action<DockNode?> setLayout)
            => new DockManager
            {
                {|REACTOR_DOCK_001:OnLiveLayoutChanged = (next) => setLayout(next)|}
            };
    }
}";

        await new CSharpAnalyzerTest<OnLiveLayoutRoundTripAnalyzer, DefaultVerifier>
        {
            TestCode = source,
        }.RunAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Fires_For_Fluent_Modifier_Setter()
    {
        var source = Stubs + @"
namespace TestApp
{
    using System;
    using Microsoft.UI.Reactor.Docking;

    public static class C
    {
        public static DockManager Build(DockManager someManager, Action<DockNode?> setLayout)
            => {|REACTOR_DOCK_001:someManager.LiveLayoutChanged(next => setLayout(next))|};
    }
}";

        await new CSharpAnalyzerTest<OnLiveLayoutRoundTripAnalyzer, DefaultVerifier>
        {
            TestCode = source,
        }.RunAsync(TestContext.Current.CancellationToken);
    }

    // ── Negative: observation-only ──────────────────────────────────────

    [Fact]
    public async Task No_Diagnostic_For_Observation_Only_Handler()
    {
        // Forwards the layout into an inspector (member-access call) — this is
        // the sanctioned observation-only use, not a state round-trip.
        var source = Stubs + @"
namespace TestApp
{
    using Microsoft.UI.Reactor.Docking;

    public static class C
    {
        public static DockManager Build(LayoutInspector inspector)
            => new DockManager
            {
                OnLiveLayoutChanged = next => inspector.Update(next)
            };
    }
}";

        await new CSharpAnalyzerTest<OnLiveLayoutRoundTripAnalyzer, DefaultVerifier>
        {
            TestCode = source,
        }.RunAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task No_Diagnostic_For_Fluent_Observation()
    {
        // Fluent modifier whose body is a member-access call — observation, not
        // a round-trip. Proves the shared helper applies the same exclusion.
        var source = Stubs + @"
namespace TestApp
{
    using Microsoft.UI.Reactor.Docking;

    public static class C
    {
        public static DockManager Build(DockManager someManager, LayoutInspector inspector)
            => someManager.LiveLayoutChanged(next => inspector.Update(next));
    }
}";

        await new CSharpAnalyzerTest<OnLiveLayoutRoundTripAnalyzer, DefaultVerifier>
        {
            TestCode = source,
        }.RunAsync(TestContext.Current.CancellationToken);
    }

    // ── Negative: logging ───────────────────────────────────────────────

    [Fact]
    public async Task No_Diagnostic_For_Logging_Handler()
    {
        var source = Stubs + @"
namespace TestApp
{
    using System;
    using Microsoft.UI.Reactor.Docking;

    public static class C
    {
        public static DockManager Build()
            => new DockManager
            {
                OnLiveLayoutChanged = next => Console.WriteLine(next)
            };
    }
}";

        await new CSharpAnalyzerTest<OnLiveLayoutRoundTripAnalyzer, DefaultVerifier>
        {
            TestCode = source,
        }.RunAsync(TestContext.Current.CancellationToken);
    }

    // ── Negative: empty body ────────────────────────────────────────────

    [Fact]
    public async Task No_Diagnostic_For_Empty_Handler_Body()
    {
        var source = Stubs + @"
namespace TestApp
{
    using Microsoft.UI.Reactor.Docking;

    public static class C
    {
        public static DockManager Build()
            => new DockManager
            {
                OnLiveLayoutChanged = next => { }
            };
    }
}";

        await new CSharpAnalyzerTest<OnLiveLayoutRoundTripAnalyzer, DefaultVerifier>
        {
            TestCode = source,
        }.RunAsync(TestContext.Current.CancellationToken);
    }

    // ── Negative: null handler ──────────────────────────────────────────

    [Fact]
    public async Task No_Diagnostic_For_Null_Handler()
    {
        var source = Stubs + @"
namespace TestApp
{
    using Microsoft.UI.Reactor.Docking;

    public static class C
    {
        public static DockManager Build()
            => new DockManager
            {
                OnLiveLayoutChanged = null
            };
    }
}";

        await new CSharpAnalyzerTest<OnLiveLayoutRoundTripAnalyzer, DefaultVerifier>
        {
            TestCode = source,
        }.RunAsync(TestContext.Current.CancellationToken);
    }

    // ── Negative: method group ──────────────────────────────────────────

    [Fact]
    public async Task No_Diagnostic_For_Method_Group()
    {
        var source = Stubs + @"
namespace TestApp
{
    using Microsoft.UI.Reactor.Docking;

    public static class C
    {
        static void Observe(DockNode? n) {}

        public static DockManager Build()
            => new DockManager
            {
                OnLiveLayoutChanged = Observe
            };
    }
}";

        await new CSharpAnalyzerTest<OnLiveLayoutRoundTripAnalyzer, DefaultVerifier>
        {
            TestCode = source,
        }.RunAsync(TestContext.Current.CancellationToken);
    }

    // ── Negative: multi-statement block ─────────────────────────────────

    [Fact]
    public async Task No_Diagnostic_For_MultiStatement_Block()
    {
        var source = Stubs + @"
namespace TestApp
{
    using Microsoft.UI.Reactor.Docking;

    public static class C
    {
        static void Inspect(DockNode? n) {}

        public static DockManager Build()
            => new DockManager
            {
                OnLiveLayoutChanged = next => { Inspect(next); Inspect(next); }
            };
    }
}";

        await new CSharpAnalyzerTest<OnLiveLayoutRoundTripAnalyzer, DefaultVerifier>
        {
            TestCode = source,
        }.RunAsync(TestContext.Current.CancellationToken);
    }

    // ── Negative: transformed parameter ─────────────────────────────────

    [Fact]
    public async Task No_Diagnostic_For_Transformed_Param()
    {
        var source = Stubs + @"
namespace TestApp
{
    using System;
    using Microsoft.UI.Reactor.Docking;

    public static class C
    {
        static DockNode? Wrap(DockNode? n) => n;

        public static DockManager Build(Action<DockNode?> setLayout)
            => new DockManager
            {
                OnLiveLayoutChanged = next => setLayout(Wrap(next))
            };
    }
}";

        await new CSharpAnalyzerTest<OnLiveLayoutRoundTripAnalyzer, DefaultVerifier>
        {
            TestCode = source,
        }.RunAsync(TestContext.Current.CancellationToken);
    }

    // ── Negative: extra arguments ───────────────────────────────────────

    [Fact]
    public async Task No_Diagnostic_For_Extra_Args()
    {
        var source = Stubs + @"
namespace TestApp
{
    using System;
    using Microsoft.UI.Reactor.Docking;

    public static class C
    {
        public static DockManager Build(Action<DockNode?, bool> setLayout2)
            => new DockManager
            {
                OnLiveLayoutChanged = next => setLayout2(next, true)
            };
    }
}";

        await new CSharpAnalyzerTest<OnLiveLayoutRoundTripAnalyzer, DefaultVerifier>
        {
            TestCode = source,
        }.RunAsync(TestContext.Current.CancellationToken);
    }
}
