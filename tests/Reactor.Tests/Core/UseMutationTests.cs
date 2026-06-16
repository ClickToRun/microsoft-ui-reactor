using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Hooks;
using Xunit;

namespace Microsoft.UI.Reactor.Tests.Core;

/// <summary>
/// Unit tests driving <c>UseMutation</c> through <see cref="RenderContext"/> directly.
/// Deterministic completion via <see cref="TaskCompletionSource{T}"/>; dispatcher is the
/// inline stub used throughout the hook test suite.
/// </summary>
public class UseMutationTests
{
    // ════════════════════════════════════════════════════════════════
    //  Test harness
    // ════════════════════════════════════════════════════════════════

    private sealed class InlineDispatcher : IHookDispatcher
    {
        public int PostCount;
        public void Post(Action action) { PostCount++; action(); }
    }

    private static (RenderContext Ctx, QueryCache Cache, InlineDispatcher Dispatcher) NewHarness()
    {
        var cache = new QueryCache();
        var t = DateTime.UtcNow;
        cache.UtcNow = () => t;
        var d = new InlineDispatcher();
        var ctx = new RenderContext();
        ctx.BeginRender(() => { });
        return (ctx, cache, d);
    }

    private static Mutation<TIn, TOut> Render<TIn, TOut>(
        RenderContext ctx,
        QueryCache cache,
        InlineDispatcher dispatcher,
        Func<TIn, CancellationToken, Task<TOut>> mutator,
        MutationOptions<TIn, TOut>? options = null)
    {
        var m = ctx.UseMutation(mutator, cache, options, dispatcher);
        ctx.FlushEffects();
        return m;
    }

    // ════════════════════════════════════════════════════════════════
    //  Happy path — IsPending / LastResult / callbacks
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task IsPending_True_During_Fetch_False_After()
    {
        var (ctx, cache, d) = NewHarness();
        var gate = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var m = Render<int, int>(ctx, cache, d, (x, _) => gate.Task);

        var run = m.RunAsync(7);
        Assert.True(m.IsPending);

        gate.SetResult(42);
        await run;
        Assert.False(m.IsPending);
        Assert.Equal(42, m.LastResult);
        Assert.Null(m.Error);
    }

    [Fact]
    public async Task OnSuccess_Fires_With_Result_And_Input()
    {
        var (ctx, cache, d) = NewHarness();
        int? seenResult = null; string? seenInput = null;
        var m = Render<string, int>(ctx, cache, d,
            (x, _) => Task.FromResult(x.Length),
            new MutationOptions<string, int>(OnSuccess: (r, i) => { seenResult = r; seenInput = i; }));

        var result = await m.RunAsync("hello");
        Assert.Equal(5, result);
        Assert.Equal(5, seenResult);
        Assert.Equal("hello", seenInput);
    }

    [Fact]
    public void OnOptimistic_Fires_Synchronously_Before_Mutator_Starts()
    {
        var (ctx, cache, d) = NewHarness();
        var order = new List<string>();
        var gate = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var m = Render<int, int>(ctx, cache, d,
            (x, _) => { order.Add("mutator"); return gate.Task; },
            new MutationOptions<int, int>(OnOptimistic: _ => order.Add("optimistic")));

        _ = m.RunAsync(1);
        Assert.Equal(new[] { "optimistic", "mutator" }, order);
    }

    [Fact]
    public async Task Optimistic_Before_OnSuccess_Before_IsPending_False()
    {
        var (ctx, cache, d) = NewHarness();
        var order = new List<string>();
        bool pendingAtSuccess = false;
        var m = Render<int, int>(ctx, cache, d,
            (x, _) => Task.FromResult(x * 2),
            new MutationOptions<int, int>(
                OnOptimistic: _ => order.Add("optimistic"),
                OnSuccess: (r, _) => { order.Add("success"); pendingAtSuccess = r > 0 && order.Count == 2; }));

        await m.RunAsync(3);
        Assert.Equal(new[] { "optimistic", "success" }, order);
        Assert.True(pendingAtSuccess, "OnSuccess should observe its own ordering after Optimistic.");
        Assert.False(m.IsPending);
    }

    // ════════════════════════════════════════════════════════════════
    //  Error path
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task OnError_Fires_Error_Populated_LastResult_Unchanged()
    {
        var (ctx, cache, d) = NewHarness();
        Exception? seen = null;
        var m = Render<int, int>(ctx, cache, d,
            (x, _) => Task.FromResult(x),
            new MutationOptions<int, int>(OnError: (ex, _) => seen = ex));

        // First, a success to populate LastResult.
        await m.RunAsync(1);
        Assert.Equal(1, m.LastResult);

        // Re-render with a failing mutator (closure rotates per render).
        ctx.BeginRender(() => { });
        m = ctx.UseMutation<int, int>((x, _) => Task.FromException<int>(new InvalidOperationException("boom")), cache,
            new MutationOptions<int, int>(OnError: (ex, _) => seen = ex), d);
        ctx.FlushEffects();

        await Assert.ThrowsAsync<InvalidOperationException>(() => m.RunAsync(2));
        Assert.IsType<InvalidOperationException>(seen);
        Assert.IsType<InvalidOperationException>(m.Error);
        Assert.Equal(1, m.LastResult); // unchanged from the prior success
    }

