<#
.SYNOPSIS
    Build + run the StressPerf WinUI harnesses, capture the four headline perf
    metrics, and (in compare mode) render the sticky PR-comparison comment.

.DESCRIPTION
    Used both locally (developers) and by .github/workflows/perf-compare.yml.

    Two modes, auto-selected:

      * Local / single-tree (no -BaselineRoot): builds and runs the requested
        harness(es) in -Root, prints a results table to the console, and writes
        result.json. Good for "what are my four numbers right now, and how do
        they line up against vanilla WinUI3 and the Rust reference?".

      * Compare (with -BaselineRoot): builds + runs StressPerf.ReactorOptimized
        in BOTH trees, INTERLEAVED on the same machine (main, PR, main, PR, ...)
        to cancel time-correlated drift, plus vanilla WinUI3 once, then renders
        the two-table sticky comment to comment.md for the workflow to post.

    Variance mitigations (we don't own CI runners): same-runner A/B, interleaved
    reps, warm-up discard, median of N, a noise band on the deltas (PerfLib),
    High process priority, a high-performance power plan for the duration, and an
    opt-in Defender exclusion (-DefenderExclude). Runner identity (CPU / cores /
    RAM) is recorded in the comment so absolute numbers are interpreted in
    context — trust the delta, not the absolutes.

    The Rust `windows-reactor` cross-framework column is measured live when
    -RustRepo points at a microsoft/windows-rs checkout (its `test_reactor_perf`
    crate is a port of this harness with the same CLI + report). Without it, the
    Rust column reads n/a.

.PARAMETER Root
    Checkout to build + run (the PR head in compare mode). Defaults to the repo
    root inferred from this script's location.

.PARAMETER BaselineRoot
    Second checkout (the `main` baseline). When set, the script runs in compare
    mode and renders comment.md.

.PARAMETER Percent
    Fraction of grid cells mutated per tick. Methodology default 50.

.PARAMETER Duration
    Measured seconds per run. Methodology default 10.

.PARAMETER Reps
    Measured runs whose median is reported (default 2 — methodology "median of
    two"). Bump locally for tighter numbers.

.PARAMETER Warmup
    Leading runs discarded before the Reps measured runs (default 1).

.PARAMETER Apps
    Which harnesses to run in single-tree mode: ReactorOptimized, Direct.
    Ignored in compare mode (which always does ReactorOptimized both sides +
    Direct once for the WinUI3 column).

.PARAMETER OutDir
    Where logs, comment.md and result.json land. Defaults to ci\out next to this
    script.

.PARAMETER SkipBuild
    Reuse existing binaries (skip dotnet build).

.PARAMETER SelfContained
    Build the harness self-contained (WindowsAppSDKSelfContained=true + the
    matching win-x64 / win-arm64 RID) so no machine-wide Windows App SDK runtime
    install is needed. Default $true. Disable with -SelfContained:$false.

.PARAMETER Platform
    Target architecture (x64 or ARM64). Defaults to the host's native
    architecture, so an ARM64 box builds and runs the harness natively instead
    of x64-under-emulation — emulated WinUI composition crashes with a stowed
    exception (0xC000027B). GitHub-hosted runners are x64, so CI builds x64.

.PARAMETER PinAffinity
    Pin each harness to a single CPU core (opt-in; can hurt on busy 2-core
    runners, off by default).

.PARAMETER RustRepo
    Path to a microsoft/windows-rs checkout. When set, the script builds + runs
    its `test_reactor_perf` crate (cargo, release) and fills the Rust
    cross-framework column with a live measurement. Omit to leave it n/a.

.PARAMETER DefenderExclude
    Opt in to a best-effort Microsoft Defender exclusion on -Root for the run
    (restored on exit). Off by default; intended for ephemeral CI runners, not
    developer machines.

.PARAMETER HeadSha
    PR head SHA echoed into the comment footer (compare mode).

.PARAMETER BaseSha
    Baseline (`main`) SHA echoed into the comment footer (compare mode).

.PARAMETER RunUrl
    Workflow run URL linked in the comment footer (compare mode).

.EXAMPLE
    # Local: my four numbers + cross-framework reference
    pwsh tests/stress_perf/ci/Run-PerfBenchmark.ps1

.EXAMPLE
    # Local A/B vs a clean main worktree
    git worktree add ../main origin/main
    pwsh tests/stress_perf/ci/Run-PerfBenchmark.ps1 -BaselineRoot ../main -Reps 3

.EXAMPLE
    # Local A/B + a live Rust column from a windows-rs checkout
    pwsh tests/stress_perf/ci/Run-PerfBenchmark.ps1 -BaselineRoot ../main -RustRepo ../windows-rs
#>
[CmdletBinding()]
param(
    [string]$Root,
    [string]$BaselineRoot = '',
    [double]$Percent = 50,
    [int]$Duration = 10,
    [int]$Reps = 2,
    [int]$Warmup = 1,
    [ValidateSet('ReactorOptimized', 'Direct')]
    [string[]]$Apps = @('ReactorOptimized', 'Direct'),
    [string]$OutDir,
    [switch]$SkipBuild,
    [bool]$SelfContained = $true,
    [ValidateSet('x64', 'ARM64')]
    [string]$Platform = $(if ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq 'Arm64') { 'ARM64' } else { 'x64' }),
    [switch]$PinAffinity,
    [string]$RustRepo = '',
    [switch]$DefenderExclude,
    [string]$HeadSha = '',
    [string]$BaseSha = '',
    [string]$RunUrl = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'PerfLib.ps1')

if (-not $Root)   { $Root = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path }
if (-not $OutDir) { $OutDir = Join-Path $PSScriptRoot 'out' }
$Root = (Resolve-Path $Root).Path
if ($BaselineRoot) { $BaselineRoot = (Resolve-Path $BaselineRoot).Path }
New-Item -ItemType Directory -Path $OutDir -Force | Out-Null

$Compare = [bool]$BaselineRoot
$tfmGuess = 'net10.0-windows10.0.22621.0'

$AppRegistry = @{
    ReactorOptimized = @{ AppName = 'StressPerf.ReactorOptimized'; ProjectRel = 'tests\stress_perf\StressPerf.ReactorOptimized\StressPerf.ReactorOptimized.csproj' }
    Direct           = @{ AppName = 'StressPerf.Direct';           ProjectRel = 'tests\stress_perf\StressPerf.Direct\StressPerf.Direct.csproj' }
}

function Write-Log {
    param([string]$Message, [string]$Color = 'Gray')
    $ts = (Get-Date).ToString('HH:mm:ss')
    Write-Host "[$ts] $Message" -ForegroundColor $Color
}

function Get-RunnerInfo {
    $info = [ordered]@{ Cpu = ''; Cores = [Environment]::ProcessorCount; MemoryGB = ''; Runner = $env:RUNNER_NAME }
    try { $info.Cpu = (Get-CimInstance Win32_Processor -ErrorAction Stop | Select-Object -First 1).Name.Trim() } catch {}
    try { $info.MemoryGB = [math]::Round((Get-CimInstance Win32_ComputerSystem -ErrorAction Stop).TotalPhysicalMemory / 1GB) } catch {}
    return [pscustomobject]$info
}

function Resolve-HarnessExe {
    param([string]$TreeRoot, [hashtable]$AppMeta)
    $projDir = Split-Path (Join-Path $TreeRoot $AppMeta.ProjectRel)
    $binRoot = Join-Path $projDir "bin\$Platform\Release"
    if (-not (Test-Path $binRoot)) { return $null }
    $candidates = @(Get-ChildItem -Path $binRoot -Recurse -Filter ("{0}.exe" -f $AppMeta.AppName) -ErrorAction SilentlyContinue)
    if ($SelfContained) {
        # Prefer the RID-specific (self-contained) output; a stale framework-dependent
        # exe from an earlier build can otherwise win on LastWriteTime and fail to launch.
        $ridDir = if ($Platform -eq 'ARM64') { 'win-arm64' } else { 'win-x64' }
        $rid = @($candidates | Where-Object { $_.FullName -match "\\$ridDir\\" })
        if ($rid.Count) { $candidates = $rid }
    }
    $exe = $candidates | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($exe) { return $exe.FullName }
    return $null
}

function Build-Harness {
    param([string]$TreeRoot, [hashtable]$AppMeta)
    $proj = Join-Path $TreeRoot $AppMeta.ProjectRel
    if (-not (Test-Path $proj)) { throw "Project not found: $proj" }
    Write-Log "build $($AppMeta.AppName)  [$TreeRoot]" 'Cyan'

    # Compare mode only: overlay the harness .csproj from the trusted baseline tree
    # over the PR tree's copy before building. The harness csproj is fixed test
    # scaffolding — the build recipe for the StocksGrid workload, including the
    # PerfCiSelfContained self-contained knob — NOT the code under measurement. The
    # PR's actual perf change lives in src/Reactor/, which the harness still compiles
    # via its relative ProjectReference into the PR tree, so overlaying only the
    # csproj is fair. This guarantees the self-contained build block is present even
    # for PRs opened before the gate landed (whose tree predates that csproj block),
    # so /perf needs no rebase. Never runs in local single-tree mode ($BaselineRoot
    # empty) or when building the baseline itself ($TreeRoot -eq $BaselineRoot).
    if ($BaselineRoot -and ($TreeRoot -ne $BaselineRoot)) {
        $trusted = Join-Path $BaselineRoot $AppMeta.ProjectRel
        if (Test-Path $trusted) {
            Copy-Item -LiteralPath $trusted -Destination $proj -Force
            Write-Log "  overlaid trusted csproj (self-contained knob) from baseline" 'DarkGray'
        } else {
            Write-Log "  trusted csproj not found in baseline ($trusted) — using PR tree copy" 'Yellow'
        }
    }

    $log = Join-Path $OutDir ("build-{0}-{1}.log" -f $AppMeta.AppName, ([IO.Path]::GetFileName($TreeRoot)))
    $buildArgs = @($proj, '-c', 'Release', "-p:Platform=$Platform", '--nologo')
    if ($SelfContained) { $buildArgs += '-p:PerfCiSelfContained=true' }
    & dotnet build @buildArgs 2>&1 | Tee-Object -FilePath $log | Out-Null
    if ($LASTEXITCODE -ne 0) {
        Write-Host (Get-Content $log -Raw) -ForegroundColor DarkRed
        throw "dotnet build failed for $($AppMeta.AppName) in $TreeRoot (see $log)"
    }
}

function Build-RustHarness {
    <# Build the windows-rs `test_reactor_perf` crate (release) for the Rust column.
       Bounded by -TimeoutSec so a slow/stuck cargo build can never consume the
       whole job or starve the C# comparison (the Rust leg is best-effort). #>
    param([string]$RepoRoot, [int]$TimeoutSec = 1500)
    Write-Log "build Rust test_reactor_perf  [$RepoRoot] (timeout ${TimeoutSec}s)" 'Cyan'
    $log = Join-Path $OutDir 'build-rust.out.log'
    $err = Join-Path $OutDir 'build-rust.err.log'
    $p = Start-Process -FilePath 'cargo' -ArgumentList @('build', '--release', '-p', 'test_reactor_perf') `
        -WorkingDirectory $RepoRoot -PassThru -NoNewWindow `
        -RedirectStandardOutput $log -RedirectStandardError $err
    if (-not $p.WaitForExit($TimeoutSec * 1000)) {
        try { $p.Kill($true) } catch { try { $p.Kill() } catch {} }
        throw "cargo build for test_reactor_perf exceeded ${TimeoutSec}s — aborted"
    }
    if ($p.ExitCode -ne 0) {
        if (Test-Path $err) { Write-Host (Get-Content $err -Raw) -ForegroundColor DarkRed }
        throw "cargo build failed for test_reactor_perf in $RepoRoot (exit=$($p.ExitCode); see $log / $err)"
    }
}

function Resolve-RustExe {
    param([string]$RepoRoot)
    $exe = Join-Path $RepoRoot 'target\release\test_reactor_perf.exe'
    if (Test-Path $exe) { return (Resolve-Path $exe).Path }
    return $null
}

function Invoke-RustLeg {
    <# Build + run the windows-rs `test_reactor_perf` crate for the Rust column.
       Best-effort: any failure logs a warning and yields $null (column reads n/a). #>
    if (-not $RustRepo) { return $null }
    if (-not (Test-Path $RustRepo)) { Write-Log "RustRepo '$RustRepo' not found — Rust column n/a" 'Yellow'; return $null }
    try {
        if (-not $SkipBuild) { Build-RustHarness -RepoRoot $RustRepo }
        $rustExe = Resolve-RustExe -RepoRoot $RustRepo
        if (-not $rustExe) { Write-Log "test_reactor_perf.exe not found after build — Rust column n/a" 'Yellow'; return $null }
        Write-Log "Rust windows-reactor (test_reactor_perf)" 'Green'
        # The Rust port writes StressPerf.Reactor.report.txt next to its exe and has
        # no --json mode, so run with -NoJson and read the report.
        $rustRuns = Measure-Sequential -Exe $rustExe -AppMeta @{ AppName = 'StressPerf.Reactor' } -Tag 'rust' -NoJson
        if ($rustRuns.Count) { return Measure-PerfRuns -Runs $rustRuns }
        Write-Log "Rust harness produced no metrics — Rust column n/a" 'Yellow'
        return $null
    } catch {
        Write-Log "Rust leg failed ($($_.Exception.Message)) — Rust column n/a" 'Yellow'
        return $null
    }
}

function Invoke-OneRun {
    <# Run the harness once; return a metric object (Read-HarnessMetrics) or $null. #>
    param([string]$Exe, [hashtable]$AppMeta, [int]$Index, [string]$Tag, [switch]$NoJson)

    $exeDir = Split-Path $Exe
    foreach ($ext in 'metrics.json', 'report.txt', 'samples.csv') {
        $p = Join-Path $exeDir ("{0}.{1}" -f $AppMeta.AppName, $ext)
        if (Test-Path $p) { Remove-Item $p -Force -ErrorAction SilentlyContinue }
    }

    $stdout = Join-Path $OutDir ("run-{0}-{1}-{2}.out.log" -f $AppMeta.AppName, $Tag, $Index)
    $stderr = Join-Path $OutDir ("run-{0}-{1}-{2}.err.log" -f $AppMeta.AppName, $Tag, $Index)
    $inv = [System.Globalization.CultureInfo]::InvariantCulture
    # The Rust port has no --json mode; it always writes report.txt. C# harnesses get --json.
    $harnessArgs = @('--headless', '--percent', $Percent.ToString($inv), '--duration', $Duration.ToString($inv))
    if (-not $NoJson) { $harnessArgs += '--json' }
    $timeoutSec = $Duration + 90

    Write-Log ("  run [{0} #{1}] {2} --percent {3} --duration {4}" -f $Tag, $Index, $AppMeta.AppName, $Percent, $Duration)
    $proc = Start-Process -FilePath $Exe -ArgumentList $harnessArgs -PassThru `
        -RedirectStandardOutput $stdout -RedirectStandardError $stderr -WindowStyle Hidden
    try {
        $proc.PriorityClass = [System.Diagnostics.ProcessPriorityClass]::High
        if ($PinAffinity) { $proc.ProcessorAffinity = [IntPtr]([int64]1 -shl (($Index - 1) % [Environment]::ProcessorCount)) }
    } catch {}

    if (-not $proc.WaitForExit($timeoutSec * 1000)) {
        Write-Log "  TIMEOUT after ${timeoutSec}s — killing $($AppMeta.AppName)" 'Yellow'
        try { $proc.Kill($true) } catch { try { $proc.Kill() } catch {} }
        Start-Sleep -Seconds 2
    }

    $metrics = Read-HarnessMetrics -Directory $exeDir -AppName $AppMeta.AppName
    if ($metrics.Source -eq 'none') {
        Write-Log "  no metrics for $($AppMeta.AppName) run #$Index (exit=$($proc.ExitCode)). stderr tail:" 'Yellow'
        if (Test-Path $stderr) { Get-Content $stderr -Tail 8 | ForEach-Object { Write-Host "      $_" -ForegroundColor DarkYellow } }
        return $null
    }
    Write-Log ("  -> renders/sec={0}  reconcile={1}  diff={2}  mem={3}  ({4})" -f `
        (Format-PerfNumber $metrics.RendersPerSec 2), (Format-PerfNumber $metrics.AvgReconcileMs 1), `
        (Format-PerfNumber $metrics.AvgDiffMs 1), (Format-PerfNumber $metrics.AvgMemoryMB 1), $metrics.Source) 'DarkGray'
    return $metrics
}

function Measure-Sequential {
    param([string]$Exe, [hashtable]$AppMeta, [string]$Tag, [switch]$NoJson)
    $runs = @()
    for ($i = 1; $i -le ($Warmup + $Reps); $i++) {
        $m = Invoke-OneRun -Exe $Exe -AppMeta $AppMeta -Index $i -Tag $Tag -NoJson:$NoJson
        if ($i -le $Warmup) { Write-Log "  (warmup #$i discarded)" 'DarkGray'; continue }
        if ($m) { $runs += $m }
    }
    return , $runs
}

# ── Power plan + Defender (best-effort, restored on exit) ────────────────────
$prevScheme = $null
try {
    $active = (& powercfg /getactivescheme) 2>$null
    if ($active -match '([0-9a-fA-F-]{36})') { $prevScheme = $Matches[1] }
    & powercfg /setactive SCHEME_MIN 2>$null | Out-Null   # High performance
    Write-Log "power plan -> High performance (was $prevScheme)" 'DarkGray'
} catch { Write-Log "power plan unchanged ($_)" 'DarkGray' }
if ($DefenderExclude) {
    try { Add-MpPreference -ExclusionPath $Root -ErrorAction Stop; Write-Log "Defender exclusion added for $Root" 'DarkGray' }
    catch { Write-Log "Defender exclusion skipped ($_)" 'DarkGray' }
}

$runner = Get-RunnerInfo
Write-Log ("runner: {0} | {1} cores | {2} GB | {3}" -f $runner.Cpu, $runner.Cores, $runner.MemoryGB, ($runner.Runner ?? 'local')) 'Cyan'
Write-Log ("mode: {0} | platform={1} | percent={2} duration={3} reps={4} warmup={5}" -f ($(if ($Compare) { 'COMPARE' } else { 'LOCAL' })), $Platform, $Percent, $Duration, $Reps, $Warmup) 'Cyan'

$exit = 0
try {
    if ($Compare) {
        # ---- Compare mode: interleaved ReactorOptimized A/B + WinUI3 once -----
        $ro = $AppRegistry.ReactorOptimized
        $direct = $AppRegistry.Direct

        if (-not $SkipBuild) {
            Build-Harness -TreeRoot $BaselineRoot -AppMeta $ro
            Build-Harness -TreeRoot $Root -AppMeta $ro
            Build-Harness -TreeRoot $Root -AppMeta $direct
        }
        $mainExe = Resolve-HarnessExe -TreeRoot $BaselineRoot -AppMeta $ro
        $prExe = Resolve-HarnessExe -TreeRoot $Root -AppMeta $ro
        $directExe = Resolve-HarnessExe -TreeRoot $Root -AppMeta $direct
        if (-not $mainExe) { throw "main ReactorOptimized exe not found under $BaselineRoot" }
        if (-not $prExe) { throw "PR ReactorOptimized exe not found under $Root" }

        Write-Log "interleaving main/PR ReactorOptimized ($($Warmup) warmup + $($Reps) measured each)" 'Green'
        $mainRuns = @(); $prRuns = @()
        for ($i = 1; $i -le ($Warmup + $Reps); $i++) {
            $mm = Invoke-OneRun -Exe $mainExe -AppMeta $ro -Index $i -Tag 'main'
            $pm = Invoke-OneRun -Exe $prExe -AppMeta $ro -Index $i -Tag 'pr'
            if ($i -le $Warmup) { Write-Log "  (warmup pair #$i discarded)" 'DarkGray'; continue }
            if ($mm) { $mainRuns += $mm }
            if ($pm) { $prRuns += $pm }
        }

        $winRuns = @()
        if ($directExe) {
            Write-Log "vanilla WinUI3 (StressPerf.Direct)" 'Green'
            $winRuns = Measure-Sequential -Exe $directExe -AppMeta $direct -Tag 'winui3'
        } else {
            Write-Log "StressPerf.Direct exe not found — WinUI3 column will read n/a" 'Yellow'
        }

        $rust = Invoke-RustLeg

        $main = Measure-PerfRuns -Runs $mainRuns
        $pr = Measure-PerfRuns -Runs $prRuns
        $winui3 = if ($winRuns.Count) { Measure-PerfRuns -Runs $winRuns } else { $null }

        $note = $null
        if ($prRuns.Count -eq 0 -or $mainRuns.Count -eq 0) {
            $note = 'One or both of the main/PR ReactorOptimized runs produced no metrics — the harness may have failed to open a window on this runner. See the workflow run log and the uploaded ``perf-logs`` artifact.'
            $exit = 1
        }
        else {
            $short = @()
            if ($mainRuns.Count -lt $Reps) { $short += "main $($mainRuns.Count)/$Reps" }
            if ($prRuns.Count -lt $Reps) { $short += "PR $($prRuns.Count)/$Reps" }
            if ($short.Count) {
                $note = "Some measured runs produced no metrics ($($short -join ', ')); the reported median uses fewer than $Reps samples, so treat the delta with extra caution. See the uploaded ``perf-logs`` artifact."
                $exit = 1
            }
        }

        $ctx = @{
            Percent = $Percent; Duration = $Duration; Reps = $Reps; Warmup = $Warmup
            Platform = $Platform
            MainSamples = $mainRuns.Count; PrSamples = $prRuns.Count
            BaseSha = $(if ($BaseSha) { $BaseSha.Substring(0, [Math]::Min(7, $BaseSha.Length)) } else { '' })
            HeadSha = $(if ($HeadSha) { $HeadSha.Substring(0, [Math]::Min(7, $HeadSha.Length)) } else { '' })
            Runner = $runner.Runner; Cpu = $runner.Cpu; Cores = $runner.Cores; MemoryGB = $runner.MemoryGB
            RunUrl = $RunUrl; Timestamp = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ'); Note = $note
        }
        $comment = Format-PerfComment -Main $main -Pr $pr -WinUI3 $winui3 -Rust $rust -Context $ctx
        $commentPath = Join-Path $OutDir 'comment.md'
        Set-Content -LiteralPath $commentPath -Value $comment -Encoding UTF8
        Write-Log "comment.md written -> $commentPath" 'Green'

        $result = [pscustomobject]@{ main = $main; pr = $pr; winui3 = $winui3; rust = $rust; runner = $runner; context = $ctx }
        $result | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $OutDir 'result.json') -Encoding UTF8

        Write-Host "`n----- comment.md -----" -ForegroundColor DarkGray
        Write-Host $comment
    }
    else {
        # ---- Local single-tree mode ------------------------------------------
        $aggs = [ordered]@{}
        foreach ($key in $Apps) {
            $meta = $AppRegistry[$key]
            if (-not $SkipBuild) { Build-Harness -TreeRoot $Root -AppMeta $meta }
            $exe = Resolve-HarnessExe -TreeRoot $Root -AppMeta $meta
            if (-not $exe) { Write-Log "exe for $key not found — skipping" 'Yellow'; continue }
            $runs = Measure-Sequential -Exe $exe -AppMeta $meta -Tag $key
            $aggs[$key] = Measure-PerfRuns -Runs $runs
        }

        $rust = Invoke-RustLeg
        if ($rust) { $aggs['Rust'] = $rust }

        Write-Host ""
        Write-Host "==== Perf results ($Platform, median of $Reps, $Warmup warmup) ====" -ForegroundColor Green
        $rows = foreach ($m in $script:PerfMetricSpec) {
            $row = [ordered]@{ Metric = ("{0} {1}" -f $m.Label, $(if ($m.LowerIsBetter) { '(lower better)' } else { '(higher better)' })) }
            foreach ($key in $aggs.Keys) { $row[$key] = Format-PerfNumber $aggs[$key].($m.Key) $m.Digits }
            [pscustomobject]$row
        }
        $rows | Format-Table -AutoSize | Out-String | Write-Host
        if ($RustRepo -and -not $rust) { Write-Host "Rust column n/a — see warnings above." -ForegroundColor DarkGray }
        elseif (-not $RustRepo) { Write-Host "Tip: pass -RustRepo <windows-rs checkout> to add a live Rust column." -ForegroundColor DarkGray }

        $result = [pscustomobject]@{ apps = $aggs; runner = $runner }
        $result | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $OutDir 'result.json') -Encoding UTF8
        Write-Log "result.json written -> $(Join-Path $OutDir 'result.json')" 'Green'
    }
}
finally {
    if ($prevScheme) { try { & powercfg /setactive $prevScheme 2>$null | Out-Null; Write-Log "power plan restored -> $prevScheme" 'DarkGray' } catch {} }
    if ($DefenderExclude) { try { Remove-MpPreference -ExclusionPath $Root -ErrorAction SilentlyContinue } catch {} }
}

exit $exit
