<#
.SYNOPSIS
    Dependency-free unit tests for PerfLib.ps1 (the pure parser/median/delta/
    renderer used by the /perf comparison workflow).

.DESCRIPTION
    Runs headless with no WinUI harness and no external test framework, so it is
    safe on any runner (it is wired into .github/workflows/perf-lib-tests.yml on
    changes under tests/stress_perf/ci/**). Exits non-zero if any assertion fails.

    Run locally:  pwsh tests/stress_perf/ci/PerfLib.Tests.ps1
#>
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'PerfLib.ps1')

$script:Pass = 0
$script:Fail = 0
$script:Failures = [System.Collections.Generic.List[string]]::new()

function Assert-Equal {
    param($Expected, $Actual, [string]$Message)
    if ($Expected -eq $Actual) {
        $script:Pass++
    } else {
        $script:Fail++
        $script:Failures.Add("$Message`n    expected: [$Expected]`n    actual:   [$Actual]")
    }
}

function Assert-Null {
    param($Actual, [string]$Message)
    if ($null -eq $Actual) { $script:Pass++ }
    else { $script:Fail++; $script:Failures.Add("$Message`n    expected: <null>`n    actual:   [$Actual]") }
}

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if ($Condition) { $script:Pass++ }
    else { $script:Fail++; $script:Failures.Add($Message) }
}

function Assert-Match {
    param([string]$Haystack, [string]$Needle, [string]$Message)
    if ($Haystack -like "*$Needle*") { $script:Pass++ }
    else { $script:Fail++; $script:Failures.Add("$Message`n    missing substring: [$Needle]") }
}

# ── Get-PerfMedian ───────────────────────────────────────────────────────────
Assert-Equal 2   (Get-PerfMedian @(3, 1, 2))        'median odd'
Assert-Equal 2.5 (Get-PerfMedian @(1, 2, 3, 4))     'median even'
Assert-Equal 5   (Get-PerfMedian @(5))              'median single'
Assert-Null      (Get-PerfMedian @())               'median empty -> null'
Assert-Equal 4   (Get-PerfMedian @(4, $null, 4))    'median ignores nulls'

# ── Get-PerfRelativeSpreadPct ────────────────────────────────────────────────
Assert-Equal 0    (Get-PerfRelativeSpreadPct @(10, 10)) 'spread identical -> 0'
Assert-Equal 18.2 (Get-PerfRelativeSpreadPct @(10, 12)) 'spread (2/11)%'
Assert-Equal 0    (Get-PerfRelativeSpreadPct @(7))      'spread single -> 0'
Assert-Equal 0    (Get-PerfRelativeSpreadPct @())       'spread empty -> 0'

# ── ConvertTo-PerfDouble ─────────────────────────────────────────────────────
Assert-Equal 8.7 (ConvertTo-PerfDouble '8.70')  'parse invariant decimal'
Assert-Equal 8.7 (ConvertTo-PerfDouble '8,70')  'parse comma-decimal culture'
Assert-Equal 12  (ConvertTo-PerfDouble '  12 ') 'parse trims whitespace'
Assert-Null      (ConvertTo-PerfDouble '')      'parse empty -> null'
Assert-Null      (ConvertTo-PerfDouble 'abc')   'parse junk -> null'

# ── Get-PerfDelta (direction-aware + noise band) ─────────────────────────────
$d = Get-PerfDelta -Baseline 10 -Candidate 12 -LowerIsBetter $false
Assert-Equal 20      $d.DeltaPct 'higher-better +20% delta'
Assert-Equal 'better' $d.Status  'higher-better improvement status'
Assert-True  $d.Improved         'higher-better improved flag'

$d = Get-PerfDelta -Baseline 10 -Candidate 8 -LowerIsBetter $false
Assert-Equal 'worse' $d.Status 'higher-better regression status'

$d = Get-PerfDelta -Baseline 10 -Candidate 8 -LowerIsBetter $true
Assert-Equal 'better' $d.Status 'lower-better improvement status'

$d = Get-PerfDelta -Baseline 10 -Candidate 12 -LowerIsBetter $true
Assert-Equal 'worse' $d.Status 'lower-better regression status'

$d = Get-PerfDelta -Baseline 10 -Candidate 10.2 -LowerIsBetter $false
Assert-Equal 'noise' $d.Status 'small delta within 4% floor -> noise'

# Spread wider than the 4% floor widens the band, so +20% can still be noise.
$d = Get-PerfDelta -Baseline 10 -Candidate 12 -LowerIsBetter $false -SpreadPct 25
Assert-Equal 'noise' $d.Status 'delta below spread band -> noise'

$d = Get-PerfDelta -Baseline $null -Candidate 12 -LowerIsBetter $false
Assert-Equal 'na' $d.Status 'null baseline -> na'
Assert-Null  $d.DeltaPct    'null baseline -> null delta'

$d = Get-PerfDelta -Baseline 0 -Candidate 12 -LowerIsBetter $false
Assert-Equal 'na' $d.Status 'zero baseline -> na'

# ── Format-PerfNumber / Format-PerfDeltaCell ─────────────────────────────────
Assert-Equal 'n/a'   (Format-PerfNumber $null 1) 'format null -> n/a'
Assert-Equal '8.70'  (Format-PerfNumber 8.7 2)   'format 2 digits invariant'
Assert-Equal '+20.0%' (Format-PerfDeltaCell ([pscustomobject]@{ DeltaPct = 20.0 })) 'delta cell signs positive'
Assert-Equal '-5.0%'  (Format-PerfDeltaCell ([pscustomobject]@{ DeltaPct = -5.0 })) 'delta cell signs negative'
Assert-Equal '—'      (Format-PerfDeltaCell ([pscustomobject]@{ DeltaPct = $null })) 'delta cell na -> dash'

# ── Get-PerfStatusGlyph (status color coding) ────────────────────────────────
Assert-Equal "$([char]0x2705) improvement"             (Get-PerfStatusGlyph 'better') 'better -> check + improvement'
Assert-Equal "$([char]0x26A0)$([char]0xFE0F) regression" (Get-PerfStatusGlyph 'worse')  'worse -> warning + regression'
Assert-Equal "$([char]0x2248) within noise"            (Get-PerfStatusGlyph 'noise')  'noise -> approx + within noise'

# ── Read-HarnessMetrics ──────────────────────────────────────────────────────
$tmp = Join-Path ([IO.Path]::GetTempPath()) ("perflib-tests-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tmp -Force | Out-Null
try {
    # C# Reactor-style report.txt (declarative: has reconcile + diff).
    $csReport = @"
StressPerf.ReactorOptimized report
Total Renders: 1234
Duration: 10.0 s
Avg Reconcile: 5.50 ms
Avg Diff: 2.20 ms
Avg Memory: 210.0 MB
"@
    Set-Content -LiteralPath (Join-Path $tmp 'StressPerf.ReactorOptimized.report.txt') -Value $csReport -Encoding UTF8
    $m = Read-HarnessMetrics -Directory $tmp -AppName 'StressPerf.ReactorOptimized'
    Assert-Equal 'report' $m.Source        'C# report parsed from report.txt'
    Assert-Equal 123.4 $m.RendersPerSec    'C# renders/sec = total/duration'
    Assert-Equal 5.5   $m.AvgReconcileMs   'C# reconcile'
    Assert-Equal 2.2   $m.AvgDiffMs        'C# diff'
    Assert-Equal 210   $m.AvgMemoryMB      'C# memory'

    # Rust port report.txt: indented Avg Diff, "Duration: 10.0s" (no space), and
    # a Renders/sec: line the parser ignores in favour of Total/Duration.
    $rustReport = @"
StressPerf.Reactor (windows-reactor) report
Renders/sec: 8.70
Avg Reconcile: 7.90 ms
  Avg Diff: 7.10 ms
Avg Memory: 190.0 MB
Total Renders: 87
Duration: 10.0s
"@
    Set-Content -LiteralPath (Join-Path $tmp 'StressPerf.Reactor.report.txt') -Value $rustReport -Encoding UTF8
    $r = Read-HarnessMetrics -Directory $tmp -AppName 'StressPerf.Reactor'
    Assert-Equal 'report' $r.Source       'Rust report parsed from report.txt'
    Assert-Equal 8.7  $r.RendersPerSec    'Rust renders/sec = 87/10'
    Assert-Equal 7.9  $r.AvgReconcileMs   'Rust reconcile'
    Assert-Equal 7.1  $r.AvgDiffMs        'Rust diff (indented line still matched)'
    Assert-Equal 190  $r.AvgMemoryMB      'Rust memory'

    # metrics.json takes precedence over report.txt when both exist.
    $json = '{"app":"StressPerf.ReactorOptimized","percent":50,"durationSeconds":10,"rendersPerSec":99.9,"totalRenders":999,"avgReconcileMs":1.1,"avgDiffMs":2.2,"avgMemoryMB":150.5,"avgFps":60,"sampleCount":5}'
    Set-Content -LiteralPath (Join-Path $tmp 'StressPerf.ReactorOptimized.metrics.json') -Value $json -Encoding UTF8
    $j = Read-HarnessMetrics -Directory $tmp -AppName 'StressPerf.ReactorOptimized'
    Assert-Equal 'json' $j.Source         'metrics.json wins over report.txt'
    Assert-Equal 99.9 $j.RendersPerSec    'json renders/sec'
    Assert-Equal 150.5 $j.AvgMemoryMB     'json memory'

    # A metrics.json with a null required field is rejected -> fall back to
    # report.txt, instead of coercing the null to 0 and reporting it.
    $badJson = '{"app":"StressPerf.Partial","percent":50,"durationSeconds":10,"rendersPerSec":null,"totalRenders":999,"avgReconcileMs":1.1,"avgDiffMs":2.2,"avgMemoryMB":150.5}'
    $partialReport = @"
StressPerf.Partial report
Total Renders: 400
Duration: 10.0 s
Avg Reconcile: 5.0 ms
Avg Diff: 3.0 ms
Avg Memory: 180.0 MB
"@
    Set-Content -LiteralPath (Join-Path $tmp 'StressPerf.Partial.metrics.json') -Value $badJson -Encoding UTF8
    Set-Content -LiteralPath (Join-Path $tmp 'StressPerf.Partial.report.txt') -Value $partialReport -Encoding UTF8
    $p = Read-HarnessMetrics -Directory $tmp -AppName 'StressPerf.Partial' -WarningAction SilentlyContinue
    Assert-Equal 'report' $p.Source       'null json field -> fall back to report.txt'
    Assert-Equal 40 $p.RendersPerSec      'fallback renders/sec from report (400/10)'

    # Imperative WinUI3 (StressPerf.Direct): no reconcile/diff lines -> n/a.
    $directReport = @"
StressPerf.Direct report
Total Renders: 500
Duration: 10.0 s
Avg Memory: 205.0 MB
"@
    Set-Content -LiteralPath (Join-Path $tmp 'StressPerf.Direct.report.txt') -Value $directReport -Encoding UTF8
    $w = Read-HarnessMetrics -Directory $tmp -AppName 'StressPerf.Direct'
    Assert-Equal 50 $w.RendersPerSec      'Direct renders/sec'
    Assert-Null  $w.AvgReconcileMs        'Direct reconcile -> n/a'
    Assert-Null  $w.AvgDiffMs             'Direct diff -> n/a'

    # Nothing on disk -> source 'none'.
    $none = Read-HarnessMetrics -Directory $tmp -AppName 'Nope.Missing'
    Assert-Equal 'none' $none.Source      'missing files -> none'
}
finally {
    Remove-Item -LiteralPath $tmp -Recurse -Force -ErrorAction SilentlyContinue
}

# ── Format-PerfComment (renderer smoke) ──────────────────────────────────────
$mainRuns = @(
    [pscustomobject]@{ RendersPerSec = 10; AvgReconcileMs = 5; AvgDiffMs = 2; AvgMemoryMB = 200; TotalRenders = 100; DurationSeconds = 10 }
    [pscustomobject]@{ RendersPerSec = 10; AvgReconcileMs = 5; AvgDiffMs = 2; AvgMemoryMB = 200; TotalRenders = 100; DurationSeconds = 10 }
)
$prRuns = @(
    [pscustomobject]@{ RendersPerSec = 12; AvgReconcileMs = 4; AvgDiffMs = 1.8; AvgMemoryMB = 195; TotalRenders = 120; DurationSeconds = 10 }
    [pscustomobject]@{ RendersPerSec = 12; AvgReconcileMs = 4; AvgDiffMs = 1.8; AvgMemoryMB = 195; TotalRenders = 120; DurationSeconds = 10 }
)
$main = Measure-PerfRuns -Runs $mainRuns
$pr = Measure-PerfRuns -Runs $prRuns
$winui3 = Measure-PerfRuns -Runs @([pscustomobject]@{ RendersPerSec = 9; AvgReconcileMs = $null; AvgDiffMs = $null; AvgMemoryMB = 220; TotalRenders = 90; DurationSeconds = 10 })
$rust = Measure-PerfRuns -Runs @([pscustomobject]@{ RendersPerSec = 8.5; AvgReconcileMs = 7.5; AvgDiffMs = 6.9; AvgMemoryMB = 188; TotalRenders = 85; DurationSeconds = 10 })
$ctx = @{ Percent = 50; Duration = 10; Reps = 2; Warmup = 1; HeadSha = 'abcdef1234'; BaseSha = '1234567890'; Cpu = 'Test CPU'; Cores = 4; MemoryGB = 16; Timestamp = '2025-01-01T00:00:00Z' }

# With a live Rust measurement.
$comment = Format-PerfComment -Main $main -Pr $pr -WinUI3 $winui3 -Rust $rust -Context $ctx
Assert-Match $comment '<!-- reactor-perf-compare -->' 'comment carries the sticky marker'
Assert-Match $comment 'Regression vs'                 'comment has regression table'
Assert-Match $comment 'Cross-framework reference'     'comment has cross-framework table'
Assert-Match $comment 'live on this runner'           'rust footnote = measured live'
Assert-Match $comment 'improvement'                   'renders/sec improvement glyph present'
Assert-Match $comment 'x64 Release'                   'methodology falls back to x64 when Platform absent'

# Platform threads through to the methodology line (and the missing-key fallback
# above must not throw under Set-StrictMode -Version Latest).
$armCtx = $ctx.Clone(); $armCtx['Platform'] = 'ARM64'
$armComment = Format-PerfComment -Main $main -Pr $pr -WinUI3 $null -Rust $null -Context $armCtx
Assert-Match $armComment 'ARM64 Release' 'methodology reflects -Platform ARM64'

# Rust absent -> column n/a, footnote says not run, and it must not throw.
$noRust = Format-PerfComment -Main $main -Pr $pr -WinUI3 $null -Rust $null -Context $ctx
Assert-Match $noRust 'Not run'  'rust footnote = not run when null'
Assert-Match $noRust 'n/a'      'n/a cells rendered when winui3 + rust null'

# ── Summary ──────────────────────────────────────────────────────────────────
Write-Host ""
if ($script:Fail -gt 0) {
    Write-Host "FAILED: $($script:Fail) / $($script:Pass + $script:Fail) assertions" -ForegroundColor Red
    foreach ($f in $script:Failures) { Write-Host "  ✗ $f" -ForegroundColor Red }
    exit 1
}
Write-Host "PASSED: all $($script:Pass) assertions" -ForegroundColor Green
exit 0