    [Fact]
    public async Task Mutator_Throws_Synchronously_Surfaces_As_Faulted_Task()
    {
        var (ctx, cache, d) = NewHarness();
        var m = Render<int, int>(ctx, cache, d,
            (_, _) => throw new InvalidOperationException("sync"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => m.RunAsync(1));
        Assert.NotNull(m.Error);
        Assert.False(m.IsPending);
    }

    // ════════════════════════════════════════════════════════════════
    //  InvalidateKeys
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Success_Invalidates_Each_Key()
    {
        var (ctx, cache, d) = NewHarness();
        cache.Set("employees.list", 1, TimeSpan.Zero, TimeSpan.FromMinutes(5));
        cache.Set("teams.list", 2, TimeSpan.Zero, TimeSpan.FromMinutes(5));
        cache.Set("unrelated", 3, TimeSpan.Zero, TimeSpan.FromMinutes(5));

        var m = Render<int, int>(ctx, cache, d,
            (x, _) => Task.FromResult(x),
            new MutationOptions<int, int>(InvalidateKeys: new[] { "employees.list", "teams.list" }));

        await m.RunAsync(99);

        Assert.False(cache.TryGet<int>("employees.list", out _));
        Assert.False(cache.TryGet<int>("teams.list", out _));
        Assert.True(cache.TryGet<int>("unrelated", out _));
    }

    [Fact]
    public async Task Error_Does_Not_Invalidate()
    {
        var (ctx, cache, d) = NewHarness();
        cache.Set("employees.list", 1, TimeSpan.Zero, TimeSpan.FromMinutes(5));

        var m = Render<int, int>(ctx, cache, d,
            (_, _) => Task.FromException<int>(new InvalidOperationException("x")),
            new MutationOptions<int, int>(InvalidateKeys: new[] { "employees.list" }));

        await Assert.ThrowsAsync<InvalidOperationException>(() => m.RunAsync(1));
        Assert.True(cache.TryGet<int>("employees.list", out _));
    }

    // ════════════════════════════════════════════════════════════════
    //  Reset()
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Reset_Clears_Error_And_LastResult()
    {
        var (ctx, cache, d) = NewHarness();
        var m = Render<int, string>(ctx, cache, d, (x, _) => Task.FromResult($"v={x}"));
        await m.RunAsync(5);
        Assert.Equal("v=5", m.LastResult);

        m.Reset();
        Assert.Null(m.LastResult);
        Assert.Null(m.Error);
    }

    [Fact]
    public void Reset_Does_Not_Cancel_InFlight()
    {
        var (ctx, cache, d) = NewHarness();
        var gate = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken seenToken = default;
        var m = Render<int, int>(ctx, cache, d,
            (x, ct) => { seenToken = ct; return gate.Task; });

        _ = m.RunAsync(1);
        m.Reset();
        Assert.False(seenToken.IsCancellationRequested);
    }

    // ════════════════════════════════════════════════════════════════
    //  Optimistic failure — abort, do not invoke mutator
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Optimistic_Throws_Aborts_Mutator_Never_Runs()
    {
        var (ctx, cache, d) = NewHarness();
        bool mutatorRan = false;
        var m = Render<int, int>(ctx, cache, d,
            (x, _) => { mutatorRan = true; return Task.FromResult(x); },
            new MutationOptions<int, int>(OnOptimistic: _ => throw new InvalidOperationException("optimistic")));

        await Assert.ThrowsAsync<InvalidOperationException>(() => m.RunAsync(1));
        Assert.False(mutatorRan);
        Assert.False(m.IsPending);
    }

    // ════════════════════════════════════════════════════════════════
    //  Unmount
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Unmount_During_Pending_Token_Cancelled_Callbacks_Silent()
    {
        var (ctx, cache, d) = NewHarness();
        var gate = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken seen = default;
        int onSuccess = 0, onError = 0;
        var m = Render<int, int>(ctx, cache, d,
            (x, ct) => { seen = ct; return gate.Task.ContinueWith(_ => { ct.ThrowIfCancellationRequested(); return 1; }, ct); },
            new MutationOptions<int, int>(
                OnSuccess: (_, _) => Interlocked.Increment(ref onSuccess),
                OnError: (_, _) => Interlocked.Increment(ref onError)));

        var run = m.RunAsync(1);

        // Unmount.
        ctx.RunCleanups();
        Assert.True(seen.IsCancellationRequested);

        gate.SetResult(99);
        try { await run; } catch { }

        await Task.Delay(20, TestContext.Current.CancellationToken);
        Assert.Equal(0, onSuccess);
        Assert.Equal(0, onError);
    }

    [Fact]
    public void RunAsync_After_Unmount_Returns_Cancelled_Task()
    {
        var (ctx, cache, d) = NewHarness();
        var m = Render<int, int>(ctx, cache, d, (x, _) => Task.FromResult(x));

        ctx.RunCleanups();
        var t = m.RunAsync(1);
        Assert.True(t.IsCanceled);
    }

    // ════════════════════════════════════════════════════════════════
    //  Ambient-cache overload
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Ambient_Cache_Overload_Invalidates_AppContexts_Default()
    {
        var defaultCache = AppContexts.QueryCache.DefaultValue;
        var key = $"mutation-ambient/{Guid.NewGuid():N}";
        defaultCache.Set(key, 1, TimeSpan.Zero, TimeSpan.FromMinutes(5));

        var ctx = new RenderContext();
        ctx.BeginRender(() => { });
        var m = ctx.UseMutation<int, int>((x, _) => Task.FromResult(x),
            new MutationOptions<int, int>(InvalidateKeys: new[] { key }),
            new InlineDispatcher());
        ctx.FlushEffects();

        await m.RunAsync(99);
        Assert.False(defaultCache.TryGet<int>(key, out _));
    }
}
