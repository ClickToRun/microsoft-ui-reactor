using System;
using System.Reflection;
using System.Runtime.ExceptionServices;
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
/// Both commit from a dedicated thread rather than the test's own. xUnit runs tests on the thread
/// pool, and a pool thread is exactly what an offload could be handed — committing from one would
/// leave "ran on the pool" saying nothing about whether the callback moved.
/// </summary>
public class DataGridCommitThreadingTests
{
    private const string MethodName = "HandleAsyncCommit";

    /// <summary>How long a test waits before declaring the arm under test broken.</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private record TestItem(int Id, string Name);

    /// <summary>
    /// A committing thread with a guarded body. An unhandled exception on a dedicated
    /// <see cref="Thread"/> tears down the process, so a run that should have reported one failing
    /// test would instead lose every test after it — and the commit here reaches a private method
    /// through reflection, which throws on any signature drift. Whatever the body throws is
    /// captured and replayed on the xUnit thread by <see cref="Rethrow"/>.
    /// </summary>
    private sealed class Committer
    {
        private readonly Thread _thread;
        private Exception? _failure;
        private bool _joined;

        internal Committer(string name, Action body)
        {
            _thread = new Thread(() =>
            {
                try
                {
                    body();
                }
                catch (Exception ex)
                {
                    _failure = ex;
                }
            })
            {
                IsBackground = true,
                Name = name,
            };
        }

        internal void Start() => _thread.Start();

        internal bool Join(TimeSpan timeout) => _joined = _thread.Join(timeout);

        /// <summary>
        /// Replays anything the committing thread threw, original stack intact. Silent unless
        /// <see cref="Join"/> has succeeded — that join is the happens-before edge which makes
        /// reading the captured exception safe, and a thread still running has not settled on a
        /// failure to report anyway. Nothing is swallowed by that: the caller asserts on the join
        /// result separately, so a thread that never finished is still a test failure. Call it
        /// before asserting on what the commit observed, because a commit that threw recorded
        /// nothing and those assertions would otherwise fail for the wrong reason.
        /// </summary>
        internal void Rethrow()
        {
            if (_joined && _failure is { } ex)
                ExceptionDispatchInfo.Capture(ex).Throw();
        }
    }

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
            // Volatile to pair with the read below. On the contracted path this runs on the
            // committing thread and the pairing is moot, but the regression this test exists to
            // catch is precisely the one where it does not — and a stale 0 there would blame the
            // wrong thread in the failure message.
            Volatile.Write(ref dispatcherThreadId, Environment.CurrentManagedThreadId);
            Interlocked.Increment(ref dispatcherCalls);
        };

        var key = (RowKey)1;
        var committerThreadId = 0;
        var callsOnReturn = 0;
        var threadIdOnReturn = 0;
        var committingOnReturn = false;

        var committer = new Committer("HandleAsyncCommit caller (dispatcher arm)", () =>
        {
            committerThreadId = Environment.CurrentManagedThreadId;
            InvokeHandleAsyncCommit(state, element, key, new TestItem(1, "Alice edited"), new TestItem(1, "Alice"));

            // Sampled the instant the call returns. An arm that scheduled the dispatch rather than
            // running it has either not run it yet (count 0) or run it somewhere else (id mismatch)
            // — which is what makes this decisive against any scheduling primitive, named or not.
            callsOnReturn = Volatile.Read(ref dispatcherCalls);
            threadIdOnReturn = Volatile.Read(ref dispatcherThreadId);
            committingOnReturn = state.IsCommitting(key);
        });

        committer.Start();
        var joined = committer.Join(Timeout);

        // Before the assertions below: a commit that threw sampled nothing, so they would all fail
        // on zeroed locals and bury the actual exception.
        committer.Rethrow();

        Assert.True(
            joined,
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

        // The committing thread below is an explicit Thread, so it is never a pool thread. That is
        // what makes "the callback ran on the pool" a proof that it left the committing thread —
        // no managed-id comparison, which would be the fragile way to ask the same question, since
        // ids are recycled once a thread ends and this committer ends as soon as the fallback
        // returns. Assert.False(committerOnPool) below keeps that premise honest.
        var callbackOnPool = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var (state, element) = NewGrid((_, _) =>
        {
            callbackOnPool.TrySetResult(Thread.CurrentThread.IsThreadPoolThread);
            return Task.CompletedTask;
        });

        Assert.Null(state.CommitDispatcher); // precondition: this is the fallback arm

        var committerOnPool = true;
        var committer = new Committer("HandleAsyncCommit caller (fallback arm)", () =>
        {
            committerOnPool = Thread.CurrentThread.IsThreadPoolThread;
            InvokeHandleAsyncCommit(state, element, (RowKey)1, new TestItem(1, "Alice edited"), new TestItem(1, "Alice"));
        });

        // One budget shared by the two waits below, so a hung regression costs Timeout once for the
        // test rather than once per wait.
        var startedAt = Environment.TickCount64;
        TimeSpan Remaining()
        {
            var left = Timeout - TimeSpan.FromMilliseconds(Environment.TickCount64 - startedAt);
            return left > TimeSpan.Zero ? left : TimeSpan.Zero;
        }

        committer.Start();

        // The callback never blocks, so this join is safe against every shape being tested: the
        // contracted arm offloads and returns, and a regression that ran the callback inline still
        // returns promptly and fails below on where it ran rather than hanging the run.
        var joined = committer.Join(Remaining());

        var entered = true;
        var onPool = false;

        try
        {
            onPool = await callbackOnPool.Task.WaitAsync(Remaining(), ct);
        }
        catch (TimeoutException)
        {
            // Recorded rather than asserted here so the rethrow below still gets its turn: a commit
            // that threw also never reached the callback, and that exception is the real story.
            entered = false;
        }

        committer.Rethrow();

        Assert.True(
            joined,
            $"{MethodName}'s caller thread was still running {Timeout.TotalSeconds:0}s after the " +
            "commit. The fallback offloads the callback and returns, so it has nothing to block on.");

        // Safe to read unsynchronised: the join above is the happens-before edge.
        Assert.False(
            committerOnPool,
            "The commit was issued from a thread-pool thread, so landing on the pool no longer " +
            "proves the callback left the committing thread. Committer must keep using a " +
            "dedicated Thread for the assertion below to mean anything.");

        Assert.True(
            entered,
            $"{MethodName}'s fallback arm never invoked OnRowChanged. It is offloaded to the " +
            "thread pool, so something has to run it.");

        Assert.True(
            onPool,
            $"{MethodName}'s fallback arm invoked OnRowChanged on the committing thread or on " +
            "another dedicated thread, not the thread pool. Task.Run is what puts it on a pool " +
            "thread, and the docs on that method describe it that way — update them together if " +
            "the target has changed.");
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
            // Unwrapped so callers see what the method under test threw, not the reflection
            // wrapper — but rethrown through ExceptionDispatchInfo, since a bare `throw ex.Inner`
            // would reset the stack to this helper and point at the wrong frame.
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
        }
    }
}
