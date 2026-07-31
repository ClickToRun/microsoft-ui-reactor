using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Microsoft.UI.Reactor.SelfTests;

/// <summary>
/// Runs all in-process self-test fixtures in a single Host app launch and reports
/// one [TestMethod] per fixture. The Host mounts each fixture, runs assertions via
/// VisualTreeHelper, and emits TAP to stdout. We parse the TAP stream, split it by
/// `# Running: <fixture>` boundaries, and pair each fixture with pass/fail.
///
/// Fixture names are discovered at test-discovery time by launching the Host with
/// `--list-fixtures` (a fast-path that prints names and exits without starting WinUI).
/// </summary>
[TestClass]
public class SelfTestBatch
{
    /// <summary>
    /// Whole-suite process budget for the <c>--self-test</c> run. This is a <b>backstop, not the
    /// hang detector</b>, and that distinction is what sets its size.
    ///
    /// <para>The Host owns two watchdogs that attribute <i>causally</i>: a per-fixture graceful
    /// timeout (<c>SelfTestFixtureBase.FixtureTimeout</c>, which emits
    /// <c>not ok &lt;n&gt; &lt;fixture&gt;_TIMEOUT</c>) and an off-dispatcher watchdog that
    /// declares a hang after 60 s of no fixture progress (emitting <c>HANG_DETECTED:</c> and
    /// fast-failing). Both name a culprit. This cap only fires when <i>both</i> were unable to —
    /// they are disabled under a debugger and via
    /// <c>REACTOR_SELFTEST_HANG_TIMEOUT_SECONDS</c> — and its attribution is merely
    /// <b>positional</b>: whichever fixture happened to be in flight.</para>
    ///
    /// <para><b>So it must be sized as "the suite could not legitimately take this long", not as
    /// "the suite normally takes this long".</b> It was 300 s, against measured runs of 262–346 s
    /// locally and up to 97.6 % of cap on CI — i.e. <c>main</c> breached it with no PR
    /// contribution, and every breach manufactured a spurious single-fixture failure on an
    /// arbitrary victim (issue #988). Raising it does not delay real hang detection, because the
    /// 60 s off-dispatcher watchdog fires first and names the offender.</para>
    /// </summary>
    private const int DefaultSelfTestTimeoutSeconds = 900;   // 15 min

    /// <summary>
    /// Overrides <see cref="DefaultSelfTestTimeoutSeconds"/> for slow or heavily contended
    /// machines and for stress shards, mirroring the Host's own
    /// <c>REACTOR_SELFTEST_HANG_TIMEOUT_SECONDS</c> knob.
    /// </summary>
    internal const string TimeoutEnvVar = "REACTOR_SELFTEST_TIMEOUT_SECONDS";

    /// <summary>
    /// Soft threshold at which <see cref="SuiteDuration_WithinBudget"/> warns. Deliberately its
    /// <b>own constant</b> rather than a fraction of <see cref="DefaultSelfTestTimeoutSeconds"/>:
    /// 80 % of a deliberately-generous 900 s cap is 720 s, which would only warn long after the
    /// margin had actually eroded. 420 s is ≈1.2× the slowest run measured when this was written,
    /// so it fires while there is still headroom to act.
    /// </summary>
    internal const int SuiteDurationWarnSeconds = 420;

    private static readonly int SelfTestTimeoutMs =
        ResolveTimeoutSeconds(Environment.GetEnvironmentVariable(TimeoutEnvVar)) * 1000;

    private const int ListFixturesTimeoutMs = 30_000;

    /// <summary>
    /// Resolves the suite budget in seconds from an environment value, falling back to
    /// <see cref="DefaultSelfTestTimeoutSeconds"/> for absent, unparseable, non-positive, or
    /// absurdly large input. A malformed override must not silently produce a tiny (or zero)
    /// budget — that would recreate the exact failure this constant exists to prevent, only
    /// faster — nor a value that overflows when converted to milliseconds, which lands as a
    /// *negative* timeout and fails initialization outright.
    /// </summary>
    internal static int ResolveTimeoutSeconds(string? envValue)
    {
        if (!string.IsNullOrWhiteSpace(envValue)
            && int.TryParse(envValue.Trim(), System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var seconds)
            && seconds > 0
            && seconds <= int.MaxValue / 1000)
        {
            return seconds;
        }

        return DefaultSelfTestTimeoutSeconds;
    }

    /// <summary>The budget actually in force this run, after the environment override.</summary>
    internal static int EffectiveTimeoutSeconds => SelfTestTimeoutMs / 1000;

    // Per-fixture aggregated outcome, populated by ClassInitialize.
    // Key = fixture name; Value = (passed, joined failure reasons).
    private static readonly ConcurrentDictionary<string, (bool Passed, string Detail)> _byFixture = new();
    private static string _fullOutput = "";
    private static bool _initialized;
    private static string? _initError;
    // Captured process outcome for the teardown-exit-code guard (issue #680).
    private static int _exitCode;
    private static bool _timedOut;
    // When the Host run aborts (hang/timeout) we attribute the failure to a
    // single fixture, but every later fixture still has no entry in
    // _byFixture. _abortedReason marks the run as not-fully-executed so the
    // Fixture test method can report missing entries as Inconclusive rather
    // than cascading "was not reported by the Host" failures across every
    // fixture downstream of the hang.
    private static string? _abortedReason;
    // Suite duration, for the duration gate and for the budget-overrun message.
    // _hostElapsedSeconds is the Host's own figure (excludes process start and
    // pipe-drain overhead); _wrapperElapsedSeconds is what this process measured.
    private static double _wrapperElapsedSeconds;
    private static double? _hostElapsedSeconds;

