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

Measured on the `StressPerf.ReactorOptimized` StocksGrid workload (Release, built for the host architecture — x64 on the CI runner):

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
- **Any architecture.** The harness builds for your **host architecture** by
  default (`x64` or `ARM64`), so it runs natively. This matters on ARM64 boxes:
  an x64 build there runs under emulation and crashes WinUI composition with a
  stowed exception (`0xC000027B`). Override with `-Platform x64|ARM64` if needed.

## Run it locally

### Quick: my four numbers right now

```pwsh
pwsh tests/stress_perf/ci/Run-PerfBenchmark.ps1
```

Builds + runs `StressPerf.ReactorOptimized` **and** `StressPerf.Direct`
(vanilla WinUI3) in the current checkout, prints a console table, and writes
`tests/stress_perf/ci/out/result.json`. Both build for your host architecture.
Add a live Rust `windows-reactor` column with `-RustRepo <windows-rs checkout>`
(see [Parameters](#parameters)).

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
| `-Platform` | host arch | Target architecture (`x64` or `ARM64`). Defaults to your machine's native arch so the WinUI harness runs without emulation. |
| `-SelfContained` | `$true` | Build with the bundled WinApp runtime (no machine-wide install). |
| `-SkipBuild` | off | Reuse existing binaries (skip `dotnet build`). |
| `-PinAffinity` | off | Pin each run to one CPU core (can hurt on small runners). |
| `-RustRepo` | _(unset)_ | Path to a [`microsoft/windows-rs`](https://github.com/microsoft/windows-rs) checkout. Builds + runs its `reactor_perf` harness to add a **live** Rust column (best-effort). |
| `-DefenderExclude` | off | Add a temporary Defender exclusion on the output tree (removed afterward). CI-only; opt-in locally. |
| `-OutDir` | `ci/out` | Where logs, `result.json` and `comment.md` land. |
| `-HeadSha` / `-BaseSha` / `-RunUrl` | _(empty)_ | Context echoed into the comment footer (compare mode). |

### Outputs

- **Console table** — median per metric, per app (plus a live Rust column when `-RustRepo` is set).
- **`out/result.json`** — machine-readable medians + run-to-run spread + runner identity.
- **`out/comment.md`** _(compare mode only)_ — the rendered sticky comment.
- **`out/*.log`** — per-build and per-run stdout/stderr for debugging.

## How `/perf` works in CI

1. A trusted author (**OWNER / MEMBER / COLLABORATOR**, checked via
   `github.event.comment.author_association`) comments **`/perf`** on a PR.
   `workflow_dispatch` with a `pr_number` input is also supported for manual runs.
2. The workflow runs from the **default branch** (that is how `issue_comment`
   behaves), so once this is on `main` it works on **every already-open PR with
   no rebase** — important while a fleet of perf PRs is in flight. To honour that
   promise even for PRs opened *before* the gate landed (whose tree predates the
   self-contained csproj block), `Run-PerfBenchmark.ps1` overlays the harness
   `.csproj` from the trusted baseline over the PR tree's copy before building
   (compare mode only), restoring it afterwards — see below.
3. It checks out the default branch (trusted perf scripts + the `main` baseline),
   sets up .NET 10, fetches the PR head via `refs/pull/N/head` into a worktree
   (so forks work), then runs `Run-PerfBenchmark.ps1` in compare mode.
4. It posts — or **updates in place** on re-runs, via the hidden
   `<!-- reactor-perf-compare -->` marker — one sticky comment.

In compare mode the PR tree supplies everything it normally would — `src/Reactor/`
(the code under measurement) **and** the harness/workload sources under
`tests/stress_perf/` — *except* the harness `.csproj` build recipe, which
`Build-Harness` overlays from the trusted baseline tree for the duration of the
build and then restores. That `.csproj` is fixed test scaffolding (the StocksGrid
build recipe, including the `PerfCiSelfContained` self-contained knob), not a
perf-sensitive input; sourcing only it from baseline guarantees the self-contained
build works regardless of how old the PR is, while the PR's actual `src/Reactor/`
change is still compiled in via the harness's relative `ProjectReference`. (A PR
that deliberately edits the harness *sources* still has those changes measured —
only the project file comes from baseline.) The perf scripts and the `main`
baseline also come from the trusted default branch. The `author_association` gate
is the security control, because the job has a write token.

### The comment

Two tables plus footnotes:

- **Regression vs `main`** — `Metric | main | This PR | Δ% | Status`, where
  Status is direction-aware (`✅ improvement` / `⚠️ regression` / `≈ within
  noise`). "Within noise" means `|Δ|` is below the larger of the run-to-run
  spread and a 4% floor — treat it as no measurable change.
- **Cross-framework reference** — `vanilla WinUI3 | Rust windows-reactor |
  Reactor (this PR)` on the same StocksGrid workload, **all measured live on the
  same runner**. The Rust column builds and runs the
  [`microsoft/windows-rs`](https://github.com/microsoft/windows-rs) `reactor_perf`
  harness (a port of this workload), pinned in CI to a known-good commit. Because
  that harness is built self-contained (its `build.rs` is patched to
  `windows_reactor_setup::as_self_contained()`), the Windows App SDK runtime DLLs
  must sit next to `test_reactor_perf.exe` at process start — `Stage-RustRuntime`
  in `Run-PerfBenchmark.ps1` stages and verifies them explicitly so a silent
  staging miss can't surface as a `0xC0000135` load failure (issue #674). It is
  best-effort: if the Rust build, staging, or run fails the column reads `n/a` and
  the PR-vs-`main` comparison is unaffected. The WinUI3 column is the local
  `StressPerf.Direct` build.

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
| [`PerfLib.ps1`](PerfLib.ps1) | Pure, side-effect-free helpers (parse, median, spread, direction-aware delta, comment renderer) + the sticky-comment marker. |

## Troubleshooting

- **Run crashes with `0xC000027B` right after "MountAndActivate ok".** That is a
  stowed XAML/compositor exception. Most often the box cannot composite a real
  WinUI window (headless server, no GPU/desktop session, or an RDP session
  without composition) — run from an interactive desktop session. **On an ARM64
  machine** it also happens when an **x64** harness runs under emulation; the
  runner builds for your host architecture by default (so ARM64 runs natively),
  but if you forced `-Platform x64` on ARM64, drop it or pass `-Platform ARM64`.
  The build and runtime are otherwise fine — `windows-latest` CI runners
  composite XAML correctly (the selftest/E2E jobs prove it).
- **`exe … not found`.** The self-contained build nests the exe under an arch +
  RID folder (`bin\<arch>\Release\<tfm>\win-<arch>\`); the script finds it
  recursively. If it is genuinely missing, check the `out/build-*.log` for the
  real `dotnet` error.
- **Scripts won't parse.** Use `pwsh` (PowerShell 7+), not `powershell.exe` 5.1.
