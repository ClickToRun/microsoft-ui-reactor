using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.UI.Reactor.Cli.Pack;
using Microsoft.UI.Reactor.Controls;
using Microsoft.UI.Reactor.Data;
using Microsoft.UI.Reactor.Data.Providers;
using Xunit;

namespace Microsoft.UI.Reactor.Tests;

/// <summary>
/// Gates <c>DataGridComponent&lt;T&gt;.HandleAsyncCommit</c>'s prose against the code it describes.
///
/// The method has two commit arms with opposite threading semantics: the dispatcher arm invokes
/// <c>OnRowChanged</c> on the calling thread (<c>Mutation.RunAsync</c> runs the mutator
/// synchronously), while the no-dispatcher fallback offloads it to the thread pool with
/// <c>Task.Run</c>. Issue #958: the XML doc described the fallback as running the callback
/// "inline", ~19 lines above a body comment that said the opposite — and the wrong half was the
/// one every call site shows on hover. Nothing gated the two against each other, so the
/// contradiction survived.
///
/// Four of these tests are structural — they Roslyn-parse the source text, so no
/// <c>Microsoft.UI.Xaml</c> object is constructed and they run headless. The other two drive the
/// two arms for real and check which thread the work lands on: a structural guard can only reject
/// the scheduling primitives it knows the names of, so the behavioural pair is what makes the
/// threading claims hold against a primitive nobody listed.
/// </summary>
public class DataGridCommitThreadingDocTests
{
    private const string MethodName = "HandleAsyncCommit";

    /// <summary>
    /// Banned outright in this method's doc. It is the exact word that produced #958 and it is
    /// ambiguous here — a reader cannot tell whether it means "synchronously on the caller's
    /// thread" (true of the dispatcher arm) or "without offloading" (false of the fallback).
    /// </summary>
    private const string BannedWord = "inline";

    /// <summary>
    /// The <c>&lt;para&gt;</c> labels that bind each threading contract to the arm it describes.
    /// Whole-doc word matching is not enough on its own: a doc that used every right phrase but
    /// attached them to the wrong arms would state the exact contradiction #958 was about and
    /// still pass.
    /// </summary>
    private const string DispatcherLabel = "Dispatcher path";

    /// <inheritdoc cref="DispatcherLabel"/>
    private const string FallbackLabel = "Fallback path";

    /// <summary>
    /// The dispatcher arm's contract, stated positively. The fallback paragraph must never make
    /// this claim — it is the half of the doc that was backwards.
    /// </summary>
    private const string CallingThreadClaim = "invoked on the calling thread";

    private static readonly string[] s_offloadWords = ["Task.Run", "thread pool", "thread-pool"];

    /// <summary>How long the behavioural test waits before declaring the fallback arm broken.</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    // Parsed once — the [Fact]s share it, and a failed lookup then reports identically in each
    // instead of racing. Mirrors the memoization in FlyoutPlacementGuardTests.
    private static readonly Lazy<MethodDeclarationSyntax> s_method = new(FindHandleAsyncCommit);

    private static MethodDeclarationSyntax Method => s_method.Value;

    // ════════════════════════════════════════════════════════════════
    //  Code side — what the two arms actually do
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public void Fallback_Arm_Offloads_OnRowChanged_To_The_Thread_Pool()
    {
        var fallback = FallbackArmStatements();
        var taskRuns = Descendants(fallback).OfType<InvocationExpressionSyntax>().Where(IsTaskRun).ToList();

        Assert.True(
            taskRuns.Count > 0,
            $"{MethodName}'s no-dispatcher fallback no longer contains a Task.Run. If the fallback " +
            "now runs on the calling thread, the <remarks> above it (which promises a thread-pool " +
            "thread) must change with it — see issue #958.");

        var callbackInvocations = Descendants(fallback)
            .OfType<InvocationExpressionSyntax>()
            .Where(IsOnRowChangedInvocation)
            .ToList();

        Assert.True(
            callbackInvocations.Count > 0,
            $"{MethodName}'s fallback arm no longer invokes OnRowChanged — this guard can no longer " +
            "see what it is meant to be guarding, so it needs rewriting alongside that change.");

        // The offload only means anything if the callback is what got offloaded.
        Assert.All(callbackInvocations, call => Assert.True(
            taskRuns.Any(run => run.Span.Contains(call.Span)),
            $"An OnRowChanged invocation in {MethodName}'s fallback arm sits outside Task.Run, so " +
            "the callback can run on the calling thread after all. Update the <remarks> to match."));
    }

