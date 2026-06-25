using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace StressPerf.Shared;

public sealed class PerfTracker
{
    private readonly Stopwatch _wallClock = Stopwatch.StartNew();
    private readonly Stopwatch _updateSw = new();
    private int _frameCount;
    private double _lastSampleTime;
    private double _currentFps;
    private double _lastUpdateMs;

    private readonly List<double> _fpsSamples = new();
    private readonly List<long> _memorySamples = new();
    private readonly List<double> _updateTimeSamples = new();
    private readonly List<double> _reconcileTimeSamples = new();
    private readonly List<double> _treeBuildSamples = new();
    private readonly List<double> _diffPatchSamples = new();
    private readonly List<double> _effectsSamples = new();
    // Cross-variant render counter. See METHODOLOGY.md for what this means
    // per framework. Imperative variants increment after each tick's
    // mutate-and-set-properties pass; declarative variants (Reactor)
    // increment when the reconcile completes via RecordPhases.
    private int _renderCount;

    public double CurrentFps => _currentFps;
    public double LastUpdateMs => _lastUpdateMs;
    public long CurrentMemoryMB => Process.GetCurrentProcess().WorkingSet64 / (1024 * 1024);

    /// <summary>
    /// Call from CompositionTarget.Rendering to count composed frames.
    /// </summary>
    public void FrameRendered()
    {
        _frameCount++;
        double now = _wallClock.Elapsed.TotalSeconds;
        double elapsed = now - _lastSampleTime;
        if (elapsed >= 1.0)
        {
            _currentFps = _frameCount / elapsed;
            _fpsSamples.Add(_currentFps);
            _memorySamples.Add(Process.GetCurrentProcess().WorkingSet64);
            _frameCount = 0;
            _lastSampleTime = now;
        }
    }

    /// <summary>
    /// Call before updating data + UI.
    /// </summary>
    public void BeginUpdate() => _updateSw.Restart();

    /// <summary>
    /// Call after updating data + UI.
    /// </summary>
    public void EndUpdate()
    {
        _updateSw.Stop();
        _lastUpdateMs = _updateSw.Elapsed.TotalMilliseconds;
        _updateTimeSamples.Add(_lastUpdateMs);
    }

    /// <summary>
    /// Increment the cross-variant render counter. Call once per "render
    /// completed" event for the framework — for imperative variants
    /// (Direct/Bound/Wpf/DirectX) that's after the tick handler finishes
    /// patching properties; for Reactor it happens automatically when
    /// <see cref="RecordPhases"/> fires from the reconcile-complete callback.
    /// See METHODOLOGY.md.
    /// </summary>
    public void RecordRender() => _renderCount++;

    public int TotalRenders => _renderCount;

    /// <summary>
    /// Record per-phase breakdown for a render pass. Reactor only — also
    /// counts as a render via <see cref="RecordRender"/>.
    /// </summary>
    public void RecordPhases(double treeBuildMs, double diffPatchMs, double effectsMs)
    {
        _treeBuildSamples.Add(treeBuildMs);
        _diffPatchSamples.Add(diffPatchMs);
        _effectsSamples.Add(effectsMs);
        _reconcileTimeSamples.Add(treeBuildMs + diffPatchMs + effectsMs);
        RecordRender();
    }

    public double ElapsedSeconds => _wallClock.Elapsed.TotalSeconds;

    public string GetReport(string appName, double percent)
    {
        if (_fpsSamples.Count == 0) return "No data collected.";

        var sb = new StringBuilder();
        sb.AppendLine($"=== {appName} ===");
        sb.AppendLine($"Duration:    {_wallClock.Elapsed.TotalSeconds:F1}s");
        sb.AppendLine($"Percent:     {percent:F0}%");
        sb.AppendLine($"Avg FPS:     {_fpsSamples.Average():F1}");
        sb.AppendLine($"Min FPS:     {_fpsSamples.Min():F1}");
        sb.AppendLine($"Max FPS:     {_fpsSamples.Max():F1}");
        if (_updateTimeSamples.Count > 0)
        {
            sb.AppendLine($"Avg Update:  {_updateTimeSamples.Average():F1} ms");
            sb.AppendLine($"Max Update:  {_updateTimeSamples.Max():F1} ms");
        }
        // Always emit Total Renders so easy-mode (no-ETW) baselines have a
        // free cross-framework throughput proxy. See METHODOLOGY.md.
        sb.AppendLine($"Total Renders: {_renderCount}");
        if (_reconcileTimeSamples.Count > 0)
        {
            sb.AppendLine($"Avg Reconcile: {_reconcileTimeSamples.Average():F1} ms");
            sb.AppendLine($"Max Reconcile: {_reconcileTimeSamples.Max():F1} ms");
        }
        if (_treeBuildSamples.Count > 0)
        {
            sb.AppendLine($"  Avg Tree:    {_treeBuildSamples.Average():F1} ms");
            sb.AppendLine($"  Avg Diff:    {_diffPatchSamples.Average():F1} ms");
            sb.AppendLine($"  Avg Effects: {_effectsSamples.Average():F1} ms");
        }
        if (_updateTimeSamples.Count > 0 && _reconcileTimeSamples.Count > 0)
        {
            // Per-tick combined cost: total work (update + reconcile) / number of ticks.
            // This correctly handles coalescing where R renders < U ticks.
            int ticks = _updateTimeSamples.Count;
            double combinedPerTick = (_updateTimeSamples.Sum() + _reconcileTimeSamples.Sum()) / ticks;
            sb.AppendLine($"Avg Combined:  {combinedPerTick:F1} ms  (renders/tick: {(double)_reconcileTimeSamples.Count / ticks:F2})");
        }
        sb.AppendLine($"Avg Memory:  {_memorySamples.Average() / (1024 * 1024):F1} MB");
        sb.AppendLine($"Peak Memory: {_memorySamples.Max() / (1024 * 1024):F1} MB");
        return sb.ToString();
    }

