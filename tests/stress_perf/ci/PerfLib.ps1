<#
.SYNOPSIS
    Pure, dot-source-able helpers for the on-demand /perf comparison workflow
    (.github/workflows/perf-compare.yml).

.DESCRIPTION
    These functions have no side effects beyond Write-Warning so they can be
    unit-tested locally without running a WinUI harness:

      Read-HarnessMetrics       parse one run's {AppName}.metrics.json (preferred)
                                or fall back to {AppName}.report.txt.
      Get-PerfMedian            median of a numeric sample.
      Get-PerfRelativeSpreadPct (max-min)/|median| as a percent — run-to-run noise.
      Measure-PerfRuns          median + spread across N per-run metric objects.
      Get-PerfDelta             signed %, direction-aware, with a noise band.
      Format-PerfComment        the sticky two-table markdown comment.

    The four headline metrics (Release build, StocksGrid workload):
      Renders/sec      higher is better
      Avg Reconcile ms lower is better
      Avg Diff ms      lower is better
      Avg Memory MB    lower is better
#>

Set-StrictMode -Version Latest

# Hidden marker used to find + update-in-place the sticky PR comment.
$script:PerfCommentMarker = '<!-- reactor-perf-compare -->'

# Headline metric table spec, shared by the comment renderer.
$script:PerfMetricSpec = @(
    [pscustomobject]@{ Key = 'RendersPerSec';  Label = 'Renders/sec';       LowerIsBetter = $false; Digits = 2; Arrow = [char]0x2191 } # up
    [pscustomobject]@{ Key = 'AvgReconcileMs'; Label = 'Avg Reconcile (ms)'; LowerIsBetter = $true;  Digits = 1; Arrow = [char]0x2193 } # down
    [pscustomobject]@{ Key = 'AvgDiffMs';      Label = 'Avg Diff (ms)';      LowerIsBetter = $true;  Digits = 1; Arrow = [char]0x2193 }
    [pscustomobject]@{ Key = 'AvgMemoryMB';    Label = 'Avg Memory (MB)';    LowerIsBetter = $true;  Digits = 1; Arrow = [char]0x2193 }
)

function ConvertTo-PerfDouble {
    <#
    .SYNOPSIS Culture-tolerant parse of a captured numeric string, or $null.
    #>
    param([string]$Raw)
    if ([string]::IsNullOrWhiteSpace($Raw)) { return $null }
    # Harness report numbers carry no thousands separators, so a comma can only
    # be a decimal separator emitted under a comma-decimal culture — normalise it.
    $norm = ($Raw.Trim() -replace ',', '.')
    [double]$val = 0
    $ok = [double]::TryParse(
        $norm,
        [System.Globalization.NumberStyles]::Float,
        [System.Globalization.CultureInfo]::InvariantCulture,
        [ref]$val)
    if ($ok) { return $val }
    return $null
}

function Get-PerfReportField {
    param([string]$Text, [string]$Pattern, [switch]$AsInt)
    $m = [regex]::Match($Text, $Pattern)
    if (-not $m.Success) { return $null }
    $val = ConvertTo-PerfDouble $m.Groups[1].Value
    if ($null -eq $val) { return $null }
    if ($AsInt) { return [int][math]::Round($val) }
    return $val
}