    /// <summary>
    /// Rejects the scheduling primitives by name. Necessarily incomplete — the list only covers
    /// what someone thought to add — so
    /// <see cref="Dispatcher_Arm_Invokes_The_Dispatcher_On_The_Calling_Thread"/> backs it with an
    /// observation of the actual thread, which no unlisted primitive can slip past.
    /// </summary>
    [Fact]
    public void Dispatcher_Arm_Does_Not_Offload()
    {
        var arm = DispatcherBranch().Statement;
        var offloads = arm.DescendantNodesAndSelf()
            .OfType<InvocationExpressionSyntax>()
            .Where(i => IsTaskRun(i) || IsNamedInvocation(
                i, "StartNew", "ContinueWith", "TryEnqueue", "Post", "QueueUserWorkItem", "UnsafeQueueUserWorkItem"))
            .Select(i => i.ToString())
            .ToList();

        Assert.True(
            offloads.Count == 0,
            $"{MethodName}'s dispatcher arm now schedules work instead of calling straight through: " +
            string.Join("; ", offloads) +
            ". The <remarks> promise OnRowChanged is invoked on the calling thread on this path.");

        Assert.Empty(arm.DescendantNodesAndSelf().OfType<AwaitExpressionSyntax>());

        // "Calls straight through" is only worth asserting if the dispatcher is what is called.
        // Without this, swapping dispatch(...) for any other synchronous call would keep the arm
        // offload-free while silently deleting the path the doc describes.
        var dispatcherName = DispatcherLocalName();
        Assert.Contains(
            arm.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>(),
            i => i.Expression.ToString() is var target
              && (target == dispatcherName || target.Contains("CommitDispatcher", StringComparison.Ordinal)));
    }

    // ════════════════════════════════════════════════════════════════
    //  Prose side — what the doc claims about them
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public void Doc_Never_Describes_The_Commit_As_Inline()
    {
        var doc = DocText();
        if (doc.Contains(BannedWord, StringComparison.OrdinalIgnoreCase))
        {
            Assert.Fail(
                $"{MethodName}'s doc uses the word \"{BannedWord}\" (issue #958). It is ambiguous " +
                "here — it reads as \"on the caller's thread\" to some and \"not offloaded\" to " +
                "others, and the two arms differ on exactly that. Say \"invoked on the calling " +
                "thread\" for the dispatcher arm and \"offloaded to the thread pool\" for the " +
                "fallback instead.");
        }
    }

    [Fact]
    public void Doc_Binds_Each_Threading_Contract_To_Its_Own_Arm()
    {
        var dispatcher = ParagraphLabelled(DispatcherLabel);
        var fallback = ParagraphLabelled(FallbackLabel);

        AssertStates(dispatcher, DispatcherLabel, ["CommitDispatcher"],
            "which piece of state selects this arm");
        AssertStates(dispatcher, DispatcherLabel, [CallingThreadClaim],
            "that OnRowChanged runs on the thread that committed the edit");

        foreach (var word in s_offloadWords)
        {
            Assert.False(
                dispatcher.Contains(word, StringComparison.OrdinalIgnoreCase),
                $"The \"{DispatcherLabel}\" paragraph of {MethodName}'s doc mentions \"{word}\". That " +
                "arm calls the dispatcher straight through; it is the fallback that offloads. " +
                $"Pinning the offload on the wrong arm is the inversion #958 fixed.\n\n{dispatcher}");
        }

        AssertStates(fallback, FallbackLabel, ["Task.Run"],
            "that the callback is handed to the thread pool rather than run here");
        AssertStates(fallback, FallbackLabel, ["thread-pool thread", "thread pool thread"],
            "where the callback actually ends up running");

        Assert.False(
            fallback.Contains(CallingThreadClaim, StringComparison.OrdinalIgnoreCase),
            $"The \"{FallbackLabel}\" paragraph of {MethodName}'s doc claims OnRowChanged is " +
            $"\"{CallingThreadClaim}\". It is not — Task.Run puts it on a thread-pool thread. That " +
            $"is the #958 defect verbatim.\n\n{fallback}");
    }

