using Microsoft.UI.Reactor.Analyzers;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;
using System.Threading.Tasks;
using Xunit;

namespace Microsoft.UI.Reactor.Tests.AnalyzerTests;

/// <summary>
/// Tests for <see cref="UIThreadAffinityAnalyzer"/> (<c>REACTOR_THREAD_001</c>) and
/// its <see cref="UIThreadAffinityCodeFix"/>. Stubs a minimal Reactor shape — a
/// <c>[UIThreadOnly]</c>-marked mutator, the <c>ReactorApp.UIDispatcher</c> /
/// <c>DispatcherQueue.TryEnqueue</c> marshal path — so the analyzer's
/// background-lambda gate and attribute check fire without pulling the framework in.
/// The stub attribute reuses the real namespace/name (<c>Microsoft.UI.Reactor.Hosting.UIThreadOnlyAttribute</c>)
/// that the analyzer keys off in metadata.
/// </summary>
public class UIThreadAffinityAnalyzerTests
{
    private const string Stubs = @"
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Hosting;

namespace Microsoft.UI.Dispatching
{
    public sealed class DispatcherQueue
    {
        public bool TryEnqueue(Action callback) { callback(); return true; }
    }
}

namespace Microsoft.UI.Reactor.Hosting
{
    [AttributeUsage(AttributeTargets.Method | AttributeTargets.Property, Inherited = false)]
    public sealed class UIThreadOnlyAttribute : Attribute { }
}

namespace Microsoft.UI.Reactor
{
    public static class ReactorApp
    {
        public static DispatcherQueue UIDispatcher = new DispatcherQueue();
    }
}

public sealed class FakeWindow
{
    [UIThreadOnly] public void Close() { }
    [UIThreadOnly] public void Activate() { }

    // Not UI-thread-only — background use is legitimate.
    public void SafeMethod() { }
}
";

    // ── Positive: fires inside each background launcher ──────────────────

