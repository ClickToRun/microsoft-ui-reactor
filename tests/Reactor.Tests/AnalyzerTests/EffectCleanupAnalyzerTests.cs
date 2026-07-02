using Microsoft.UI.Reactor.Analyzers;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;
using System.Threading.Tasks;
using Xunit;

namespace Microsoft.UI.Reactor.Tests.AnalyzerTests;

/// <summary>
/// Tests for <see cref="EffectCleanupAnalyzer"/> (<c>REACTOR_LIFECYCLE_002</c>). Stubs a minimal
/// Reactor surface — a <c>RenderContext</c>/<c>Component</c> exposing both the <c>Action</c> and
/// <c>Func&lt;Action&gt;</c> <c>UseEffect</c> overloads — plus lightweight producer types
/// (<c>PeriodicTimer</c>/<c>Timer</c>, an observable-shaped <c>Subscribe</c>, and an event source)
/// so the analyzer's overload selection and lifetime-allocation detection resolve without pulling
/// in the framework.
/// </summary>
public class EffectCleanupAnalyzerTests
{
    private const string Stubs = @"
using System;

namespace System.Runtime.CompilerServices { public static class IsExternalInit { } }

namespace Fakes
{
    // Simple-named producer types the analyzer recognizes syntactically.
    public sealed class PeriodicTimer : IDisposable
    {
        public PeriodicTimer(TimeSpan period) { }
        public void Dispose() { }
        public System.Threading.Tasks.Task<bool> WaitForNextTickAsync() =>
            System.Threading.Tasks.Task.FromResult(true);
    }

    public sealed class Timer
    {
        public Timer(Action callback) { }
        public void Dispose() { }
    }

    public sealed class Subscription : IDisposable { public void Dispose() { } }

    public sealed class Ticker
    {
        // Rx-shaped: Subscribe returns IDisposable.
        public IDisposable Subscribe(Action onNext) => new Subscription();
    }

    public sealed class Producer
    {
        public event Action Ping;
        public void Raise() => Ping?.Invoke();
    }
}

namespace Microsoft.UI.Reactor.Core
{
    using System;

    public class RenderContext
    {
        public void UseEffect(Action effect, params object[] dependencies) { }
        public void UseEffect(Func<Action> effectWithCleanup, params object[] dependencies) { }
    }

    public abstract class Component
    {
        protected internal RenderContext Context { get; } = new RenderContext();
        public abstract string Render();
        protected void UseEffect(Action effect, params object[] dependencies)
            => Context.UseEffect(effect, dependencies);
        protected void UseEffect(Func<Action> effectWithCleanup, params object[] dependencies)
            => Context.UseEffect(effectWithCleanup, dependencies);
        protected (int, Action<Func<int, int>>) UseReducer(int initial) => (0, _ => { });
    }
}
";

    private static Task Verify(string body) =>
        new CSharpAnalyzerTest<EffectCleanupAnalyzer, DefaultVerifier>
        {
            TestCode = Stubs + body,
        }.RunAsync(TestContext.Current.CancellationToken);

    // ── Positive ────────────────────────────────────────────────────────

