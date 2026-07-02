using Microsoft.UI.Reactor.Analyzers;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;
using System.Threading.Tasks;
using Xunit;

namespace Microsoft.UI.Reactor.Tests.AnalyzerTests;

/// <summary>
/// Tests for <see cref="BlockingTaskAnalyzer"/> (<c>REACTOR_THREAD_002</c>). Stubs a
/// minimal Reactor-shaped <c>Component</c> / <c>RenderContext</c> (with a real
/// <c>UseEffect</c> overload set) so the analyzer's Render/effect context walk and its
/// semantic <c>Task</c>-receiver confirmation both fire without pulling the framework in.
/// </summary>
public class BlockingTaskAnalyzerTests
{
    // Shapes the two anchoring types the analyzer keys off — Component (with a Render()
    // override target + protected UseEffect wrappers) and RenderContext (public UseEffect)
    // — under the real Microsoft.UI.Reactor.Core namespace, plus a couple of async helpers.
    private const string Stubs = @"
using System;
using System.Threading.Tasks;

namespace Microsoft.UI.Reactor.Core
{
    public abstract class Element { }

    public sealed class RenderContext
    {
        public void UseEffect(Action effect, params object[] dependencies) { }
        public void UseEffect(Func<Action> effectWithCleanup, params object[] dependencies) { }
    }

    public abstract class Component
    {
        protected RenderContext Context = new RenderContext();
        public abstract Element Render();
        protected void UseEffect(Action effect, params object[] dependencies) { }
        protected void UseEffect(Func<Action> effectWithCleanup, params object[] dependencies) { }
    }
}

namespace App
{
    using Microsoft.UI.Reactor.Core;

    public sealed class TextElement : Element
    {
        public TextElement(string s) { }
    }

    public static class Data
    {
        public static Task<int> FetchAsync() => Task.FromResult(1);
        public static ValueTask<int> FetchValueAsync() => new ValueTask<int>(1);
        public static Task RunAsync() => Task.CompletedTask;
    }

    // A non-Task type that also exposes a .Result member — must never trip the rule.
    public sealed class Poll
    {
        public int Result => 42;
    }
}
";

    private static Task VerifyAsync(string body) =>
        new CSharpAnalyzerTest<BlockingTaskAnalyzer, DefaultVerifier>
        {
            TestCode = Stubs + body,
        }.RunAsync(TestContext.Current.CancellationToken);

    // ── Positive: blocking inside Render() ──────────────────────────────