    private static double ElapsedSeconds => _hostElapsedSeconds ?? _wrapperElapsedSeconds;

    [ClassInitialize]
    public static void RunSelfTests(TestContext context)
    {
        var exe = FindHostExe();
        var stopwatch = Stopwatch.StartNew();
        var (stdout, stderr, exitCode, timedOut) = RunProcess(exe, "--self-test", SelfTestTimeoutMs);
        stopwatch.Stop();
        _wrapperElapsedSeconds = stopwatch.Elapsed.TotalSeconds;
        _exitCode = exitCode;
        _timedOut = timedOut;

        _fullOutput = stdout;
        if (!string.IsNullOrEmpty(stderr))
            _fullOutput += "\n--- stderr ---\n" + stderr;

        _hostElapsedSeconds = ExtractSuiteElapsedSeconds(stdout);

        var tap = ParseTap(stdout);

        // Off-dispatcher watchdog in the Host emits a structured signal on
        // dispatcher-starvation hangs. Parse it from stdout *and* stderr (the
        // Host writes to both before FailFast so the signal survives buffered
        // pipes), then attribute the failure to the named fixture so the dev
        // sees a clear pointer to the offender instead of an opaque "process
        // timed out" affecting every fixture.
        var hangFixture = ExtractHangSignal(stdout) ?? ExtractHangSignal(stderr);

        var outcome = ClassifyAbort(
            timedOut, hangFixture, ExtractLastRunningFixture(stdout),
            SelfTestTimeoutMs, _wrapperElapsedSeconds, _byFixture.Count, TryGetFixtureCount(),
            Tail(_fullOutput, 4000));

        if (outcome is not null)
        {
            _byFixture[outcome.Fixture] = (false, outcome.Detail);
            _abortedReason = outcome.AbortReason;
        }
        else if (timedOut)
        {
            _initError = $"Self-test process timed out after {SelfTestTimeoutMs}ms with no fixture attribution.\n{_fullOutput}";
        }

        if (timedOut)
        {
            _initialized = true;
            return;
        }

        MarkEarlyAbortIfNeeded(exitCode, tap);

        _initialized = true;

        if (exitCode != 0 && _byFixture.IsEmpty)
            _initError = $"Self-test process exited with code {exitCode} but produced no parsable TAP output.\n{_fullOutput}";
    }

    /// <summary>The fixture an aborted run is attributed to, and how that attribution is worded.</summary>
    internal sealed record AbortOutcome(string Fixture, string Detail, string AbortReason);

    /// <summary>
    /// Decides which fixture (if any) an aborted run is attributed to, and with what wording.
    /// Returns null when the run was not aborted, or when nothing can be attributed.
    ///
    /// <para>This is the whole point of issue #988, so it is a <b>pure function that production
    /// actually calls</b> rather than three inline branches. Three cases reach here and they used
    /// to be two:</para>
    /// <list type="bullet">
    /// <item><b>Hang, process died.</b> The Host's watchdog printed <c>HANG_DETECTED</c> and
    /// <c>FailFast</c>ed, so the process exited on its own. Causal — this is the ordinary hang
    /// path, and the common one.</item>
    /// <item><b>Hang, process would not die.</b> Same signal, but <c>FailFast</c> did not take the
    /// process down before the wrapper's budget expired. Still causal.</item>
    /// <item><b>Budget expired with no signal.</b> Positional: the named fixture was merely in
    /// flight. This is the case that manufactured seven spurious failures across six PRs.</item>
    /// </list>
    /// </summary>
    internal static AbortOutcome? ClassifyAbort(
        bool timedOut, string? hangFixture, string? lastRunningFixture,
        int budgetMs, double elapsedSeconds, int fixturesReported, int? fixturesTotal, string tail)
    {
        if (hangFixture is not null)
        {
            return new AbortOutcome(
                hangFixture,
                DescribeStarvationHang(hangFixture, timedOut, budgetMs, tail),
                StarvationHangAbortReason(hangFixture, timedOut));
        }

        if (!timedOut || lastRunningFixture is null) return null;

        return new AbortOutcome(
            lastRunningFixture,
            DescribeBudgetOverrun(lastRunningFixture, budgetMs, elapsedSeconds, fixturesReported, fixturesTotal, tail),
            BudgetOverrunAbortReason(lastRunningFixture, budgetMs));
    }

