using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Reactor.Controls;
using Microsoft.UI.Reactor.Data;
using Microsoft.UI.Reactor.Data.Providers;
using Xunit;

namespace Microsoft.UI.Reactor.Tests;

/// <summary>
/// Pins the threading contract of <c>DataGridComponent&lt;T&gt;.HandleAsyncCommit</c>'s two commit
/// arms, which are opposite: the dispatcher arm calls its delegate synchronously on the committing
/// thread, and the no-dispatcher fallback offloads <c>OnRowChanged</c> to the thread pool with
/// <c>Task.Run</c>.
///
/// Issue #958 was a doc that described the fallback as running the callback "inline" while its own
/// body comment said the opposite. Nothing tested either arm, so there was nothing to consult and
/// no way to tell which half was lying. These two tests are that reference.
///
/// Both commit from a dedicated thread rather than the test's own: xUnit runs tests on the thread
/// pool, so a pool thread is exactly what an offload could be handed, which would make the two ids
/// match on a busy machine even though nothing ran on the committing thread.
/// </summary>
public class DataGridCommitThreadingTests
{
    private const string MethodName = "HandleAsyncCommit";

    /// <summary>How long a test waits before declaring the arm under test broken.</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private record TestItem(int Id, string Name);

    /// <summary>
    /// With a <c>CommitDispatcher</c> installed, the delegate is invoked synchronously on the
    /// committing thread and the fallback does not also run.
    /// </summary>
    [Fact]
    public void Dispatcher_Arm_Invokes_The_Dispatcher_On_The_Calling_Thread()
    {
        var rowChangedCalls = 0;
        var (state, element) = NewGrid((_, _) =>
        {
            Interlocked.Increment(ref rowChangedCalls);
            return Task.CompletedTask;
        });

        var dispatcherCalls = 0;
        var dispatcherThreadId = 0;
        state.CommitDispatcher = (_, _, _) =>
        {
            dispatcherThreadId = Environment.CurrentManagedThreadId;
            Interlocked.Increment(ref dispatcherCalls);
        };

        var key = (RowKey)1;
        var committerThreadId = 0;
        var callsOnReturn = 0;
        var threadIdOnReturn = 0;
        var committingOnReturn = false;

        var committer = new Thread(() =>
        {
            committerThreadId = Environment.CurrentManagedThreadId;
            InvokeHandleAsyncCommit(state, element, key, new TestItem(1, "Alice edited"), new TestItem(1, "Alice"));

            // Sampled the instant the call returns. An arm that scheduled the dispatch rather than
            // running it has either not run it yet (count 0) or run it somewhere else (id mismatch)
            // — which is what makes this decisive against any scheduling primitive, named or not.
            callsOnReturn = Volatile.Read(ref dispatcherCalls);
            threadIdOnReturn = Volatile.Read(ref dispatcherThreadId);
            committingOnReturn = state.IsCommitting(key);
        })
        {
            IsBackground = true,
            Name = "HandleAsyncCommit caller (dispatcher arm)",
        };

        committer.Start();
        Assert.True(
            committer.Join(Timeout),
            $"{MethodName} never returned on the dispatcher path. It is a straight call through to " +
            "the dispatcher, which cannot block.");

        Assert.True(
            callsOnReturn == 1,
            $"{MethodName} had invoked the dispatcher {callsOnReturn} time(s) by the time it " +
            "returned, expected exactly 1. This arm calls straight through, so the dispatcher must " +
            "have run before the call returned.");

        Assert.True(
            threadIdOnReturn == committerThreadId,
            $"{MethodName}'s dispatcher arm ran the dispatcher on thread {threadIdOnReturn}, not " +
            $"the committing thread {committerThreadId}. Scheduling the dispatch breaks the " +
            "calling-thread contract this arm documents.");

        // BeginAsyncCommit is the fallback's first synchronous act, so a still-empty committing
        // set proves the dispatcher arm returned rather than running both arms.
        Assert.False(
            committingOnReturn,
            $"{MethodName} started the fallback's commit lifecycle even though a dispatcher was " +
            "installed. The two arms are meant to be exclusive — the dispatcher arm returns.");

        Assert.Equal(0, Volatile.Read(ref rowChangedCalls));
    }