    [Fact]
    public async Task Fires_For_Result_In_Render()
    {
        await VerifyAsync(@"
namespace App
{
    using Microsoft.UI.Reactor.Core;
    public sealed class C : Component
    {
        public override Element Render()
        {
            var data = {|REACTOR_THREAD_002:Data.FetchAsync().Result|};
            return new TextElement(data.ToString());
        }
    }
}");
    }

    [Fact]
    public async Task Fires_For_Wait_In_Render()
    {
        await VerifyAsync(@"
namespace App
{
    using Microsoft.UI.Reactor.Core;
    public sealed class C : Component
    {
        public override Element Render()
        {
            {|REACTOR_THREAD_002:Data.RunAsync().Wait()|};
            return new TextElement(""hi"");
        }
    }
}");
    }

    [Fact]
    public async Task Fires_For_GetAwaiter_GetResult_In_Render()
    {
        await VerifyAsync(@"
namespace App
{
    using Microsoft.UI.Reactor.Core;
    public sealed class C : Component
    {
        public override Element Render()
        {
            var data = {|REACTOR_THREAD_002:Data.FetchAsync().GetAwaiter().GetResult()|};
            return new TextElement(data.ToString());
        }
    }
}");
    }

    [Fact]
    public async Task Fires_For_ValueTask_Result_In_Render()
    {
        await VerifyAsync(@"
namespace App
{
    using Microsoft.UI.Reactor.Core;
    public sealed class C : Component
    {
        public override Element Render()
        {
            var data = {|REACTOR_THREAD_002:Data.FetchValueAsync().Result|};
            return new TextElement(data.ToString());
        }
    }
}");
    }

    // ── Positive: blocking inside a UseEffect lambda ────────────────────

    [Fact]
    public async Task Fires_For_Result_In_UseEffect_Lambda()
    {
        await VerifyAsync(@"
namespace App
{
    using Microsoft.UI.Reactor.Core;
    public sealed class C : Component
    {
        public override Element Render()
        {
            UseEffect(() =>
            {
                var data = {|REACTOR_THREAD_002:Data.FetchAsync().Result|};
            }, System.Array.Empty<object>());
            return new TextElement(""hi"");
        }
    }
}");
    }

    [Fact]
    public async Task Fires_For_Result_In_RenderContext_UseEffect_Lambda()
    {
        await VerifyAsync(@"
namespace App
{
    using Microsoft.UI.Reactor.Core;
    public sealed class C : Component
    {
        public override Element Render()
        {
            Context.UseEffect(() =>
            {
                {|REACTOR_THREAD_002:Data.RunAsync().Wait()|};
            }, System.Array.Empty<object>());
            return new TextElement(""hi"");
        }
    }
}");
    }

    // ── Negative: nested Task.Run inside Render (background thread) ──────

    [Fact]
    public async Task No_Diagnostic_For_Result_Inside_Nested_TaskRun()
    {
        await VerifyAsync(@"
namespace App
{
    using Microsoft.UI.Reactor.Core;
    using System.Threading.Tasks;
    public sealed class C : Component
    {
        public override Element Render()
        {
            _ = Task.Run(() =>
            {
                var data = Data.FetchAsync().Result;
                return data;
            });
            return new TextElement(""hi"");
        }
    }
}");
    }

    [Fact]
    public async Task No_Diagnostic_For_GetResult_Inside_Nested_TaskRun_In_Effect()
    {
        // Task.Run inside a UseEffect body still moves the block off the UI thread.
        await VerifyAsync(@"
namespace App
{
    using Microsoft.UI.Reactor.Core;
    using System.Threading.Tasks;
    public sealed class C : Component
    {
        public override Element Render()
        {
            UseEffect(() =>
            {
                _ = Task.Run(() => Data.FetchAsync().GetAwaiter().GetResult());
            }, System.Array.Empty<object>());
            return new TextElement(""hi"");
        }
    }
}");
    }

    // ── Negative: .Result on a non-Task property ───────────────────────

    [Fact]
    public async Task No_Diagnostic_For_Result_On_Non_Task()
    {
        await VerifyAsync(@"
namespace App
{
    using Microsoft.UI.Reactor.Core;
    public sealed class C : Component
    {
        public override Element Render()
        {
            var poll = new Poll();
            var value = poll.Result;
            return new TextElement(value.ToString());
        }
    }
}");
    }

    // ── Near-miss: blocking OUTSIDE any render/effect context ──────────

    [Fact]
    public async Task No_Diagnostic_For_Result_Outside_Render_Or_Effect()
    {
        // Same Data.FetchAsync().Result shape, but in a plain method on a Component —
        // not Render(), not a UseEffect lambda. This is the syntactic near-miss that the
        // context walk must reject.
        await VerifyAsync(@"
namespace App
{
    using Microsoft.UI.Reactor.Core;
    public sealed class C : Component
    {
        public override Element Render() => new TextElement(""hi"");

        public int LoadSync()
        {
            return Data.FetchAsync().Result;
        }
    }
}");
    }

    [Fact]
    public async Task No_Diagnostic_For_Result_In_NonComponent_Render()
    {
        // A Render() override that is NOT on a Reactor Component must not fire.
        await VerifyAsync(@"
namespace App
{
    using Microsoft.UI.Reactor.Core;
    public abstract class Drawable
    {
        public abstract void Render();
    }
    public sealed class C : Drawable
    {
        public override void Render()
        {
            var data = Data.FetchAsync().Result;
        }
    }
}");
    }

    [Fact]
    public async Task No_Diagnostic_For_Awaited_Task_In_Render()
    {
        // The correct async form: an async effect that awaits. No blocking member.
        await VerifyAsync(@"
namespace App
{
    using Microsoft.UI.Reactor.Core;
    public sealed class C : Component
    {
        public override Element Render()
        {
            UseEffect(() => Load(), System.Array.Empty<object>());
            return new TextElement(""hi"");
        }

        private static async void Load()
        {
            var data = await Data.FetchAsync();
        }
    }
}");
    }
}