function Read-HarnessMetrics {
    <#
    .SYNOPSIS
        Normalised metrics for one harness run. Prefers {AppName}.metrics.json
        (emitted by --json); falls back to {AppName}.report.txt regex parsing
        for harness builds/variants that predate --json (e.g. StressPerf.Direct).
    .OUTPUTS
        PSCustomObject with RendersPerSec, AvgReconcileMs, AvgDiffMs,
        AvgMemoryMB, TotalRenders, DurationSeconds (any of which may be $null
        when not applicable), and Source ('json' | 'report' | 'none').
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Directory,
        [Parameter(Mandatory)][string]$AppName
    )

    $result = [pscustomobject]@{
        AppName         = $AppName
        RendersPerSec   = $null
        AvgReconcileMs  = $null
        AvgDiffMs       = $null
        AvgMemoryMB     = $null
        TotalRenders    = $null
        DurationSeconds = $null
        Source          = 'none'
    }

    $jsonPath   = Join-Path $Directory ("{0}.metrics.json" -f $AppName)
    $reportPath = Join-Path $Directory ("{0}.report.txt" -f $AppName)

    if (Test-Path -LiteralPath $jsonPath) {
        try {
            $j = Get-Content -LiteralPath $jsonPath -Raw | ConvertFrom-Json
            # Every field below is always emitted by GetMetricsJson, so a missing
            # or null one means a partial write or a schema mismatch. Reject the
            # JSON and fall back to report.txt rather than coerce null -> 0 and
            # surface a misleading metric / delta.
            $required = 'rendersPerSec', 'avgReconcileMs', 'avgDiffMs', 'avgMemoryMB', 'totalRenders', 'durationSeconds'
            $missing = @($required | Where-Object {
                    $p = $j.PSObject.Properties[$_]
                    (-not $p) -or ($null -eq $p.Value)
                })
            if ($missing.Count -gt 0) {
                Write-Warning "Read-HarnessMetrics: '$jsonPath' is missing/null field(s): $($missing -join ', '); falling back to report.txt."
            }
            else {
                $result.RendersPerSec   = [double]$j.rendersPerSec
                $result.AvgReconcileMs  = [double]$j.avgReconcileMs
                $result.AvgDiffMs       = [double]$j.avgDiffMs
                $result.AvgMemoryMB     = [double]$j.avgMemoryMB
                $result.TotalRenders    = [int]$j.totalRenders
                $result.DurationSeconds = [double]$j.durationSeconds
                $result.Source          = 'json'
                return $result
            }
        }
        catch {
            Write-Warning "Read-HarnessMetrics: '$jsonPath' is not valid JSON ($($_.Exception.Message)); falling back to report.txt."
        }
    }

    if (-not (Test-Path -LiteralPath $reportPath)) {
        Write-Warning "Read-HarnessMetrics: no metrics.json or report.txt for '$AppName' in '$Directory'."
        return $result
    }

    $text = Get-Content -LiteralPath $reportPath -Raw
    $result.Source          = 'report'
    $result.TotalRenders    = Get-PerfReportField $text 'Total Renders:\s*([0-9][0-9.,]*)' -AsInt
    $result.DurationSeconds = Get-PerfReportField $text 'Duration:\s*([0-9][0-9.,]*)\s*s'
    # Reconcile / Diff lines only exist for declarative (Reactor) variants;
    # imperative WinUI3 (StressPerf.Direct) omits them -> stay $null (n/a).
    $result.AvgReconcileMs  = Get-PerfReportField $text 'Avg Reconcile:\s*([0-9][0-9.,]*)\s*ms'
    $result.AvgDiffMs       = Get-PerfReportField $text 'Avg Diff:\s*([0-9][0-9.,]*)\s*ms'
    $result.AvgMemoryMB     = Get-PerfReportField $text 'Avg Memory:\s*([0-9][0-9.,]*)\s*MB'

    if ($null -ne $result.TotalRenders -and $null -ne $result.DurationSeconds -and $result.DurationSeconds -gt 0) {
        $result.RendersPerSec = [math]::Round($result.TotalRenders / $result.DurationSeconds, 4)
    }
    return $result
}

function Get-PerfMedian {
    param([Parameter(ValueFromPipeline)][AllowNull()][double[]]$Values)
    $v = @($Values | Where-Object { $null -ne $_ })
    if ($v.Count -eq 0) { return $null }
    $sorted = @($v | Sort-Object)
    $n = $sorted.Count
    if ($n % 2 -eq 1) { return [double]$sorted[[int][math]::Floor($n / 2)] }
    return ([double]$sorted[$n / 2 - 1] + [double]$sorted[$n / 2]) / 2.0
}

function Get-PerfRelativeSpreadPct {
    <#
    .SYNOPSIS Run-to-run dispersion (max-min)/|median| as a percent. 0 for <2 samples.
    #>
    param([AllowNull()][double[]]$Values)
    $v = @($Values | Where-Object { $null -ne $_ })
    if ($v.Count -lt 2) { return 0.0 }
    $min = ($v | Measure-Object -Minimum).Minimum
    $max = ($v | Measure-Object -Maximum).Maximum
    $med = Get-PerfMedian $v
    if ($null -eq $med -or $med -eq 0) { return 0.0 }
    return [math]::Round((($max - $min) / [math]::Abs($med)) * 100.0, 1)
}

function Measure-PerfRuns {
    <#
    .SYNOPSIS
        Collapse N per-run metric objects (from Read-HarnessMetrics) into a
        single object carrying the median of each metric plus a "<Key>Spread"
        relative-dispersion percent.
    #>
    param([Parameter(Mandatory)][AllowEmptyCollection()][object[]]$Runs)

    $keys = 'RendersPerSec', 'AvgReconcileMs', 'AvgDiffMs', 'AvgMemoryMB', 'TotalRenders', 'DurationSeconds'
    $agg = [ordered]@{ RunCount = @($Runs).Count }
    foreach ($k in $keys) {
        $vals = @($Runs | ForEach-Object { $_.$k } | Where-Object { $null -ne $_ } | ForEach-Object { [double]$_ })
        $agg[$k] = Get-PerfMedian $vals
        $agg["${k}Spread"] = Get-PerfRelativeSpreadPct $vals
    }
    return [pscustomobject]$agg
}

