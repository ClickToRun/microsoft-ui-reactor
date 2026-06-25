# Perf comparison (`/perf`) — CI workflow + local runner

On-demand benchmarking for the Reactor data-grid stress harness. The same
PowerShell entry point powers two things:

- **In CI** — comment **`/perf`** on a pull request and
  [`.github/workflows/perf-compare.yml`](../../../.github/workflows/perf-compare.yml)
  builds the harness on the **PR head** and on **`main`**, runs them
  interleaved on one runner, and posts a sticky comparison comment.
- **Locally** — run [`Run-PerfBenchmark.ps1`](Run-PerfBenchmark.ps1) yourself to
  get your four numbers (and an optional A/B against a clean `main` worktree)
  before you ever push.

> There is also a Copilot skill — [`perf-compare`](../../../.github/skills/perf-compare/SKILL.md) —
> that drives the local runner for you. Just ask Copilot to "benchmark my perf
> changes vs main".

## The four metrics

Measured on the `StressPerf.ReactorOptimized` StocksGrid workload, x64 Release:

| Metric | Meaning | Direction |
|---|---|:--:|
| **Renders/sec** | `Total Renders` ÷ `Duration` — render throughput | higher is better ↑ |
| **Avg Reconcile (ms)** | mean reconcile-phase time per render | lower is better ↓ |
| **Avg Diff (ms)** | mean element-tree diff time per render | lower is better ↓ |
| **Avg Memory (MB)** | mean working set during the measured window | lower is better ↓ |

Imperative WinUI3 (`StressPerf.Direct`) has no virtual-DOM, so it has **no**
reconcile/diff phase — those cells read *n/a*.

## Prerequisites