    /// <summary>
    /// Renders a Host exit code with its NTSTATUS interpretation, for the truncated-TAP paths
    /// where the only question a triager has is "did the host fault, or did something kill it?".
    ///
    /// <para>This is a <b>strong prior, not a guarantee</b>, and the wording keeps that honest.
    /// What is measured: .NET's <c>Process.Kill()</c> — and <c>Stop-Process -Force</c>, which goes
    /// through the same path — produce <c>-1</c> (<c>0xFFFFFFFF</c>); <c>taskkill /F</c> produces
    /// <c>1</c>. What is NOT established: that an external killer *cannot* produce an
    /// NTSTATUS-shaped code. <c>TerminateProcess</c> takes <c>uExitCode</c> as an arbitrary
    /// <c>UINT</c>, so the caller picks it; nothing structurally prevents a killer choosing
    /// <c>0xC0000005</c>. It just isn't what any killer in this environment does.</para>
    ///
    /// <para><b>Scope of the prior, and when it expires.</b> The inference is not over
    /// <c>TerminateProcess</c> — it is over <i>the population of things that kill this process</i>.
    /// It holds because every killer present today (this harness's own watchdog, an external
    /// <c>Process.Kill</c>/<c>Stop-Process</c>, <c>taskkill /F</c>) lands on <c>-1</c> or
    /// <c>1</c>. It weakens the moment that population changes — a new harness, a CI job-object
    /// teardown, a container runtime, or any watchdog that deliberately propagates the child's
    /// status. <b>If you add something that can kill the Host, check what exit code it produces
    /// and revisit this method.</b> A reader with no cue that the prior is environment-scoped
    /// would keep trusting it after it stopped being true.</para>
    ///
    /// <para>The raw value is always printed alongside the interpretation so nobody has to trust
    /// the mapping. A triager who reads "external kill" as certain stops looking, and the cost of
    /// a false certainty here is chasing the wrong cause.</para>
    /// </summary>
    internal static string DescribeExitCode(int exitCode)
    {
        // Compared as uint: these are NTSTATUS values that arrive as negative Int32.
        var known = (uint)exitCode switch
        {
            0xC0000005 => "STATUS_ACCESS_VIOLATION",
            0xC000027B => "STATUS_STOWED_EXCEPTION (WinUI/WinRT — the most likely one here)",
            0xC0000409 => "STATUS_STACK_BUFFER_OVERRUN / fast-fail",
            0xC00000FD => "STATUS_STACK_OVERFLOW",
            0xE0434352 => "CLR managed exception (unhandled .NET exception)",
            _ => null,
        };

        // The CLR's managed-exception tag is NOT NTSTATUS-shaped — 0xE0434352 & 0xF0000000 is
        // 0xE0000000, so the mask below does not catch it and it would otherwise fall through
        // with no verdict at all. That is the likeliest crash mode for a .NET host, so it gets
        // its own branch. Same tag the Devtools stress runner already keys on
        // (DevtoolsStressE2ERunner.cs) and MxcSandbox documents.
        bool clrManaged = (uint)exitCode == 0xE0434352u;

        // NTSTATUS failure codes are 0xC0000000-shaped. Treat that whole space as
        // "the host faulted", not just the four named above.
        bool ntStatusShaped = ((uint)exitCode & 0xF0000000u) == 0xC0000000u;

        var raw = $"Exit code: {exitCode} (0x{(uint)exitCode:X8}{(known is null ? "" : " " + known)})";

        if (clrManaged)
        {
            return raw + "\n  -> Unhandled MANAGED exception: the host almost certainly crashed " +
                   "on its own, via the CLR's unhandled-exception path rather than a native " +
                   "fault. The exception type and stack trace are in the Host's stderr / the " +
                   "output tail below — read those first. As with the native-fault codes, a " +
                   "terminator can pass any value to TerminateProcess, so this is a strong prior " +
                   "rather than proof.";
        }

        if (ntStatusShaped)
        {
            return raw + "\n  -> NTSTATUS-shaped: the host almost certainly crashed on its own. " +
                   "Look for the faulting fixture, not for an external killer. " +
                   "(Known killers in this environment exit -1 or 1, but TerminateProcess lets a " +
                   "caller choose any code, so this is a strong prior rather than proof.)";
        }

        if (exitCode is -1 or 1)
        {
            return raw + "\n  -> Category is decidable, cause is NOT. This says the host did not fault; " +
                   "it does not say who ended it. Beware: `RunProcess` SYNTHESIZES -1 for this " +
                   "harness's own watchdog kill (it discards the real code), and an external " +
                   "`Process.Kill` / `Stop-Process`, a parent reap and a CI job-object teardown all " +
                   "land on -1 too — so -1 alone cannot name the agent. `taskkill /F` and a genuine " +
                   "fixture failure both exit 1; use the TAP trailer to separate those two: present " +
                   "trailer = real failure, truncated = killed.";
        }

        return raw;
    }