function Get-PerfDelta {
    <#
    .SYNOPSIS
        Signed percent change of Candidate vs Baseline, direction-aware, with a
        noise band. Status is one of: better | worse | noise | na.
    .PARAMETER NoiseFloorPct
        Absolute floor for the "within noise" band (default 4%).
    .PARAMETER SpreadPct
        Run-to-run dispersion for this metric; the effective noise band is
        max(NoiseFloorPct, SpreadPct).
    #>
    param(
        [AllowNull()]$Baseline,
        [AllowNull()]$Candidate,
        [Parameter(Mandatory)][bool]$LowerIsBetter,
        [double]$NoiseFloorPct = 4.0,
        [double]$SpreadPct = 0.0
    )
    if ($null -eq $Baseline -or $null -eq $Candidate -or [double]$Baseline -eq 0) {
        return [pscustomobject]@{ DeltaPct = $null; Status = 'na'; Improved = $null }
    }
    $b = [double]$Baseline
    $c = [double]$Candidate
    $deltaPct = (($c - $b) / [math]::Abs($b)) * 100.0
    $improved = if ($LowerIsBetter) { $deltaPct -lt 0 } else { $deltaPct -gt 0 }
    $band = [math]::Max($NoiseFloorPct, $SpreadPct)
    $status = if ([math]::Abs($deltaPct) -lt $band) { 'noise' } elseif ($improved) { 'better' } else { 'worse' }
    return [pscustomobject]@{ DeltaPct = [math]::Round($deltaPct, 1); Status = $status; Improved = $improved }
}

function Format-PerfNumber {
    param([AllowNull()]$Value, [int]$Digits = 1)
    if ($null -eq $Value) { return 'n/a' }
    return ([math]::Round([double]$Value, $Digits)).ToString("0.$('0' * $Digits)", [System.Globalization.CultureInfo]::InvariantCulture)
}

function Format-PerfDeltaCell {
    param([pscustomobject]$Delta)
    if ($null -eq $Delta.DeltaPct) { return '—' }
    $s = ('{0:+0.0;-0.0;0.0}' -f $Delta.DeltaPct)
    return "$s%"
}

function Get-PerfStatusGlyph {
    param([string]$Status)
    switch ($Status) {
        'better' { return [char]0x2705 + ' improvement' }                       # checkmark
        'worse'  { return [char]0x26A0 + [char]0xFE0F + ' regression' }          # warning
        'noise'  { return [char]0x2248 + ' within noise' }                       # almost-equal
        default  { return '—' }
    }
}

