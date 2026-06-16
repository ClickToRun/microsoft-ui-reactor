using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Hooks;
using Xunit;

namespace Microsoft.UI.Reactor.Tests.Core;

/// <summary>
/// Unit tests for <see cref="PendingScope"/> and the <c>UseResource</c> / <c>UseInfiniteResource</c>
/// integration with it. The <c>Pending</c> element itself requires the reconciler +
/// a real WinUI tree to exercise — that's covered in the selfhost suite. These
/// tests pin the ref-count machinery and the Loading-vs-Reloading distinction.
/// </summary>
public class PendingTests
{
    private sealed class InlineDispatcher : IHookDispatcher
    {
        public void Post(Action action) => action();
    }

    // ════════════════════════════════════════════════════════════════
    //  Scope ref-count transitions
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public void Register_AnyLoading_True_Until_SetLoading_False()
    {
        var s = new PendingScope();
        var tokenA = new object();
        s.Register(tokenA, isLoading: true);
        Assert.True(s.AnyLoading);
        s.SetLoading(tokenA, false);
        Assert.False(s.AnyLoading);
    }

    [Fact]
    public void Changed_Fires_On_Register_SetLoading_Unregister()
    {
        var s = new PendingScope();
        int changed = 0;
        s.Changed += () => Interlocked.Increment(ref changed);

        var t = new object();
        s.Register(t, true); Assert.Equal(1, changed);
        s.SetLoading(t, false); Assert.Equal(2, changed);
        s.SetLoading(t, false); // no-op — no event
        Assert.Equal(2, changed);
        s.Unregister(t); Assert.Equal(3, changed);
    }

    [Fact]
    public void Unregister_Idempotent()
    {
        var s = new PendingScope();
        int changed = 0;
        s.Changed += () => Interlocked.Increment(ref changed);

        s.Unregister(new object()); // never registered
        Assert.Equal(0, changed);
    }

    [Fact]
    public void Multiple_Tokens_AnyLoading_True_If_Any_Loading()
    {
        var s = new PendingScope();
        var a = new object(); var b = new object();
        s.Register(a, true);
        s.Register(b, false);
        Assert.True(s.AnyLoading);
        s.SetLoading(a, false);
        Assert.False(s.AnyLoading);
    }

    // ════════════════════════════════════════════════════════════════
    //  UseResource integration: registers + updates on state transition
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task UseResource_With_Pending_Scope_Registers_Loading()
    {
        using var cache = new QueryCache();
        var scope = new PendingScope();
        var dispatcher = new InlineDispatcher();

        var ctx = new RenderContext();
        var contextScope = new ContextScope();
        contextScope.Push(new Dictionary<ContextBase, object?> { [AppContexts.PendingScope] = scope });
        ctx.BeginRender(() => { }, contextScope);

        var tcs = new TaskCompletionSource<int>();
        var v = ctx.UseResource(_ => tcs.Task, cache, Array.Empty<object>(), null, dispatcher);
        Assert.IsType<AsyncValue<int>.Loading>(v);
        Assert.True(scope.AnyLoading);
        Assert.Equal(1, scope.Count);

        tcs.SetResult(42);
        // The continuation runs via the InlineDispatcher.Post; allow any in-flight
        // Task.ContinueWith to settle before asserting.
        for (int i = 0; i < 10 && scope.AnyLoading; i++) await Task.Delay(5, TestContext.Current.CancellationToken);
        Assert.False(scope.AnyLoading);
    }

    [Fact]
    public void UseResource_Sync_Complete_Never_Registers_Loading()
    {
        using var cache = new QueryCache();
        var scope = new PendingScope();
        var dispatcher = new InlineDispatcher();

        var ctx = new RenderContext();
        var cs = new ContextScope();
        cs.Push(new Dictionary<ContextBase, object?> { [AppContexts.PendingScope] = scope });
        ctx.BeginRender(() => { }, cs);

        var v = ctx.UseResource(_ => Task.FromResult(7), cache, Array.Empty<object>(), null, dispatcher);
        Assert.Equal(new AsyncValue<int>.Data(7), v);
        // Register-as-Loading fires inline during construction, then NotifyPending flips
        // it to false on the Data assignment. AnyLoading is the caller-visible invariant.
        Assert.False(scope.AnyLoading);
    }

    [Fact]
    public void UseResource_Reloading_Does_Not_Count_As_Loading()
    {
        using var cache = new QueryCache();
        cache.Set("k", 5, TimeSpan.Zero, TimeSpan.FromMinutes(5));
        var scope = new PendingScope();
        var dispatcher = new InlineDispatcher();

        var ctx = new RenderContext();
        var cs = new ContextScope();
        cs.Push(new Dictionary<ContextBase, object?> { [AppContexts.PendingScope] = scope });
        ctx.BeginRender(() => { }, cs);

        var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var v = ctx.UseResource(_ => tcs.Task, cache, new object[] { "k" },
            new ResourceOptions(CacheKey: "k", StaleTime: TimeSpan.Zero), dispatcher);

        // Cache hit past StaleTime — surfaces Reloading, not Loading.
        Assert.IsType<AsyncValue<int>.Reloading>(v);
        Assert.False(scope.AnyLoading);
    }