    // The canonical docs/guide/effects.md "Missing cleanup" example.
    [Fact]
    public Task Fires_On_PeriodicTimer_Without_Cleanup()
        => Verify(@"
namespace TestApp
{
    using System;
    using Microsoft.UI.Reactor.Core;
    using Fakes;

    public sealed class Comp : Component
    {
        public override string Render()
        {
            var (tick, updateTick) = UseReducer(0);
            UseEffect(() =>
            {
                var timer = {|REACTOR_LIFECYCLE_002:new PeriodicTimer(TimeSpan.FromSeconds(1))|};
                System.Threading.Tasks.Task.Run(async () =>
                {
                    while (await timer.WaitForNextTickAsync())
                        updateTick(t => t + 1);
                });
            }, Array.Empty<object>());
            return """";
        }
    }
}");

    [Fact]
    public Task Fires_On_Timer_Without_Cleanup()
        => Verify(@"
namespace TestApp
{
    using System;
    using Microsoft.UI.Reactor.Core;
    using Fakes;

    public sealed class Comp : Component
    {
        public override string Render()
        {
            UseEffect(() =>
            {
                var t = {|REACTOR_LIFECYCLE_002:new Timer(() => { })|};
            }, Array.Empty<object>());
            return """";
        }
    }
}");

    [Fact]
    public Task Fires_On_Subscription_Without_Cleanup()
        => Verify(@"
namespace TestApp
{
    using System;
    using Microsoft.UI.Reactor.Core;
    using Fakes;

    public sealed class Comp : Component
    {
        public override string Render()
        {
            var ticker = new Ticker();
            UseEffect(() =>
            {
                {|REACTOR_LIFECYCLE_002:ticker.Subscribe(() => { })|};
            }, Array.Empty<object>());
            return """";
        }
    }
}");

    [Fact]
    public Task Fires_On_Event_Subscription_Without_Unsubscribe()
        => Verify(@"
namespace TestApp
{
    using System;
    using Microsoft.UI.Reactor.Core;
    using Fakes;

    public sealed class Comp : Component
    {
        void OnPing() { }

        public override string Render()
        {
            var producer = new Producer();
            UseEffect(() =>
            {
                {|REACTOR_LIFECYCLE_002:producer.Ping += OnPing|};
            }, Array.Empty<object>());
            return """";
        }
    }
}");

    // Anchors on RenderContext directly (Context.UseEffect), not just the Component wrapper.
    [Fact]
    public Task Fires_Via_RenderContext_Receiver()
        => Verify(@"
namespace TestApp
{
    using System;
    using Microsoft.UI.Reactor.Core;
    using Fakes;

    public sealed class Comp : Component
    {
        public override string Render()
        {
            Context.UseEffect(() =>
            {
                var t = {|REACTOR_LIFECYCLE_002:new PeriodicTimer(TimeSpan.FromSeconds(1))|};
            }, Array.Empty<object>());
            return """";
        }
    }
}");

    // Expression-bodied effect whose whole body IS the offending subscription.
    [Fact]
    public Task Fires_On_Expression_Bodied_Effect()
        => Verify(@"
namespace TestApp
{
    using System;
    using Microsoft.UI.Reactor.Core;
    using Fakes;

    public sealed class Comp : Component
    {
        public override string Render()
        {
            var ticker = new Ticker();
            UseEffect(() => {|REACTOR_LIFECYCLE_002:ticker.Subscribe(() => { })|}, Array.Empty<object>());
            return """";
        }
    }
}");

    // ── Negative ────────────────────────────────────────────────────────

    // Returning a cleanup selects the Func<Action> overload — the correct pattern.
    [Fact]
    public Task NoFire_When_Cleanup_Returned()
        => Verify(@"
namespace TestApp
{
    using System;
    using Microsoft.UI.Reactor.Core;
    using Fakes;

    public sealed class Comp : Component
    {
        public override string Render()
        {
            UseEffect(() =>
            {
                var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
                return () => timer.Dispose();
            }, Array.Empty<object>());
            return """";
        }
    }
}");

    [Fact]
    public Task NoFire_When_Using_Declaration()
        => Verify(@"
namespace TestApp
{
    using System;
    using Microsoft.UI.Reactor.Core;
    using Fakes;

    public sealed class Comp : Component
    {
        public override string Render()
        {
            UseEffect(() =>
            {
                using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            }, Array.Empty<object>());
            return """";
        }
    }
}");

    [Fact]
    public Task NoFire_When_Disposed_In_Body()
        => Verify(@"
namespace TestApp
{
    using System;
    using Microsoft.UI.Reactor.Core;
    using Fakes;

    public sealed class Comp : Component
    {
        public override string Render()
        {
            UseEffect(() =>
            {
                var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
                timer.Dispose();
            }, Array.Empty<object>());
            return """";
        }
    }
}");

    [Fact]
    public Task NoFire_When_Event_Unsubscribed_In_Body()
        => Verify(@"
namespace TestApp
{
    using System;
    using Microsoft.UI.Reactor.Core;
    using Fakes;

    public sealed class Comp : Component
    {
        void OnPing() { }

        public override string Render()
        {
            var producer = new Producer();
            UseEffect(() =>
            {
                producer.Ping += OnPing;
                producer.Ping -= OnPing;
            }, Array.Empty<object>());
            return """";
        }
    }
}");

    // A numeric -= must NOT be mistaken for an event unsubscribe, so the timer still fires.
    [Fact]
    public Task Fires_Even_With_Numeric_CompoundAssign()
        => Verify(@"
namespace TestApp
{
    using System;
    using Microsoft.UI.Reactor.Core;
    using Fakes;

    public sealed class Comp : Component
    {
        public override string Render()
        {
            var count = 5;
            UseEffect(() =>
            {
                var timer = {|REACTOR_LIFECYCLE_002:new PeriodicTimer(TimeSpan.FromSeconds(1))|};
                count -= 1;
            }, Array.Empty<object>());
            return """";
        }
    }
}");

    // No lifetime resource at all — pure side effect.
    [Fact]
    public Task NoFire_When_No_Lifetime_Resource()
        => Verify(@"
namespace TestApp
{
    using System;
    using Microsoft.UI.Reactor.Core;

    public sealed class Comp : Component
    {
        public override string Render()
        {
            UseEffect(() =>
            {
                Console.WriteLine(""side effect"");
            }, Array.Empty<object>());
            return """";
        }
    }
}");

    // Producer created inside a nested continuation has its own lifetime — not effect setup, so the
    // top-level-only allocation scan skips it even though the effect returns no cleanup.
    [Fact]
    public Task NoFire_When_Resource_In_Nested_Lambda()
        => Verify(@"
namespace TestApp
{
    using System;
    using Microsoft.UI.Reactor.Core;
    using Fakes;

    public sealed class Comp : Component
    {
        public override string Render()
        {
            UseEffect(() =>
            {
                System.Threading.Tasks.Task.Run(() =>
                {
                    var t = new PeriodicTimer(TimeSpan.FromSeconds(1));
                });
            }, Array.Empty<object>());
            return """";
        }
    }
}");

    // UseEffect on an unrelated (non-Reactor) type must not be flagged.
    [Fact]
    public Task NoFire_When_Not_Reactor_UseEffect()
        => Verify(@"
namespace TestApp
{
    using System;
    using Fakes;

    public sealed class NotReactor
    {
        public void UseEffect(Action effect, params object[] deps) { }

        public void Setup()
        {
            UseEffect(() =>
            {
                var t = new PeriodicTimer(TimeSpan.FromSeconds(1));
            }, Array.Empty<object>());
        }
    }
}");

    // Near-miss: a method group hides the body, so the rule can't prove a leak — bail.
    [Fact]
    public Task NoFire_On_Method_Group_Effect()
        => Verify(@"
namespace TestApp
{
    using System;
    using Microsoft.UI.Reactor.Core;
    using Fakes;

    public sealed class Comp : Component
    {
        void SetUp()
        {
            var t = new PeriodicTimer(TimeSpan.FromSeconds(1));
        }

        public override string Render()
        {
            UseEffect(SetUp, Array.Empty<object>());
            return """";
        }
    }
}");

    // Near-miss: a similarly-named hook that isn't UseEffect never trips the syntactic fast path.
    [Fact]
    public Task NoFire_On_Similarly_Named_Hook()
        => Verify(@"
namespace TestApp
{
    using System;
    using Microsoft.UI.Reactor.Core;
    using Fakes;

    public static class Extra
    {
        public static void UseLayoutEffect(this RenderContext ctx, Action effect, params object[] deps) { }
    }

    public sealed class Comp : Component
    {
        public override string Render()
        {
            Context.UseLayoutEffect(() =>
            {
                var t = new PeriodicTimer(TimeSpan.FromSeconds(1));
            }, Array.Empty<object>());
            return """";
        }
    }
}");
}