- **Windows** with a real interactive desktop session (the harness opens a real
  WinUI window — see [Troubleshooting](#troubleshooting)).
- **.NET 10 SDK** (`dotnet --version` ≥ 10).
- **PowerShell 7+** (`pwsh`). The scripts use pwsh-7 syntax (`??`, `(if …)`
  sub-expressions) and will not run under Windows PowerShell 5.1.
- **No Windows App SDK runtime install needed.** The runner builds the harness
  with `WindowsAppSDKSelfContained=true` (via the gated `-p:PerfCiSelfContained=true`
  property), the same hermetic trick the WinUI selftest hosts use, so the
  bundled runtime ships next to the exe. Pass `-SelfContained:$false` to use a
  machine-wide runtime instead.

## Run it locally

### Quick: my four numbers right now

```pwsh
pwsh tests/stress_perf/ci/Run-PerfBenchmark.ps1
```

Builds + runs `StressPerf.ReactorOptimized` **and** `StressPerf.Direct`
(vanilla WinUI3) in the current checkout, prints a console table, and writes
`tests/stress_perf/ci/out/result.json`. The table also shows the static Rust
`windows-reactor` reference column for context.

### A/B against a clean `main` baseline

Create a worktree on `main`, then point `-BaselineRoot` at it. This switches the
script into **compare mode**: it interleaves the PR-tree and main-tree
`ReactorOptimized` runs on the same machine, runs WinUI3 once, and renders the
exact sticky comment (`out/comment.md`) the CI workflow would post.

```pwsh
git worktree add ../main origin/main
pwsh tests/stress_perf/ci/Run-PerfBenchmark.ps1 -BaselineRoot ../main -Reps 3
# when done:
git worktree remove ../main
```

### Parameters

| Parameter | Default | Purpose |
|---|---|---|
| `-Root` | repo root | Checkout to build + run (the **PR head** in compare mode). |
| `-BaselineRoot` | _(unset)_ | A second checkout (the **`main`** baseline). Setting it enables **compare mode** and renders `comment.md`. |
| `-Percent` | `50` | Fraction of grid cells mutated per tick (methodology default). |
| `-Duration` | `10` | Measured seconds per run (methodology default). |
| `-Reps` | `2` | Measured runs whose **median** is reported (methodology "median of two"). Bump for tighter numbers. |
| `-Warmup` | `1` | Leading runs discarded before the measured `Reps`. |
| `-Apps` | `ReactorOptimized,Direct` | Single-tree mode only: which harnesses to run. |
| `-SelfContained` | `$true` | Build with the bundled WinApp runtime (no machine-wide install). |
| `-SkipBuild` | off | Reuse existing binaries (skip `dotnet build`). |
| `-PinAffinity` | off | Pin each run to one CPU core (can hurt on small runners). |
| `-OutDir` | `ci/out` | Where logs, `result.json` and `comment.md` land. |
| `-HeadSha` / `-BaseSha` / `-RunUrl` | _(empty)_ | Context echoed into the comment footer (compare mode). |

### Outputs

- **Console table** — median per metric, per app, plus the Rust reference column.
- **`out/result.json`** — machine-readable medians + run-to-run spread + runner identity.
- **`out/comment.md`** _(compare mode only)_ — the rendered sticky comment.
- **`out/*.log`** — per-build and per-run stdout/stderr for debugging.

## How `/perf` works in CI

1. A trusted author (**OWNER / MEMBER / COLLABORATOR**, checked via
   `github.event.comment.author_association`) comments **`/perf`** on a PR.
   `workflow_dispatch` with a `pr_number` input is also supported for manual runs.
2. The workflow runs from the **default branch** (that is how `issue_comment`
   behaves), so once this is on `main` it works on **every already-open PR with
   no rebase** — important while a fleet of perf PRs is in flight.
3. It checks out the default branch (trusted perf scripts + the `main` baseline),
   sets up .NET 10, fetches the PR head via `refs/pull/N/head` into a worktree
   (so forks work), then runs `Run-PerfBenchmark.ps1` in compare mode.
4. It posts — or **updates in place** on re-runs, via the hidden
   `<!-- reactor-perf-compare -->` marker — one sticky comment.

Only the harness *code* comes from the PR; the perf scripts and the `main`
baseline always come from the trusted default branch. The `author_association`
gate is the security control, because the job has a write token.

### The comment

Two tables plus footnotes:

- **Regression vs `main`** — `Metric | main | This PR | Δ% | Status`, where
  Status is direction-aware (`✅ improvement` / `⚠️ regression` / `≈ within
  noise`). "Within noise" means `|Δ|` is below the larger of the run-to-run
  spread and a 4% floor — treat it as no measurable change.
- **Cross-framework reference** — `vanilla WinUI3 | Rust windows-reactor |
  Reactor (this PR)` on the same StocksGrid workload. The Rust column is a
  **static** point-in-time snapshot cited from
  [`microsoft/windows-rs` `windows-reactor.md`](https://github.com/microsoft/windows-rs/blob/master/docs/crates/windows-reactor.md#performance-notes)
  (different machine, x64 Release, `--percent 50 --duration 10`, median of 2,
  80×60 grid) — a cross-language reference, **not** a runner-local measurement.
  The WinUI3 column **is** measured live on the runner.

## Variance: trust the delta, not the absolutes

GitHub-hosted runners are shared and heterogeneous — absolute numbers drift
between runs and machines. We do not own them, so the design leans on
**relative** measurement and several mitigations:

- **Same-runner A/B** — PR and `main` are built and measured on the *same*
  machine in the *same* job, so machine-class differences cancel.
- **Interleaving** — runs alternate `main, PR, main, PR, …` so slow time
  windows hit both sides roughly equally.
- **Warm-up discard + median of N** — the first run(s) are dropped; the median
  rejects single-run outliers.
- **Noise band** — a delta smaller than the larger of the measured run-to-run
  spread and a 4% floor is reported as *within noise*, not a win/regression.
- **Process priority + power plan** — runs use High priority and a
  high-performance power plan (restored afterward), with a best-effort Defender
  exclusion on the output tree.
- **Runner identity** — CPU / cores / RAM are recorded in the comment so the
  absolute numbers are read in context.

For the steadiest local numbers: close other apps, stay on AC power, and bump
`-Reps` (e.g. `-Reps 5`).

## Files here

| File | Role |
|---|---|
| [`Run-PerfBenchmark.ps1`](Run-PerfBenchmark.ps1) | Orchestrator — build, run, interleave, render. Used by both the workflow and humans. |
| [`PerfLib.ps1`](PerfLib.ps1) | Pure, side-effect-free helpers (parse, median, spread, direction-aware delta, comment renderer). Holds the Rust reference constants + comment marker. |

## Troubleshooting

- **Run crashes with `0xC000027B` right after "MountAndActivate ok".** That is a
  stowed XAML/compositor exception: the box cannot composite a real WinUI window
  (headless server, no GPU/desktop session, or an RDP session without
  composition). The **build and runtime are fine** — `windows-latest` CI runners
  composite XAML correctly (the selftest/E2E jobs prove it). Run locally from an
  interactive desktop session.
- **`exe … not found`.** The self-contained build nests the exe under a RID
  folder (`bin\x64\Release\<tfm>\win-x64\`); the script finds it recursively. If
  it is genuinely missing, check the `out/build-*.log` for the real `dotnet`
  error.
- **Scripts won't parse.** Use `pwsh` (PowerShell 7+), not `powershell.exe` 5.1.