    [Fact]
    public void UseResource_Unregisters_On_Unmount()
    {
        using var cache = new QueryCache();
        var scope = new PendingScope();
        var dispatcher = new InlineDispatcher();

        var ctx = new RenderContext();
        var cs = new ContextScope();
        cs.Push(new Dictionary<ContextBase, object?> { [AppContexts.PendingScope] = scope });
        ctx.BeginRender(() => { }, cs);

        var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        ctx.UseResource(_ => tcs.Task, cache, Array.Empty<object>(), null, dispatcher);
        ctx.FlushEffects();
        Assert.Equal(1, scope.Count);

        ctx.RunCleanups();
        Assert.Equal(0, scope.Count);
    }

    // ════════════════════════════════════════════════════════════════
    //  Two siblings — scope loading until both resolve
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Two_Siblings_AnyLoading_Until_Both_Resolve()
    {
        using var cache = new QueryCache();
        var scope = new PendingScope();
        var dispatcher = new InlineDispatcher();

        var csA = new ContextScope();
        csA.Push(new Dictionary<ContextBase, object?> { [AppContexts.PendingScope] = scope });
        var ctxA = new RenderContext();
        ctxA.BeginRender(() => { }, csA);

        var csB = new ContextScope();
        csB.Push(new Dictionary<ContextBase, object?> { [AppContexts.PendingScope] = scope });
        var ctxB = new RenderContext();
        ctxB.BeginRender(() => { }, csB);

        var gateA = new TaskCompletionSource<int>();
        var gateB = new TaskCompletionSource<int>();
        ctxA.UseResource(_ => gateA.Task, cache, new object[] { "a" }, null, dispatcher);
        ctxB.UseResource(_ => gateB.Task, cache, new object[] { "b" }, null, dispatcher);
        Assert.True(scope.AnyLoading);

        gateA.SetResult(1);
        // Even after A resolves, scope should still show loading because B hasn't.
        await Task.Delay(20, TestContext.Current.CancellationToken);
        Assert.True(scope.AnyLoading);

        gateB.SetResult(2);
        for (int i = 0; i < 10 && scope.AnyLoading; i++) await Task.Delay(5, TestContext.Current.CancellationToken);
        Assert.False(scope.AnyLoading);
    }

    // ════════════════════════════════════════════════════════════════
    //  No scope installed — zero overhead, AnyLoading untouched.
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public void UseResource_With_No_Scope_Does_Not_Explode()
    {
        using var cache = new QueryCache();
        var dispatcher = new InlineDispatcher();

        var ctx = new RenderContext();
        ctx.BeginRender(() => { });

        var v = ctx.UseResource(_ => Task.FromResult(7), cache, Array.Empty<object>(), null, dispatcher);
        Assert.Equal(new AsyncValue<int>.Data(7), v);
    }

    // ════════════════════════════════════════════════════════════════
    //  UseInfiniteResource integration — empty-items Loading registers,
    //  first page landing clears it.
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task UseInfiniteResource_Scope_Tracks_Initial_Load()
    {
        using var cache = new QueryCache();
        var scope = new PendingScope();
        var dispatcher = new InlineDispatcher();

        var ctx = new RenderContext();
        var cs = new ContextScope();
        cs.Push(new Dictionary<ContextBase, object?> { [AppContexts.PendingScope] = scope });
        ctx.BeginRender(() => { }, cs);

        var gate = new TaskCompletionSource<Page<int, string>>();
        var resource = ctx.UseInfiniteResource<int, string>(
            fetchPage: (_, _) => gate.Task,
            cache, new object[] { "k" }, null, dispatcher);

        Assert.True(scope.AnyLoading);

        gate.SetResult(new Page<int, string>(new[] { 1, 2, 3 }, NextCursor: null, TotalCount: 3));
        for (int i = 0; i < 10 && scope.AnyLoading; i++) await Task.Delay(5, TestContext.Current.CancellationToken);
        Assert.False(scope.AnyLoading);
    }

    [Fact]
    public void UseInfiniteResource_Unregisters_On_Unmount()
    {
        using var cache = new QueryCache();
        var scope = new PendingScope();
        var dispatcher = new InlineDispatcher();

        var ctx = new RenderContext();
        var cs = new ContextScope();
        cs.Push(new Dictionary<ContextBase, object?> { [AppContexts.PendingScope] = scope });
        ctx.BeginRender(() => { }, cs);

        var gate = new TaskCompletionSource<Page<int, string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        ctx.UseInfiniteResource<int, string>((_, _) => gate.Task, cache, new object[] { "k" }, null, dispatcher);
        ctx.FlushEffects();
        Assert.Equal(1, scope.Count);

        ctx.RunCleanups();
        Assert.Equal(0, scope.Count);
    }
}