    /// <summary>
    /// Message for a run where the Host emitted a <c>HANG_DETECTED:</c> signal, i.e. the named
    /// fixture starved the dispatcher. <b>Causal</b> attribution: this fixture is the culprit, and
    /// a per-fixture repro genuinely reproduces it — which is why this message keeps the
    /// <c>--filter</c> line and <see cref="DescribeBudgetOverrun"/> deliberately does not.
    /// </summary>
    /// <param name="alsoTimedOut">
    /// False on the ordinary path (the watchdog's <c>FailFast</c> took the process down, so the
    /// wrapper never had to). True when even <c>FailFast</c> did not land before the suite budget
    /// expired — worth saying out loud, because it means the process was wedged below the CLR.
    /// </param>
    internal static string DescribeStarvationHang(string fixture, bool alsoTimedOut, int budgetMs, string tail)
    {
        var lead = alsoTimedOut
            ? $"DISPATCHER-STARVATION HANG in '{fixture}' — this fixture IS the cause.\n" +
              $"The Host's off-dispatcher watchdog named it via a HANG_DETECTED signal, and the " +
              $"process then failed to exit within the {budgetMs / 1000}s suite budget, so the " +
              $"wrapper killed it. FailFast not landing points below the CLR — a native lock or " +
              $"a wedged UI thread.\n"
            : $"DISPATCHER-STARVATION HANG in '{fixture}' — this fixture IS the cause.\n" +
              $"The Host's off-dispatcher watchdog named it via a HANG_DETECTED signal and " +
              $"fast-failed the process.\n";

        return lead +
               $"Repro: build the Host (AOT publish if needed) and run " +
               $"`Reactor.AppTests.Host.exe --self-test --no-aot-skip --filter {fixture}`. " +
               $"Set DOTNET_DbgEnableMiniDump=1 (and COMPlus_DbgEnableMiniDump=1) to capture a dump.\n" +
               $"--- tail of full output ---\n{tail}";
    }

    /// <summary>
    /// Message for a wrapper timeout with <b>no</b> hang signal: the suite ran out of its shared
    /// process budget, and the harness blamed whichever fixture happened to be in flight.
    ///
    /// <para><b>This message's job is to stop the reader debugging that fixture.</b> Issue #988
    /// records seven distinct victims across six PRs, all innocent, because three things in the
    /// old message pointed the wrong way: the fixture name looked causal, MSTest printed the
    /// fixture's own elapsed time (<c>[16 ms]</c>) next to a 300-second process kill so it read as
    /// a fast assertion failure, and the <c>Repro:</c> line suggested <c>--filter &lt;fixture&gt;</c>
    /// — which removes the ~1387 other fixtures sharing the budget, i.e. removes the cause, so the
    /// suggested reproduction essentially always passes and argues the fixture is fine.</para>
    ///
    /// <para>It must not overcorrect into the opposite false claim. The absence of a
    /// <c>HANG_DETECTED</c> signal does <b>not</b> prove the fixture innocent — the watchdog can be
    /// disabled by env or by an attached debugger, and a fixture can be pathologically slow, or
    /// order-dependent, while still pumping the dispatcher often enough never to trip it.
    /// Positional attribution means <i>unproven</i>, not <i>exonerated</i>, and the wording says
    /// so.</para>
    /// </summary>
    internal static string DescribeBudgetOverrun(
        string inFlight, int budgetMs, double elapsedSeconds, int fixturesReported, int? fixturesTotal, string tail)
    {
        var budgetSeconds = budgetMs / 1000.0;
        var progress = fixturesTotal is int total
            ? $"{fixturesReported} of {total}"
            : $"{fixturesReported}";

        return $"SUITE BUDGET EXCEEDED — '{inFlight}' is NOT PROVEN to be the cause.\n" +
               $"The whole selftest suite shares ONE process budget. It expired while '{inFlight}' " +
               $"happened to be running, so the harness killed the Host and attributed the kill to " +
               $"it. That attribution is POSITIONAL: it records where the run was, not what went " +
               $"wrong, and the fixture named here differs from run to run.\n" +
               $"  elapsed   : {elapsedSeconds:F1}s against a {budgetSeconds:F0}s budget " +
               $"(these are necessarily close — the kill IS the budget expiring)\n" +
               $"  reported  : {progress} fixtures had TAP output parsed before the kill " +
               $"(includes '{inFlight}', which was still running)\n" +
               $"  remaining : reported Skipped (Assert.Inconclusive) — never RUN, so their " +
               $"results say nothing\n" +
               $"Do NOT start by debugging '{inFlight}', and do NOT re-run it under `--filter`: " +
               $"that removes the other fixtures that shared the budget — i.e. removes the cause — " +
               $"so it passes whether or not the fixture is healthy. It is not a valid " +
               $"reproduction of a suite-budget kill in either direction.\n" +
               $"Start with total suite duration instead. If the suite has simply grown into its " +
               $"cap (issue #988), raise it with {TimeoutEnvVar} or trim suite time; the " +
               $"`# Fixture time:` TAP comments rank the offenders. If duration looks normal, " +
               $"look for a fixture that wedged WITHOUT tripping the Host's per-fixture timeout " +
               $"or its 60s off-dispatcher watchdog — which is the one scenario where '{inFlight}' " +
               $"could still turn out to be at fault.\n" +
               $"The exit code is not evidence on this path: RunProcess synthesizes -1 for its own " +
               $"watchdog kill and discards the real one.\n" +
               $"--- tail of full output ---\n{tail}";
    }

    // Abort reasons are stamped verbatim onto every unexecuted fixture (see Fixture below), so
    // their prefixes are the cheapest triage signal there is: they are readable off ANY skipped
    // fixture with no re-run and no raw job log. Keeping the causal and positional kinds distinct
    // here is the whole point — they used to share one string.
    internal static string StarvationHangAbortReason(string fixture, bool alsoTimedOut) =>
        alsoTimedOut
            ? $"Run aborted by dispatcher-starvation hang on fixture '{fixture}' (FailFast did not " +
              $"land; the wrapper's budget killed the process)"
            : $"Run aborted by dispatcher-starvation hang on fixture '{fixture}'";