function Format-PerfComment {
    <#
    .SYNOPSIS
        Render the sticky two-table comparison comment (markdown), prefixed with
        the hidden marker used for update-in-place.
    .PARAMETER Main       Aggregated baseline metrics (Measure-PerfRuns output).
    .PARAMETER Pr         Aggregated PR-head metrics.
    .PARAMETER WinUI3     Aggregated vanilla-WinUI3 (StressPerf.Direct) metrics, or $null.
    .PARAMETER Rust       Aggregated Rust windows-reactor (test_reactor_perf) metrics
                          measured live on this runner, or $null when not run.
    .PARAMETER Context    Hashtable: Percent, Duration, Reps, Warmup, BaseSha, HeadSha,
                          Runner, Cpu, Cores, MemoryGB, RunUrl, Timestamp, Note.
    #>
    param(
        [Parameter(Mandatory)][pscustomobject]$Main,
        [Parameter(Mandatory)][pscustomobject]$Pr,
        [AllowNull()][pscustomobject]$WinUI3,
        [AllowNull()][pscustomobject]$Rust,
        [Parameter(Mandatory)][hashtable]$Context
    )

    $nl = "`n"
    $lines = [System.Collections.Generic.List[string]]::new()
    $add = { param($t) $lines.Add($t) }

    & $add $script:PerfCommentMarker
    & $add "## $([char]0x26A1) Reactor perf comparison"
    & $add ''
    $plat = if ($Context.ContainsKey('Platform') -and $Context.Platform) { $Context.Platform } else { 'x64' }
    $methodology = "**Workload:** ``StressPerf.ReactorOptimized`` StocksGrid &middot; " +
        "``--percent $($Context.Percent) --duration $($Context.Duration)`` &middot; $plat Release &middot; " +
        "median of $($Context.Reps) runs ($($Context.Warmup) warmup dropped) &middot; " +
        "PR head and ``main`` built and run **interleaved on the same runner**."
    & $add $methodology
    & $add ''

    # ── Table 1: regression vs main ──────────────────────────────────────────
    & $add "### Regression vs ``main`` baseline"
    & $add ''
    & $add '| Metric | `main` (baseline) | This PR | Δ | Status |'
    & $add '|---|--:|--:|--:|:--|'
    foreach ($m in $script:PerfMetricSpec) {
        $bVal = $Main.($m.Key)
        $pVal = $Pr.($m.Key)
        $spread = [math]::Max([double]$Main."$($m.Key)Spread", [double]$Pr."$($m.Key)Spread")
        $delta = Get-PerfDelta -Baseline $bVal -Candidate $pVal -LowerIsBetter $m.LowerIsBetter -SpreadPct $spread
        $row = '| {0} {1} | {2} | {3} | {4} | {5} |' -f `
            $m.Label, $m.Arrow, `
            (Format-PerfNumber $bVal $m.Digits), `
            (Format-PerfNumber $pVal $m.Digits), `
            (Format-PerfDeltaCell $delta), `
            (Get-PerfStatusGlyph $delta.Status)
        & $add $row
    }
    & $add ''

    # ── Table 2: cross-framework reference ───────────────────────────────────
    & $add "### Cross-framework reference (same StocksGrid workload)"
    & $add ''
    & $add ('| Metric | vanilla WinUI3{0} | Rust `windows-reactor`{1} | Reactor (this PR) |' -f [char]0x00B9, [char]0x00B2)
    & $add '|---|--:|--:|--:|'
    foreach ($m in $script:PerfMetricSpec) {
        $w = if ($null -ne $WinUI3) { $WinUI3.($m.Key) } else { $null }
        $r = if ($null -ne $Rust) { $Rust.($m.Key) } else { $null }
        $row = '| {0} {1} | {2} | {3} | {4} |' -f `
            $m.Label, $m.Arrow, `
            (Format-PerfNumber $w $m.Digits), `
            (Format-PerfNumber $r $m.Digits), `
            (Format-PerfNumber $Pr.($m.Key) $m.Digits)
        & $add $row
    }
    & $add ''

    # ── Footnotes ────────────────────────────────────────────────────────────
    $up = [char]0x2191; $down = [char]0x2193
    $rustNote = if ($null -ne $Rust) {
        'Built from source and measured **live on this runner**.'
    } else {
        '*Not run* on this runner (Rust toolchain/checkout unavailable or the Rust leg failed) — its cells read *n/a*.'
    }
    & $add "<sub>$up higher is better &middot; $down lower is better. **Within noise** = |Δ| below the larger of the run-to-run spread and a 4% floor; treat as no measurable change.</sub>"
    & $add "<sub>$([char]0x00B9) vanilla WinUI3 = ``StressPerf.Direct`` (imperative; no virtual-DOM, so it has no reconcile/diff phase — those cells read *n/a*). Measured live on this runner.</sub>"
    & $add "<sub>$([char]0x00B2) Rust = ``test_reactor_perf`` from [microsoft/windows-rs](https://github.com/microsoft/windows-rs/tree/master/crates/tests/libs/reactor_perf) — a port of this harness (same StocksGrid, same ``--percent``/``--duration`` CLI). $rustNote</sub>"
    & $add "<sub>Absolute numbers are runner-dependent — trust the **Δ vs main**, not the absolute values. Memory (working set) is the noisiest metric.</sub>"

    $ctxBits = @()
    if ($Context.ContainsKey('Cpu') -and $Context.Cpu)       { $ctxBits += "CPU: $($Context.Cpu)" }
    if ($Context.ContainsKey('Cores') -and $Context.Cores)   { $ctxBits += "$($Context.Cores) logical cores" }
    if ($Context.ContainsKey('MemoryGB') -and $Context.MemoryGB) { $ctxBits += "$($Context.MemoryGB) GB RAM" }
    if ($Context.ContainsKey('Runner') -and $Context.Runner) { $ctxBits += "runner: $($Context.Runner)" }
    if ($ctxBits.Count -gt 0) { & $add ("<sub>Runner: " + ($ctxBits -join ' &middot; ') + ".</sub>") }

    $shaBits = @()
    if ($Context.ContainsKey('HeadSha') -and $Context.HeadSha) { $shaBits += "PR ``$($Context.HeadSha)``" }
    if ($Context.ContainsKey('BaseSha') -and $Context.BaseSha) { $shaBits += "main ``$($Context.BaseSha)``" }
    $genLine = "<sub>Generated by ``.github/workflows/perf-compare.yml``"
    if ($shaBits.Count -gt 0) { $genLine += ' &middot; ' + ($shaBits -join ' vs ') }
    if ($Context.ContainsKey('Timestamp') -and $Context.Timestamp) { $genLine += " &middot; $($Context.Timestamp)" }
    if ($Context.ContainsKey('RunUrl') -and $Context.RunUrl) { $genLine += " &middot; [run log]($($Context.RunUrl))" }
    $genLine += '.</sub>'
    & $add $genLine

    if ($Context.ContainsKey('Note') -and $Context.Note) {
        & $add ''
        & $add "> [!NOTE]$nl> $($Context.Note)"
    }

    return ($lines -join $nl)
}