    // ════════════════════════════════════════════════════════════════
    //  Behaviour — the two arms really do land on different threads
    // ════════════════════════════════════════════════════════════════

    private record TestItem(int Id, string Name);

    /// <summary>
    /// Drives the real dispatcher arm — a <c>CommitDispatcher</c> installed — and checks the
    /// dispatcher is called synchronously, on the thread that committed.
    /// <see cref="Dispatcher_Arm_Does_Not_Offload"/> can only reject offload primitives it names,
    /// so it stays green for any scheduler left off that list. This one observes the thread
    /// instead of the syntax, so nothing gets past it by being unlisted.
    ///
    /// Committed from a dedicated thread for the reason spelled out on
    /// <see cref="Fallback_Invokes_OnRowChanged_Off_The_Calling_Thread"/>: xUnit runs tests on
    /// pool threads, and a pool thread is exactly what an offload could be handed.
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
            // running it has either not run it yet (count 0) or run it somewhere else (id mismatch).
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
            $"{MethodName} never returned on the dispatcher path. The <remarks> describe it as a " +
            "straight call through to the dispatcher, which cannot block.");

        Assert.True(
            callsOnReturn == 1,
            $"{MethodName} had invoked the dispatcher {callsOnReturn} time(s) by the time it " +
            "returned, expected exactly 1. The <remarks> promise this arm calls straight through, " +
            "so the dispatcher must have run before the call returned.");

        Assert.True(
            threadIdOnReturn == committerThreadId,
            $"{MethodName}'s dispatcher arm ran the dispatcher on thread {threadIdOnReturn}, not " +
            $"the committing thread {committerThreadId}. The <remarks> say OnRowChanged is " +
            "invoked on the calling thread on this path — scheduling the dispatch breaks that.");

        // BeginAsyncCommit is the fallback's first synchronous act, so a still-empty committing
        // set proves the dispatcher arm returned rather than running both arms.
        Assert.False(
            committingOnReturn,
            $"{MethodName} started the fallback's commit lifecycle even though a dispatcher was " +
            "installed. The two arms are meant to be exclusive — the dispatcher arm returns.");

        Assert.Equal(0, Volatile.Read(ref rowChangedCalls));
    }

    /// <summary>
    /// Drives the real fallback arm with no <c>CommitDispatcher</c> installed and checks that
    /// <c>OnRowChanged</c> lands on a thread-pool thread other than the one that committed. The
    /// structural checks above pin the shape of the code; this pins what the shape buys, which is
    /// the half the doc got wrong.
    ///
    /// The commit is issued from a dedicated thread rather than the test's own. xUnit runs tests
    /// on the thread pool, and an <c>await</c> here returns that thread to the pool — from where
    /// <c>Task.Run</c> may legitimately hand it the offloaded work, making the two ids match on a
    /// busy machine even though nothing ran on the committing thread. A thread the pool does not
    /// own cannot be reused that way, so the comparison stays decisive under load.
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
        // id cannot be recycled onto the pool thread we are about to compare it against.
        using var assertionsDone = new ManualResetEventSlim(false);
        var committer = new Thread(() =>
        {
            InvokeHandleAsyncCommit(state, element, (RowKey)1, new TestItem(1, "Alice edited"), new TestItem(1, "Alice"));
            assertionsDone.Wait(Timeout);
        })
        {
            IsBackground = true,
            Name = "HandleAsyncCommit caller",
        };

        // Watchdog: a regression that ran the callback to completion on the committing thread would
        // block that thread until this fires, so the test fails on thread identity rather than
        // hanging. Registered before the commit, and torn down with the test either way.
        using var watchdog = new CancellationTokenSource(Timeout);
        using var unblock = watchdog.Token.UnsafeRegister(_ => release.TrySetResult(), null);

        committer.Start();

        try
        {
            await entered.Task.WaitAsync(Timeout, ct);

            Assert.NotEqual(committer.ManagedThreadId, callbackThreadId);
            Assert.True(
                onPoolThread,
                $"{MethodName}'s fallback arm left the committing thread but did not land on the " +
                "thread pool. The <remarks> say Task.Run puts OnRowChanged on a thread-pool thread; " +
                "update them if the offload target has changed.");
        }
        catch (TimeoutException)
        {
            Assert.Fail(
                $"{MethodName}'s fallback arm never invoked OnRowChanged. The <remarks> promise it " +
                "is offloaded to the thread pool, so something has to run it.");
        }
        finally
        {
            release.TrySetResult();
            assertionsDone.Set();
            committer.Join(Timeout);
        }
    }

    /// <summary>
    /// A two-row grid state plus the element that carries <paramref name="onRowChanged"/>. No
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

    // ── parsing helpers ─────────────────────────────────────────────

    private static string DocText()
    {
        var doc = Method.GetLeadingTrivia()
            .Select(t => t.GetStructure())
            .OfType<DocumentationCommentTriviaSyntax>()
            .FirstOrDefault();

        Assert.True(doc is not null, $"{MethodName} has no XML documentation comment.");
        return doc!.ToFullString();
    }

    /// <summary>
    /// The doc's <c>&lt;para&gt;</c> blocks as plain prose: cref targets reduced to the member
    /// they name, remaining XML tags and <c>///</c> prefixes dropped, whitespace collapsed. Phrase
    /// matching then survives the doc's line wrapping and its &lt;b&gt;/&lt;c&gt; emphasis markup.
    /// </summary>
    private static IReadOnlyList<string> DocParagraphs()
    {
        var paragraphs = Regex.Matches(DocText(), "<para>(.*?)</para>", RegexOptions.Singleline)
            .Select(m => Flatten(m.Groups[1].Value))
            .ToList();

        Assert.True(
            paragraphs.Count > 0,
            $"{MethodName}'s doc has no <para> blocks. This guard reads each commit arm's threading " +
            "contract out of its own labelled paragraph, so that a doc cannot pass by naming the " +
            "right words against the wrong arm — see issue #958.");

        return paragraphs;
    }

    /// <summary>
    /// The single <c>&lt;para&gt;</c> introduced by <paramref name="label"/>. Requiring exactly one
    /// is what keeps the two contracts in separate, individually checkable paragraphs.
    /// </summary>
    private static string ParagraphLabelled(string label)
    {
        var matches = DocParagraphs()
            .Where(p => p.StartsWith(label, StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.True(
            matches.Count == 1,
            $"{MethodName}'s doc should open exactly one <para> with \"{label}\", found " +
            $"{matches.Count}. Each arm's threading contract is checked inside its own labelled " +
            "paragraph — keep the labels if you restructure the doc.");

        return matches[0];
    }

    private static void AssertStates(string paragraph, string label, string[] anyOf, string why)
        => Assert.True(
            anyOf.Any(phrase => paragraph.Contains(phrase, StringComparison.OrdinalIgnoreCase)),
            $"The \"{label}\" paragraph of {MethodName}'s doc no longer says " +
            $"{string.Join(" / ", anyOf.Select(p => $"\"{p}\""))}, so it stops telling a caller " +
            $"{why}. Both arms' threading has to stay spelled out where it belongs — that is the " +
            $"whole point of issue #958.\n\n{paragraph}");

    private static string Flatten(string xml)
    {
        // Keep what a cref points at: "CommitDispatcher" only ever appears in the doc as one.
        var text = Regex.Replace(
            xml,
            """<see(also)?\s+cref\s*=\s*"([^"]*)"\s*/?>""",
            m => " " + m.Groups[2].Value.Split('.')[^1] + " ");
        text = Regex.Replace(text, "<[^>]*>", " ");
        text = Regex.Replace(text, @"^\s*///", " ", RegexOptions.Multiline);
        return Regex.Replace(text, @"\s+", " ").Trim();
    }

    /// <summary>
    /// The local the dispatcher branch binds — <c>dispatch</c> in
    /// <c>if (state.CommitDispatcher is { } dispatch)</c>. Empty when the condition binds nothing,
    /// in which case the caller falls back to matching on <c>CommitDispatcher</c> directly.
    /// </summary>
    private static string DispatcherLocalName()
        => DispatcherBranch().Condition
            .DescendantNodesAndSelf()
            .OfType<SingleVariableDesignationSyntax>()
            .Select(d => d.Identifier.Text)
            .FirstOrDefault() ?? string.Empty;

    /// <summary>The <c>if (state.CommitDispatcher is ...)</c> statement — the dispatcher arm.</summary>
    private static IfStatementSyntax DispatcherBranch()
    {
        var dispatcherIf = MethodBody().Statements
            .OfType<IfStatementSyntax>()
            .FirstOrDefault(s => s.Condition.ToString().Contains("CommitDispatcher", StringComparison.Ordinal));

        Assert.True(
            dispatcherIf is not null,
            $"{MethodName} no longer branches on CommitDispatcher at statement level — the two-arm " +
            "shape this guard describes has changed, so both the guard and the doc need revisiting.");

        return dispatcherIf!;
    }

    /// <summary>Everything after the dispatcher branch — the no-dispatcher fallback.</summary>
    private static IReadOnlyList<StatementSyntax> FallbackArmStatements()
    {
        var body = MethodBody();
        var index = body.Statements.IndexOf(DispatcherBranch());

        var fallback = body.Statements.Skip(index + 1).ToList();
        Assert.True(fallback.Count > 0, $"{MethodName} has no statements after the CommitDispatcher branch.");
        return fallback;
    }

    private static BlockSyntax MethodBody()
    {
        Assert.True(Method.Body is not null, $"{MethodName} is expression-bodied; this guard expects a block body.");
        return Method.Body!;
    }

    private static IEnumerable<SyntaxNode> Descendants(IEnumerable<StatementSyntax> statements)
        => statements.SelectMany(s => s.DescendantNodesAndSelf());

    /// <summary>Matches <c>Task.Run(...)</c> however <c>Task</c> is qualified.</summary>
    private static bool IsTaskRun(InvocationExpressionSyntax invocation)
        => invocation.Expression is MemberAccessExpressionSyntax { Name.Identifier.Text: "Run" } access
        && access.Expression.ToString().EndsWith("Task", StringComparison.Ordinal);

    private static bool IsNamedInvocation(InvocationExpressionSyntax invocation, params string[] names)
        => invocation.Expression is MemberAccessExpressionSyntax access
        && names.Contains(access.Name.Identifier.Text, StringComparer.Ordinal);

    private static bool IsOnRowChangedInvocation(InvocationExpressionSyntax invocation)
        => invocation.Expression.ToString().Contains("OnRowChanged", StringComparison.Ordinal);

    private static MethodDeclarationSyntax FindHandleAsyncCommit()
    {
        var root = RepoRootFinder.FindRepoRoot();
        Assert.NotNull(root);

        var file = Path.Join(root!, "src", "Reactor.Advanced", "Controls", "DataGrid", "DataGridComponent.cs");
        Assert.True(File.Exists(file), $"DataGridComponent.cs not found at {file}");

        var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(file));
        var methods = tree.GetCompilationUnitRoot()
            .DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Where(m => m.Identifier.Text == MethodName)
            .ToList();

        Assert.True(
            methods.Count == 1,
            $"Expected exactly one {MethodName} declaration in DataGridComponent.cs, found {methods.Count}.");

        return methods[0];
    }
}