    /// <summary>
    /// With no <c>CommitDispatcher</c> installed, <c>OnRowChanged</c> lands on a thread-pool thread
    /// other than the one that committed. This is the half issue #958's doc got backwards.
    /// </summary>
    [Fact]
    public async Task Fallback_Invokes_OnRowChanged_Off_The_Calling_Thread()
    {
        var ct = TestContext.Current.CancellationToken;

        var callbackThreadId = 0;
        var onPoolThread = false;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var (state, element) = NewGrid(async (_, _) =>
        {
            // Published before entered fires; the await below is the happens-before edge.
            callbackThreadId = Environment.CurrentManagedThreadId;
            onPoolThread = Thread.CurrentThread.IsThreadPoolThread;
            entered.TrySetResult();
            await release.Task;
        });

        Assert.Null(state.CommitDispatcher); // precondition: this is the fallback arm

        // Held until the assertions have run, so the committing thread stays alive and its managed
        // id cannot be recycled onto the pool thread we are about to compare it against. Disposed
        // by hand rather than with `using`: the committer blocks on this handle, and
        // ManualResetEventSlim.Wait throws ObjectDisposedException even when already signalled —
        // on a thread we own that would go unhandled and take the run down. So it is disposed only
        // once the join below confirms nobody is left inside it.
        var assertionsDone = new ManualResetEventSlim(false);
        var committer = new Thread(() =>
        {
            InvokeHandleAsyncCommit(state, element, (RowKey)1, new TestItem(1, "Alice edited"), new TestItem(1, "Alice"));
            assertionsDone.Wait(Timeout);
        })
        {
            IsBackground = true,
            Name = "HandleAsyncCommit caller (fallback arm)",
        };

        // Watchdog: a regression that ran the callback to completion on the committing thread would
        // block that thread until this fires, so the test fails on thread identity rather than
        // hanging. Registered before the commit, and torn down with the test either way.
        using var watchdog = new CancellationTokenSource(Timeout);
        using var unblock = watchdog.Token.UnsafeRegister(_ => release.TrySetResult(), null);

        committer.Start();

        var joined = false;

        try
        {
            await entered.Task.WaitAsync(Timeout, ct);

            Assert.NotEqual(committer.ManagedThreadId, callbackThreadId);
            Assert.True(
                onPoolThread,
                $"{MethodName}'s fallback arm left the committing thread but did not land on the " +
                "thread pool. Task.Run is what puts OnRowChanged on a thread-pool thread; the " +
                "docs on this method describe that, so update them if the target has changed.");
        }
        catch (TimeoutException)
        {
            Assert.Fail(
                $"{MethodName}'s fallback arm never invoked OnRowChanged. It is offloaded to the " +
                "thread pool, so something has to run it.");
        }
        finally
        {
            release.TrySetResult();
            assertionsDone.Set();
            joined = committer.Join(Timeout);

            if (joined)
                assertionsDone.Dispose();
        }

        Assert.True(
            joined,
            $"{MethodName}'s caller thread was still running {Timeout.TotalSeconds:0}s after the " +
            "fallback was released. The fallback offloads the callback and returns, so the " +
            "committing thread has nothing left to block on.");
    }

    /// <summary>
    /// A two-row grid state plus the element carrying <paramref name="onRowChanged"/>. No
    /// <c>CommitDispatcher</c> is installed — that is the fallback arm's precondition, and the
    /// dispatcher test sets one itself.
    /// </summary>
    private static (DataGridState<TestItem> State, DataGridElement<TestItem> Element) NewGrid(
        Func<RowKey, TestItem, Task> onRowChanged)
    {
        var source = new ListDataSource<TestItem>(
            [new TestItem(1, "Alice"), new TestItem(2, "Bob")], i => (RowKey)i.Id);
        var columns = new FieldDescriptor[]
        {
            new() { Name = "Id", FieldType = typeof(int), GetValue = o => ((TestItem)o).Id },
            new() { Name = "Name", FieldType = typeof(string), GetValue = o => ((TestItem)o).Name },
        };

        var state = new DataGridState<TestItem>(source, columns, SelectionMode.None);
        var element = new DataGridElement<TestItem>
        {
            Source = source,
            Columns = columns,
            OnRowChanged = onRowChanged,
        };

        return (state, element);
    }

    private static void InvokeHandleAsyncCommit(
        DataGridState<TestItem> state,
        DataGridElement<TestItem> element,
        RowKey key,
        TestItem newItem,
        TestItem originalItem)
    {
        var method = typeof(DataGridComponent<TestItem>)
            .GetMethod(MethodName, BindingFlags.NonPublic | BindingFlags.Static);

        Assert.True(method is not null, $"{MethodName} is no longer a private static method of DataGridComponent<T>.");

        try
        {
            method!.Invoke(null, [state, element, key, newItem, originalItem]);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            throw ex.InnerException;
        }
    }
}