    [Fact]
    public async Task Fires_For_Marked_Method_In_TaskRun_ExpressionLambda()
    {
        var source = Stubs + @"
class C
{
    void M()
    {
        var window = new FakeWindow();
        Task.Run(() => {|REACTOR_THREAD_001:window.Close()|});
    }
}";

        await new CSharpAnalyzerTest<UIThreadAffinityAnalyzer, DefaultVerifier>
        {
            TestCode = source,
        }.RunAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Fires_For_Marked_Method_In_TaskRun_Block()
    {
        var source = Stubs + @"
class C
{
    void M()
    {
        var window = new FakeWindow();
        Task.Run(() =>
        {
            {|REACTOR_THREAD_001:window.Close()|};
        });
    }
}";

        await new CSharpAnalyzerTest<UIThreadAffinityAnalyzer, DefaultVerifier>
        {
            TestCode = source,
        }.RunAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Fires_For_Marked_Method_In_TaskFactoryStartNew()
    {
        var source = Stubs + @"
class C
{
    void M()
    {
        var window = new FakeWindow();
        Task.Factory.StartNew(() => {|REACTOR_THREAD_001:window.Close()|});
    }
}";

        await new CSharpAnalyzerTest<UIThreadAffinityAnalyzer, DefaultVerifier>
        {
            TestCode = source,
        }.RunAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Fires_For_Marked_Method_In_ThreadPool_QueueUserWorkItem()
    {
        var source = Stubs + @"
class C
{
    void M()
    {
        var window = new FakeWindow();
        ThreadPool.QueueUserWorkItem(_ => {|REACTOR_THREAD_001:window.Close()|});
    }
}";

        await new CSharpAnalyzerTest<UIThreadAffinityAnalyzer, DefaultVerifier>
        {
            TestCode = source,
        }.RunAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Fires_For_Marked_Method_Nested_In_Inner_Lambda()
    {
        // The call is two lambdas deep inside Task.Run — the inner LINQ-style
        // lambda is transparent, the Task.Run boundary still governs the thread.
        var source = Stubs + @"
class C
{
    void M(List<int> items)
    {
        var window = new FakeWindow();
        Task.Run(() => items.ForEach(x => {|REACTOR_THREAD_001:window.Close()|}));
    }
}";

        await new CSharpAnalyzerTest<UIThreadAffinityAnalyzer, DefaultVerifier>
        {
            TestCode = source,
        }.RunAsync(TestContext.Current.CancellationToken);
    }

    // ── Negative: marshaled or unmarked ─────────────────────────────────

    [Fact]
    public async Task No_Diagnostic_When_Already_Marshaled_Through_TryEnqueue()
    {
        var source = Stubs + @"
class C
{
    void M()
    {
        var window = new FakeWindow();
        Task.Run(() => ReactorApp.UIDispatcher.TryEnqueue(() => window.Close()));
    }
}";

        await new CSharpAnalyzerTest<UIThreadAffinityAnalyzer, DefaultVerifier>
        {
            TestCode = source,
        }.RunAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task No_Diagnostic_For_Unmarked_Method_In_TaskRun()
    {
        var source = Stubs + @"
class C
{
    void M()
    {
        var window = new FakeWindow();
        Task.Run(() => window.SafeMethod());
    }
}";

        await new CSharpAnalyzerTest<UIThreadAffinityAnalyzer, DefaultVerifier>
        {
            TestCode = source,
        }.RunAsync(TestContext.Current.CancellationToken);
    }

    // ── Near-miss: almost trips the syntactic fast path ─────────────────

    [Fact]
    public async Task No_Diagnostic_For_Marked_Method_On_UI_Thread()
    {
        // Called directly — not inside any background lambda. This is the correct
        // UI-thread call site and must not fire.
        var source = Stubs + @"
class C
{
    void M()
    {
        var window = new FakeWindow();
        window.Close();
    }
}";

        await new CSharpAnalyzerTest<UIThreadAffinityAnalyzer, DefaultVerifier>
        {
            TestCode = source,
        }.RunAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task No_Diagnostic_For_Marked_Method_In_Plain_Lambda()
    {
        // A lambda that is not passed to a background launcher — e.g. assigned to
        // an Action — runs on whatever thread invokes it; the gate must not fire.
        var source = Stubs + @"
class C
{
    void M()
    {
        var window = new FakeWindow();
        Action a = () => window.Close();
        a();
    }
}";

        await new CSharpAnalyzerTest<UIThreadAffinityAnalyzer, DefaultVerifier>
        {
            TestCode = source,
        }.RunAsync(TestContext.Current.CancellationToken);
    }

    // ── Code fix: null-safe dispatcher marshal ──────────────────────────

    [Fact]
    public async Task CodeFix_Marshals_ExpressionLambda_Call()
    {
        var before = Stubs + @"
class C
{
    void M()
    {
        var window = new FakeWindow();
        Task.Run(() => {|REACTOR_THREAD_001:window.Close()|});
    }
}";

        var after = Stubs + @"
class C
{
    void M()
    {
        var window = new FakeWindow();
        Task.Run(() =>
        {
            var d = ReactorApp.UIDispatcher;
            if (d is null)
                window.Close();
            else
                d.TryEnqueue(() => window.Close());
        });
    }
}";

        await new CSharpCodeFixTest<UIThreadAffinityAnalyzer, UIThreadAffinityCodeFix, DefaultVerifier>
        {
            TestCode = before,
            FixedCode = after,
        }.RunAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task CodeFix_Marshals_Statement_In_Block()
    {
        var before = Stubs + @"
class C
{
    void M()
    {
        var window = new FakeWindow();
        Task.Run(() =>
        {
            {|REACTOR_THREAD_001:window.Close()|};
        });
    }
}";

        var after = Stubs + @"
class C
{
    void M()
    {
        var window = new FakeWindow();
        Task.Run(() =>
        {
            var d = ReactorApp.UIDispatcher;
            if (d is null)
                window.Close();
            else
                d.TryEnqueue(() => window.Close());
        });
    }
}";

        await new CSharpCodeFixTest<UIThreadAffinityAnalyzer, UIThreadAffinityCodeFix, DefaultVerifier>
        {
            TestCode = before,
            FixedCode = after,
        }.RunAsync(TestContext.Current.CancellationToken);
    }
}