    /// <summary>
    /// Write report to a file next to the executable.
    /// </summary>
    public void WriteReportFile(string appName, double percent)
    {
        var report = GetReport(appName, percent);
        var path = Path.Combine(AppContext.BaseDirectory, $"{appName}.report.txt");
        File.WriteAllText(path, report);

        var csv = new StringBuilder();
        csv.AppendLine("Second,FPS,Memory_MB");
        int n = Math.Min(_fpsSamples.Count, _memorySamples.Count);
        for (int i = 0; i < n; i++)
        {
            double mb = _memorySamples[i] / (1024.0 * 1024.0);
            csv.AppendLine($"{i + 1},{_fpsSamples[i]:F2},{mb:F1}");
        }
        var csvPath = Path.Combine(AppContext.BaseDirectory, $"{appName}.samples.csv");
        File.WriteAllText(csvPath, csv.ToString());
    }

    // ── Machine-readable metrics (CI) ────────────────────────────────────────
    // The on-demand perf-comparison workflow parses these four headline numbers
    // to diff a PR against the main baseline. Renders/sec is "higher is better";
    // the three latency/memory figures are "lower is better". Kept here (rather
    // than scraped from GetReport) so CI never has to depend on the exact prose
    // layout of the human report, and so missing phase samples surface as 0
    // rather than an absent line. See .github/workflows/perf-compare.yml.

    /// <summary>Average reconcile cost (ms) across all recorded render passes, or 0.</summary>
    public double AvgReconcileMs => _reconcileTimeSamples.Count > 0 ? _reconcileTimeSamples.Average() : 0.0;

    /// <summary>Average diff/patch cost (ms) across all recorded render passes, or 0.</summary>
    public double AvgDiffMs => _diffPatchSamples.Count > 0 ? _diffPatchSamples.Average() : 0.0;

    /// <summary>Average sampled working set in MB, or 0 when no samples were taken.</summary>
    public double AvgMemoryMB => _memorySamples.Count > 0 ? _memorySamples.Average() / (1024.0 * 1024.0) : 0.0;

    /// <summary>
    /// Throughput proxy: total renders divided by measured wall-clock seconds.
    /// Mirrors the methodology's <c>Total Renders / Duration</c> (METHODOLOGY.md,
    /// "easy mode") since both use the same <see cref="ElapsedSeconds"/> clock.
    /// </summary>
    public double RendersPerSec => ElapsedSeconds > 0 ? _renderCount / ElapsedSeconds : 0.0;

    /// <summary>
    /// Compact, single-line, culture-invariant JSON with the four headline
    /// metrics plus context. Built by hand (no serializer) to stay trivially
    /// AOT/trim-safe for this PublishAot harness.
    /// </summary>
    public string GetMetricsJson(string appName, double percent)
    {
        static string F(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);
        var sb = new StringBuilder();
        sb.Append('{');
        sb.Append("\"app\":\"").Append(appName).Append("\",");
        sb.Append("\"percent\":").Append(F(percent)).Append(',');
        sb.Append("\"durationSeconds\":").Append(F(ElapsedSeconds)).Append(',');
        sb.Append("\"rendersPerSec\":").Append(F(RendersPerSec)).Append(',');
        sb.Append("\"totalRenders\":").Append(_renderCount.ToString(CultureInfo.InvariantCulture)).Append(',');
        sb.Append("\"avgReconcileMs\":").Append(F(AvgReconcileMs)).Append(',');
        sb.Append("\"avgDiffMs\":").Append(F(AvgDiffMs)).Append(',');
        sb.Append("\"avgMemoryMB\":").Append(F(AvgMemoryMB)).Append(',');
        sb.Append("\"avgFps\":").Append(F(_fpsSamples.Count > 0 ? _fpsSamples.Average() : 0.0)).Append(',');
        sb.Append("\"sampleCount\":").Append(_fpsSamples.Count.ToString(CultureInfo.InvariantCulture));
        sb.Append('}');
        return sb.ToString();
    }

    /// <summary>
    /// Write the machine-readable metrics to <c>{appName}.metrics.json</c> next
    /// to the executable (alongside the human report written by
    /// <see cref="WriteReportFile"/>).
    /// </summary>
    public void WriteMetricsJsonFile(string appName, double percent)
    {
        // GetFileName() strips any directory/rooted segment so a stray appName
        // can't redirect the write or make Path.Combine drop BaseDirectory.
        var safeAppName = Path.GetFileName(appName);
        var path = Path.Combine(AppContext.BaseDirectory, $"{safeAppName}.metrics.json");
        File.WriteAllText(path, GetMetricsJson(appName, percent));
    }
}