    internal static string BudgetOverrunAbortReason(string inFlight, int budgetMs) =>
        $"Run aborted: suite exceeded its {budgetMs / 1000}s budget with fixture '{inFlight}' in " +
        $"flight (POSITIONAL attribution — that fixture is not proven to be at fault)";

    /// <summary>
    /// Renders the suite's wall clock against the soft warn threshold and the hard budget, and
    /// says whether it warrants a warning. Pure, so the thresholds are testable without a run.
    /// </summary>
    internal static (bool Warn, string Text) DescribeSuiteDuration(
        double elapsedSeconds, int warnSeconds, int budgetSeconds)
    {
        var percentOfBudget = budgetSeconds > 0 ? elapsedSeconds / budgetSeconds * 100.0 : 0.0;
        var warn = elapsedSeconds > warnSeconds;

        var text =
            $"Selftest suite duration: {elapsedSeconds:F1}s " +
            $"({percentOfBudget:F1}% of the {budgetSeconds}s hard budget, warn above {warnSeconds}s).";

        if (warn)
        {
            text += $"\nThe suite is approaching the budget that kills it. When it crosses, the " +
                    $"harness reports ONE arbitrary fixture as failed and skips the rest — a " +
                    $"misleading signal that has cost multiple investigations (issue #988). " +
                    $"Trim suite time, or raise the budget deliberately rather than discovering " +
                    $"it in a red PR.";
        }

        return (warn, text);
    }

