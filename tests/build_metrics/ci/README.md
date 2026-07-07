# Build metrics (artifact size-diff) PR comment

An automatic, self-updating PR comment that reports how much each shipped Reactor
artifact grew or shrank versus `main`. Informational only — it never fails a PR.

Example:

> ## 📦 Build metrics
>
> Artifact sizes for `deadbee` vs `main` (`cafef00`).
>
> ### Packages (compressed .nupkg)
>
> | Artifact | main | PR | Δ | |
> |---|--:|--:|--:|:-:|
> | Microsoft.UI.Reactor.nupkg | 1.94 MB | 1.95 MB | +8.4 KB (+0.42%) | ⚠️ |
>
> ### Assemblies (uncompressed)
>
> | Artifact | main | PR | Δ | |
> |---|--:|--:|--:|:-:|
> | Reactor.dll | 3.16 MB | 3.16 MB | +0 B (0.00%) | ≈ |

## What is measured

For each shipped package the report tracks two numbers:

| Group | What | Why |
|---|---|---|
| **Packages (compressed .nupkg)** | the `.nupkg` file size | the download a consumer actually pays for |
| **Assemblies (uncompressed)** | the primary DLL inside `lib/<tfm>/` | the real "did our code grow" signal, unaffected by zip compression noise |

Tracked packages (see `$targets` in `Measure-BuildMetrics.ps1`):

- `Microsoft.UI.Reactor` → `Reactor.dll`
- `Microsoft.UI.Reactor.Advanced` → `Reactor.Advanced.dll`
- `Microsoft.UI.Reactor.Devtools` → `Microsoft.UI.Reactor.Devtools.dll`

Adding a package is a one-line entry in that `$targets` array.

## Files

| File | Role |
|---|---|
| `BuildMetricsLib.ps1` | **Pure** helpers (no filesystem side effects): byte formatting, `Get-SizeDelta`, and the sticky-comment renderer. Unit-testable headless. |
| `BuildMetricsLib.Tests.ps1` | Dependency-free assertions for the pure lib. Exits non-zero on failure. |
| `Measure-BuildMetrics.ps1` | Orchestrator: `dotnet pack` each package in a source tree and emit a `sizes.json` of measurements. |

## Workflows

Two workflows implement the standard secure `pull_request` + `workflow_run`
split, so untrusted PR build code never holds a write token:

| Workflow | Trigger | Privilege | Job |
|---|---|---|---|
| `.github/workflows/build-metrics.yml` | `pull_request` (+ manual `workflow_dispatch`) | read-only | Builds + packs the PR head **and** base, measures both, renders the comment, uploads it as an artifact. Runs untrusted PR build code. |
| `.github/workflows/build-metrics-comment.yml` | `workflow_run` | `pull-requests: write` | Downloads the artifact and posts/updates the sticky comment. Runs **no** PR code. Resolves the target PR from the trusted `workflow_run` head SHA, never the artifact. |
| `.github/workflows/build-metrics-lib-tests.yml` | `pull_request` / `push` on `tests/build_metrics/ci/**` | read-only | Fast headless run of `BuildMetricsLib.Tests.ps1`. |

The sticky comment is found + updated in place via a hidden marker
(`<!-- reactor-build-metrics -->`), the same convention as
`tests/stress_perf/ci/PerfLib.ps1`.

## Local runbook

Unit-test the renderer (seconds, no build):

```pwsh
pwsh tests/build_metrics/ci/BuildMetricsLib.Tests.ps1
```

Measure the current tree and render a comment against a baseline:

```pwsh
# Measure this working tree.
pwsh tests/build_metrics/ci/Measure-BuildMetrics.ps1 -Root . -OutFile head.sizes.json

# Measure another checkout/worktree (e.g. main) the same way -> base.sizes.json,
# then render:
. tests/build_metrics/ci/BuildMetricsLib.ps1
$head = Get-Content head.sizes.json -Raw | ConvertFrom-Json
$base = Get-Content base.sizes.json -Raw | ConvertFrom-Json
Format-BuildMetricsComment -BaseMeasurements $base -HeadMeasurements $head -HeadSha HEAD -BaseSha main
```

## Notes

- A fixed package version (`0.0.0-buildmetrics`) is used for both sides so the
  version string embedded in the `.nuspec` never contributes to the diff.
- A small noise band (64 B **and** 0.05%, both must clear) keeps `.nupkg` zip
  jitter from rendering as a spurious regression.
- Growth is the regression direction for every artifact, so a shrink is flagged
  as the improvement (✅) and growth as the regression (⚠️).
- Not yet tracked (candidate future `$targets` entries): the NativeAOT-published
  `hello-world-aot.exe` — the highest-value size signal for this AOT/trim-focused
  repo — and the `mur` CLI publish. Left out of the initial cut to keep the double
  build fast and reliable; both slot in as additional targets.
