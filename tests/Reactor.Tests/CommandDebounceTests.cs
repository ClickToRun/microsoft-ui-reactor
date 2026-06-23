using Microsoft.UI.Reactor.Core;
using Xunit;

namespace Microsoft.UI.Reactor.Tests;

/// <summary>
/// Tests for the leading-edge <see cref="Command.DebounceMs"/> debounce realized by
/// <see cref="RenderContext.UseCommand(Command)"/> (issue #136). Timing is driven by an
/// injected <see cref="FakeTimeProvider"/> so the window assertions aren't wall-clock-flaky.
/// </summary>
[Collection("UnobservedTaskException")]
public class CommandDebounceTests
{
    private static RenderContext CreateContext(FakeTimeProvider time)
    {
        var ctx = new RenderContext { TimeProvider = time };
        ctx.BeginRender(() => { });
        return ctx;
    }

    private static void Rerender(RenderContext ctx)
    {
        ctx.BeginRender(() => { });
    }

    // ════════════════════════════════════════════════════════════════
    //  (a) second invoke within the window is dropped
    //  (b) an invoke after the window elapses is accepted
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public void Second_Invoke_Within_Window_Is_Dropped_Then_Accepted_After()
    {
        var time = new FakeTimeProvider();
        var ctx = CreateContext(time);
        int fires = 0;
        var cmd = new Command { Label = "Run", Execute = () => fires++, DebounceMs = 1500 };

        var result = ctx.UseCommand(cmd);

        result.Execute!();                 // accepted
        Assert.Equal(1, fires);

        result.Execute!();                 // within window → dropped
        time.Advance(TimeSpan.FromMilliseconds(500));
        result.Execute!();                 // still within window → dropped
        Assert.Equal(1, fires);

        time.Advance(TimeSpan.FromMilliseconds(1000)); // window (1500ms) elapses → timer clears it
        result.Execute!();                 // accepted again
        Assert.Equal(2, fires);
    }

    // ════════════════════════════════════════════════════════════════
    //  (c) IsEnabled is false during the window and true after
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public void IsEnabled_Is_False_During_Window_And_True_After()
    {
        var time = new FakeTimeProvider();
        var ctx = CreateContext(time);
        var cmd = new Command { Label = "Run", Execute = () => { }, DebounceMs = 1000 };

        var result = ctx.UseCommand(cmd);
        Assert.True(result.IsEnabled);
        Assert.False(result.IsDebouncing);

        result.Execute!();

        // Re-render to observe the debouncing state flowing through.
        Rerender(ctx);
        var during = ctx.UseCommand(cmd);
        Assert.True(during.IsDebouncing);
        Assert.False(during.IsEnabled);

        // Not yet elapsed — still disabled.
        time.Advance(TimeSpan.FromMilliseconds(999));
        Rerender(ctx);
        var stillDuring = ctx.UseCommand(cmd);
        Assert.False(stillDuring.IsEnabled);

        // Window elapses → timer fires → re-enabled.
        time.Advance(TimeSpan.FromMilliseconds(1));
        Rerender(ctx);
        var after = ctx.UseCommand(cmd);
        Assert.False(after.IsDebouncing);
        Assert.True(after.IsEnabled);
    }

    // ════════════════════════════════════════════════════════════════
    //  (d) async commands keep DebounceMs extending the window past lambda return
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Async_Command_DebounceMs_Extends_Window_Past_Lambda_Return()
    {
        var time = new FakeTimeProvider();
        var stateChanged = new SemaphoreSlim(0);
        var ctx = new RenderContext { TimeProvider = time };
        ctx.BeginRender(() => stateChanged.Release());

        var cmd = new Command
        {
            Label = "Re-gen",
            ExecuteAsync = () => Task.CompletedTask, // returns immediately
            DebounceMs = 250,
        };

        var result = ctx.UseCommand(cmd);
        result.Execute!();

        // The synchronous part sets IsDebouncing=true (1st release) and IsExecuting=true (2nd),
        // and the immediately-completing task resets IsExecuting=false (3rd release).
        for (int i = 0; i < 3; i++)
            await stateChanged.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // Lambda already returned (IsExecuting back to false) but the debounce window holds
        // the command disabled.
        ctx.BeginRender(() => stateChanged.Release());
        var during = ctx.UseCommand(cmd);
        Assert.False(during.IsExecuting);
        Assert.True(during.IsDebouncing);
        Assert.False(during.IsEnabled);

        // Elapse the debounce window → re-enabled.
        time.Advance(TimeSpan.FromMilliseconds(250));
        await stateChanged.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Rerender(ctx);
        var after = ctx.UseCommand(cmd);
        Assert.False(after.IsDebouncing);
        Assert.True(after.IsEnabled);
    }

    // ════════════════════════════════════════════════════════════════
    //  (e) DebounceMs = 0 preserves today's behavior exactly
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public void DebounceMs_Zero_Sync_Command_Passes_Through_Unchanged()
    {
        var time = new FakeTimeProvider();
        var ctx = CreateContext(time);
        var original = new Command { Label = "Cut", Execute = () => { } }; // DebounceMs defaults to 0

        var result = ctx.UseCommand(original);

        Assert.Same(original, result);
    }

    [Fact]
    public void DebounceMs_Zero_Sync_Command_Never_Disables()
    {
        var time = new FakeTimeProvider();
        var ctx = CreateContext(time);
        int fires = 0;
        var cmd = new Command { Label = "Cut", Execute = () => fires++, DebounceMs = 0 };

        var result = ctx.UseCommand(cmd);

        result.Execute!();
        result.Execute!();
        result.Execute!();
        Assert.Equal(3, fires);          // no fire is dropped
        Assert.True(result.IsEnabled);   // never disables
    }

    [Fact]
    public void Default_DebounceMs_Is_Zero()
    {
        var cmd = new Command { Label = "x", Execute = () => { } };
        Assert.Equal(0, cmd.DebounceMs);
        Assert.False(cmd.IsDebouncing);
    }

    // ════════════════════════════════════════════════════════════════
    //  Parameterized command debounce
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public void Parameterized_Sync_Command_Debounces()
    {
        var time = new FakeTimeProvider();
        var ctx = CreateContext(time);
        var args = new List<string>();
        var cmd = new Command<string> { Label = "Delete", Execute = args.Add, DebounceMs = 500 };

        var result = ctx.UseCommand(cmd);

        result.Execute!("a");            // accepted
        result.Execute!("b");            // dropped (within window)
        Assert.Equal(new[] { "a" }, args);

        time.Advance(TimeSpan.FromMilliseconds(500));
        result.Execute!("c");            // accepted
        Assert.Equal(new[] { "a", "c" }, args);
    }
}