    /// <summary>
    /// Reads the Host's own <c># Suite elapsed: &lt;seconds&gt;</c> trailer. Preferred over this
    /// process's stopwatch because it excludes process start and pipe-drain overhead (measured at
    /// ≈2.5 s in issue #988, enough to make a 300 s kill report as 302.5 s and confuse the margin).
    /// Returns null when the marker is absent — an older Host, or a run killed before the trailer.
    /// </summary>
    /// <remarks>Marker literal is duplicated from <c>SelfTestRunner.SuiteElapsedMarker</c>; the
    /// Host assembly is referenced with <c>ReferenceOutputAssembly=false</c> so it cannot be shared.</remarks>
    internal static double? ExtractSuiteElapsedSeconds(string stdout)
    {
        if (string.IsNullOrEmpty(stdout)) return null;
        const string marker = "# Suite elapsed: ";
        double? last = null;
        foreach (var raw in stdout.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith(marker, StringComparison.Ordinal)) continue;
            if (double.TryParse(line[marker.Length..].Trim(),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var seconds))
            {
                last = seconds;
            }
        }
        return last;
    }

    private static int? TryGetFixtureCount()
    {
        // Discovery has normally already resolved this (DynamicData drives AllFixtures), but a
        // failure here must not replace a useful timeout message with a discovery exception.
        try { return FixtureNames.Value.Length; }
        catch { return null; }
    }

    private static string? ExtractHangSignal(string output)
    {
        if (string.IsNullOrEmpty(output)) return null;
        const string marker = "HANG_DETECTED: ";
        foreach (var raw in output.Split('\n'))
        {
            var idx = raw.IndexOf(marker, StringComparison.Ordinal);
            if (idx < 0) continue;
            var rest = raw[(idx + marker.Length)..].TrimStart();
            var space = rest.IndexOf(' ');
            var name = (space > 0 ? rest[..space] : rest).Trim();
            if (name.Length > 0) return name;
        }
        return null;
    }

    private static string? ExtractLastRunningFixture(string stdout)
    {
        if (string.IsNullOrEmpty(stdout)) return null;
        string? last = null;
        const string marker = "# Running: ";
        foreach (var raw in stdout.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith(marker, StringComparison.Ordinal))
                last = line[marker.Length..].Trim();
        }
        return string.IsNullOrEmpty(last) ? null : last;
    }

    private static string Tail(string s, int maxChars)
    {
        if (string.IsNullOrEmpty(s) || s.Length <= maxChars) return s;
        return "..." + s[^maxChars..];
    }

    private sealed record TapParseResult(string? LastRunningFixture, bool SawTotalFailures);

    private static TapParseResult ParseTap(string stdout)
    {
        // Two TAP emitter sources:
        //   Harness check:   "ok <checkName>"  /  "not ok <checkName> - <reason>"
        //   SelfTestRunner:  "# Running: <fixtureName>"
        //                    "not ok <index> <fixtureName> - fixture not found"     (before any marker)
        //                    "not ok <index> <fixtureName>_CRASH - <type>: <msg>"   (after marker if RunAsync threw)
        //
        // Runner-level "not ok" lines start with a numeric test index; check-level lines do not.
        // Runner-level failures attribute to their own fixture name regardless of `current`.

        string? current = null;
        var failuresForCurrent = new List<string>();
        var sawChecksForCurrent = false;
        string? lastRunningFixture = null;
        var sawTotalFailures = false;

        void Flush()
        {
            if (current is null) return;
            if (_byFixture.TryGetValue(current, out var existing) && !existing.Passed && failuresForCurrent.Count == 0)
                return;

            var passed = failuresForCurrent.Count == 0 && sawChecksForCurrent;
            var detail = failuresForCurrent.Count == 0
                ? (sawChecksForCurrent ? "" : "fixture emitted no TAP checks")
                : string.Join("\n", failuresForCurrent);
            _byFixture[current] = (passed, detail);
        }

        foreach (var raw in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.Trim();
            if (line.StartsWith("# Running: "))
            {
                Flush();
                current = line["# Running: ".Length..].Trim();
                lastRunningFixture = current;
                failuresForCurrent = new List<string>();
                sawChecksForCurrent = false;
            }
            else if (line.StartsWith("# Total failures:", StringComparison.Ordinal))
            {
                sawTotalFailures = true;
            }
            else if (line.StartsWith("ok "))
            {
                // Harness-level pass; ignore payload, just note that current saw checks.
                sawChecksForCurrent = true;
            }
            else if (line.StartsWith("not ok "))
            {
                var rest = line[7..].Trim();
                if (TryParseRunnerLevelFailure(rest, out var fixtureName, out var detail))
                {
                    if (string.Equals(fixtureName, current, StringComparison.Ordinal))
                    {
                        sawChecksForCurrent = true;
                        failuresForCurrent.Add(detail);
                    }
                    else
                    {
                        // Runner-level failure — attribute directly to the fixture name,
                        // overriding any in-progress `current` bucket.
                        _byFixture[fixtureName] = (false, detail);
                    }
                }
                else
                {
                    sawChecksForCurrent = true;
                    if (current is not null)
                        failuresForCurrent.Add(rest);
                    // A check-level failure with no `# Running:` context is malformed TAP;
                    // drop it into the output blob (already captured in _fullOutput).
                }
            }
        }
        Flush();
        return new TapParseResult(lastRunningFixture, sawTotalFailures);
    }

    private static bool TryParseRunnerLevelFailure(string rest, out string fixtureName, out string detail)
    {
        // Runner-level format: "<digits> <fixtureName>[_CRASH] - <detail>"
        fixtureName = "";
        detail = "";
        var firstSpace = rest.IndexOf(' ');
        if (firstSpace <= 0) return false;
        var head = rest[..firstSpace];
        if (!head.All(char.IsDigit)) return false;

        var tail = rest[(firstSpace + 1)..].TrimStart();
        var dashIdx = tail.IndexOf(" - ");
        string namePart;
        if (dashIdx >= 0)
        {
            namePart = tail[..dashIdx].Trim();
            detail = tail[(dashIdx + 3)..].Trim();
        }
        else
        {
            namePart = tail.Trim();
            detail = "(no detail)";
        }

        if (namePart.Length == 0) return false;
        fixtureName = StripRunnerFailureSuffix(namePart);
        return true;
    }

    private static string StripRunnerFailureSuffix(string namePart)
    {
        string[] suffixes = ["_CRASH", "_TIMEOUT"];
        foreach (var suffix in suffixes)
        {
            if (namePart.EndsWith(suffix, StringComparison.Ordinal))
                return namePart[..^suffix.Length];
        }

        return namePart;
    }

    private static void MarkEarlyAbortIfNeeded(int exitCode, TapParseResult tap)
    {
        if (_abortedReason is not null || exitCode == 0 && tap.SawTotalFailures)
            return;

        var fixtureNames = FixtureNames.Value;
        var firstMissingIndex = Array.FindIndex(fixtureNames, name => !_byFixture.ContainsKey(name));
        if (firstMissingIndex < 0)
            return;

        var hasReportedAfterMissing = fixtureNames
            .Skip(firstMissingIndex + 1)
            .Any(name => _byFixture.ContainsKey(name));
        if (hasReportedAfterMissing)
            return;

        var attributed = tap.LastRunningFixture;
        if (attributed is not null)
        {
            if (!_byFixture.TryGetValue(attributed, out var existing) || existing.Passed)
            {
                _byFixture[attributed] = (false,
                    $"Selftest Host exited before completing fixture '{attributed}'. " +
                    $"{DescribeExitCode(exitCode)}\n" +
                    $"Downstream fixtures were not executed.\n" +
                    $"--- tail of full output ---\n{Tail(_fullOutput, 4000)}");
            }

            _abortedReason = $"Run aborted after fixture '{attributed}'";
        }
        else
        {
            _abortedReason = $"Run aborted before fixture '{fixtureNames[firstMissingIndex]}'";
        }
    }

    public static IEnumerable<object[]> AllFixtures => FixtureNames.Value.Select(n => new object[] { n });

    [TestMethod]
    [DynamicData(nameof(AllFixtures))]
    public void Fixture(string name)
    {
        Assert.IsTrue(_initialized, "Self-test batch did not run.");
        if (_initError is not null)
            Assert.Fail(_initError);

        if (!_byFixture.TryGetValue(name, out var result))
        {
            if (_abortedReason is not null)
                Assert.Inconclusive(
                    $"{_abortedReason}; fixture '{name}' was not executed — this result carries NO " +
                    $"information about '{name}' itself. Read the abort reason above: it names " +
                    $"which of the four abort paths fired, and whether the fixture blamed by the " +
                    $"run was the cause or merely in flight.");
            Assert.Fail($"Fixture '{name}' was not reported by the Host. Full output:\n{_fullOutput}");
        }

        if (!result.Passed)
            Assert.Fail(result.Detail);
    }

    /// <summary>
    /// Reports the suite's wall clock every run, and warns — without failing — when it climbs past
    /// <see cref="SuiteDurationWarnSeconds"/>.
    ///
    /// <para>This exists because the failure it guards against is silent. Nothing measured suite
    /// duration before, so the margin against the process budget eroded run by run as fixtures
    /// were added (check counts moved 6090 → 6146 in a single day) until <c>main</c> itself
    /// breached the cap. The first visible symptom was an unrelated fixture failing on an
    /// unrelated PR. A number in the log every run makes that erosion observable while there is
    /// still headroom to act.</para>
    ///
    /// <para>Deliberately <c>Inconclusive</c> rather than <c>Fail</c>: duration depends on runner
    /// speed and contention, so a hard gate here would itself be a flake. A Skipped result cannot
    /// turn a slow runner into a red build, but it is conspicuous in the run summary.</para>
    ///
    /// <para><b>Why the number goes to a file and not just the console.</b> Measured, not assumed:
    /// under <c>dotnet test</c> the tests execute in a child <c>testhost</c> process whose stdout
    /// the runner does not forward. A probe writing to <c>Console.WriteLine</c> <i>and</i> straight
    /// to the process's standard-output handle produced neither marker in the run output at
    /// <c>console;verbosity=normal</c>, and the <c>Assert.Inconclusive</c> message did not appear
    /// either — only the bare line <c>Skipped SuiteDuration_WithinBudget</c>. So a
    /// <c>::warning::</c> workflow command emitted from here can never reach the Actions runner,
    /// and a gate whose report is invisible is the same silent erosion in a new costume.
    /// <c>GITHUB_STEP_SUMMARY</c> is plain file I/O performed by this process, so it is immune to
    /// whatever the runner does with stdout, and it renders on the run page. The console lines are
    /// kept for local <c>vstest</c>/IDE runs, where they do show up.</para>
    /// </summary>
    [TestMethod]
    public void SuiteDuration_WithinBudget()
    {
        Assert.IsTrue(_initialized, "Self-test batch did not run.");
        if (_initError is not null)
            Assert.Fail(_initError);

        if (_timedOut)
        {
            // Elapsed == budget by construction here; the overrun is already reported against the
            // in-flight fixture, and repeating it as a duration warning would add nothing.
            PublishToStepSummary(
                $"### ❌ Selftest suite exceeded its {SelfTestTimeoutMs / 1000}s hard budget\n\n" +
                $"The Host was killed. One fixture is reported failed, but that attribution is " +
                $"positional — see its message, and issue #988.");

            Assert.Inconclusive(
                $"Suite hit its {SelfTestTimeoutMs / 1000}s hard budget and was killed; see the " +
                $"budget-overrun failure for the attribution caveat.");
        }

        var (warn, text) = DescribeSuiteDuration(
            ElapsedSeconds, SuiteDurationWarnSeconds, SelfTestTimeoutMs / 1000);

        var source = _hostElapsedSeconds is not null ? "Host-reported" : "wrapper-measured";
        Console.WriteLine($"{text} [{source}]");

        PublishToStepSummary(
            $"### {(warn ? "⚠️" : "✅")} Selftest suite duration\n\n" +
            $"{text.Replace("\n", " ")}\n\n<sub>Source: {source}. Budget knob: " +
            $"`{TimeoutEnvVar}`. Background: issue #988.</sub>");

        if (warn)
        {
            // Kept for local runs; under `dotnet test` this is swallowed with the rest of the
            // testhost's stdout, which is why the step summary above is the load-bearing channel.
            Console.WriteLine($"::warning title=Selftest suite duration::{text.Replace("\n", " ")}");
            Assert.Inconclusive(text);
        }
    }

    /// <summary>
    /// Appends a markdown block to the GitHub Actions job summary, if we are running under one.
    /// </summary>
    private static void PublishToStepSummary(string markdown) =>
        TryAppendSummary(Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY"), markdown);

    /// <summary>
    /// Appends to the job-summary file, reporting whether it landed. Best-effort by design: this
    /// is a diagnostic channel, and a failure to write it must never turn a green selftest run red
    /// — an unwritable summary path would otherwise convert a healthy suite into a hard error,
    /// which is a strictly worse outcome than the silence this whole gate exists to fix.
    /// Returns false (rather than throwing) when there is no summary file, which is the normal
    /// case locally.
    /// </summary>
    internal static bool TryAppendSummary(string? path, string markdown)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;

        try
        {
            File.AppendAllText(path, markdown + "\n\n");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or NotSupportedException or ArgumentException)
        {
            Console.WriteLine($"Could not write the job summary ({ex.GetType().Name}: {ex.Message}).");
            return false;
        }
    }

    /// <summary>
    /// Regression guard for issue #680. The full self-test suite used to fault
    /// with 0xC0000005 at *final process teardown*: <c>SelfTestRunner</c> called
    /// <see cref="System.Environment.Exit(int)"/> from inside the live WinUI
    /// desktop message loop, which jumps straight to <c>ExitProcess</c> and lets
    /// the Windows loader run Microsoft.UI.Xaml's TLS destructors while the
    /// suite's accumulated XAML object graph is still mounted — a
    /// <c>DependencyObject</c> destructor then dereferences the XAML core's
    /// already-freed tear-off bookkeeping map and access-violates.
    /// <para>
    /// The per-fixture <see cref="Fixture"/> tests can't catch this: every
    /// fixture has already emitted its TAP result before the teardown crash, so
    /// the run looks green even though the process exited with a crash code.
    /// This guard asserts the Host exited with one of the only two codes the
    /// runner legitimately produces — 0 (all passed) or 1 (fixture failures) —
    /// so a teardown access violation (a large negative exit code) fails CI.
    /// </para>
    /// </summary>
    [TestMethod]
    public void HostProcessExitsCleanly_NoTeardownCrash()
    {
        Assert.IsTrue(_initialized, "Self-test batch did not run.");
        if (_initError is not null)
            Assert.Fail(_initError);

        // Hang/timeout is surfaced per-fixture by the watchdog path and the
        // process is killed (exit code is not meaningful), so this teardown
        // guard only applies to a run that completed on its own.
        if (_timedOut || _abortedReason is not null)
            Assert.Inconclusive(_abortedReason ?? "Self-test process timed out; teardown exit code is not meaningful.");

        Assert.IsTrue(_exitCode is 0 or 1,
            $"Self-test Host exited ABNORMALLY — the runner only ever returns 0 (all passed) or " +
            $"1 (fixture failures), so any other code means the process did not exit through the " +
            $"runner. Do not assume teardown: the guard is reached whenever the run was not " +
            $"already attributed to a hang/timeout, which does not by itself distinguish a " +
            $"teardown fault from an earlier crash or an external termination. The line below " +
            $"classifies the code; the issue #680 final-exit access violation is one known cause, " +
            $"not the only one.\n" +
            $"{DescribeExitCode(_exitCode)}\n" +
            $"--- tail of full output ---\n{Tail(_fullOutput, 4000)}");
    }

    // -- Discovery: one-shot Host launch to list fixture names -----------------

    private static readonly Lazy<string[]> FixtureNames = new(LoadFixtureNames);

    private static string[] LoadFixtureNames()
    {
        var exe = FindHostExe();
        var (stdout, stderr, exitCode, timedOut) = RunProcess(exe, "--list-fixtures", ListFixturesTimeoutMs);

        if (timedOut)
            throw new TimeoutException($"`--list-fixtures` timed out after {ListFixturesTimeoutMs}ms. Host: {exe}");

        if (exitCode != 0)
            throw new InvalidOperationException(
                $"`--list-fixtures` failed with exit code {exitCode}.\nstdout:\n{stdout}\nstderr:\n{stderr}");

        var names = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToArray();

        if (names.Length == 0)
            throw new InvalidOperationException(
                $"`--list-fixtures` returned no fixture names.\nstdout:\n{stdout}\nstderr:\n{stderr}");

        return names;
    }

    // -- Process runner: async reads + timeout race with kill ------------------

    private static (string Stdout, string Stderr, int ExitCode, bool TimedOut) RunProcess(
        string exe, string args, int timeoutMs)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start process: {exe} {args}");

        // Read both streams concurrently so neither pipe can block the child by
        // filling its OS buffer.
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        var exitTask = process.WaitForExitAsync();
        var timeoutTask = Task.Delay(timeoutMs);

        var completed = Task.WhenAny(exitTask, timeoutTask).GetAwaiter().GetResult();
        var timedOut = completed != exitTask;

        if (timedOut)
        {
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { /* already exited */ }
            process.WaitForExit();
        }

        // At this point the process has exited; the stream tasks will complete.
        var stdout = stdoutTask.GetAwaiter().GetResult();
        var stderr = stderrTask.GetAwaiter().GetResult();

        return (stdout, stderr, timedOut ? -1 : process.ExitCode, timedOut);
    }

    private static string FindHostExe()
    {
        // Allow callers to point the harness at an AOT-published Host (which
        // lives under a `publish` directory, not the standard build output)
        // or any other custom build. This lets the same MSTest harness validate
        // the AOT binary that the developer is actually trying to ship.
        var overrideExe = Environment.GetEnvironmentVariable("REACTOR_SELFTEST_HOST_EXE");
        if (!string.IsNullOrWhiteSpace(overrideExe))
        {
            if (!File.Exists(overrideExe))
                throw new FileNotFoundException(
                    $"REACTOR_SELFTEST_HOST_EXE points at a path that does not exist: {overrideExe}");
            return overrideExe;
        }

        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "Reactor.slnx")))
            dir = Path.GetDirectoryName(dir);

        if (dir == null)
            throw new DirectoryNotFoundException("Could not find repo root (Reactor.slnx)");

        var platform = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "ARM64",
            _ => "x64"
        };

        var exe = Path.Combine(dir, "tests", "Reactor.AppTests.Host", "bin", platform,
            "Debug", "net10.0-windows10.0.22621.0", "Reactor.AppTests.Host.exe");

        if (!File.Exists(exe))
            throw new FileNotFoundException($"Host app not built. Expected: {exe}");

        return exe;
    }
}
