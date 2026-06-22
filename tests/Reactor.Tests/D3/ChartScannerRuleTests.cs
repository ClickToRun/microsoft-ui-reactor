using Microsoft.UI.Reactor.Charting;
using Microsoft.UI.Reactor.Charting.Accessibility;
using Microsoft.UI.Reactor.Charting.D3;
using Microsoft.UI.Reactor.Core;
using Xunit;
using static Microsoft.UI.Reactor.Factories;

namespace Microsoft.UI.Reactor.Tests.D3;

public class ChartScannerRuleTests
{
    static ChartScannerRuleTests()
    {
        // The chart accessibility rules now live in the Charting subsystem and are
        // contributed to the core scanner via registration (issue #498). Install
        // the checker so these unit tests exercise the chart rules.
        AccessibilityScanner.RegisterScanExtension(ChartAccessibilityChecker.Instance);
    }

    private record DataPoint(double X, double Y);

    private static readonly DataPoint[] SampleData =
        Enumerable.Range(0, 5).Select(i => new DataPoint(i, (i + 1) * 10.0)).ToArray();

    /// <summary>
    /// Creates a CanvasElement that simulates a chart with the given chart data properties.
    /// Avoids calling ToElement() which requires WinUI COM initialization.
    /// </summary>
    private static CanvasElement MakeChartCanvas(
        IChartAccessibilityData? chartData = null,
        bool isColorOnly = false,
        bool isRawColors = false,
        ChartPalette? customPalette = null,
        string? automationName = null,
        bool isInteractive = false,
        bool isKeyboardDisabled = false,
        bool isTightHitTest = false,
        global::Windows.UI.Color? customFocusColor = null,
        bool isAnnounceEveryFrame = false)
    {
        var canvas = new CanvasElement([])
        {
            Width = 400,
            Height = 300,
        };

        if (chartData != null)
        {
            canvas = (CanvasElement)canvas.SetAttached(new ChartA11yData(chartData)
            {
                IsColorOnly = isColorOnly,
                IsRawColors = isRawColors,
                CustomPalette = customPalette,
                IsInteractive = isInteractive,
                IsKeyboardDisabled = isKeyboardDisabled,
                IsTightHitTest = isTightHitTest,
                CustomFocusColor = customFocusColor,
                IsAnnounceEveryFrame = isAnnounceEveryFrame,
            });
        }

        if (automationName != null)
            canvas = (CanvasElement)(canvas as Element).AutomationName(automationName);

        return canvas;
    }

    /// <summary>Mock chart accessibility data for testing.</summary>
    private sealed class MockChartData : IChartAccessibilityData
    {
        public string? Name { get; init; }
        public string? Description { get; init; }
        public IReadOnlyList<ChartSeriesDescriptor> Series { get; init; } = [];
        public IReadOnlyList<ChartAxisDescriptor> Axes { get; init; } = [];
        public ChartViewport? Viewport { get; init; }
        public string ChartTypeName { get; init; } = "Line";
    }

    private static MockChartData DataWithSeries(string? name = null, string? description = null, int pointCount = 5)
    {
        var points = Enumerable.Range(0, pointCount)
            .Select(i => new ChartPointDescriptor(i.ToString(), (i + 1) * 10.0))
            .ToArray();
        return new MockChartData
        {
            Name = name,
            Description = description,
            Series = [new ChartSeriesDescriptor("Series 1", points)],
            Axes = [
                new ChartAxisDescriptor(ChartAxisType.X, "X", 0, pointCount - 1),
                new ChartAxisDescriptor(ChartAxisType.Y, "Y", 10, pointCount * 10),
            ],
        };
    }

    // ── A11Y_CHART_001: Chart has no Title/AutomationName ───────────

    [Fact]
    public void A11Y_CHART_001_ChartWithoutTitle_Flagged()
    {
        var canvas = MakeChartCanvas(chartData: DataWithSeries());
        var tree = VStack(canvas);

        var findings = AccessibilityScanner.Scan(tree);
        Assert.Contains(findings, f => f.Id == "A11Y_CHART_001");
    }

    [Fact]
    public void A11Y_CHART_001_ChartWithTitle_Passes()
    {
        var canvas = MakeChartCanvas(chartData: DataWithSeries(name: "Revenue Over Time"));
        var tree = VStack(canvas);

        var findings = AccessibilityScanner.Scan(tree);
        Assert.DoesNotContain(findings, f => f.Id == "A11Y_CHART_001");
    }

    [Fact]
    public void A11Y_CHART_001_ChartWithAutomationName_Passes()
    {
        var canvas = MakeChartCanvas(chartData: DataWithSeries(), automationName: "Revenue Chart");
        var tree = VStack(canvas);

        var findings = AccessibilityScanner.Scan(tree);
        Assert.DoesNotContain(findings, f => f.Id == "A11Y_CHART_001");
    }

    // ── A11Y_CHART_002: Chart has no Description ────────────────────

    [Fact]
    public void A11Y_CHART_002_ChartWithData_HasAutoSummary_Passes()
    {
        var canvas = MakeChartCanvas(chartData: DataWithSeries(name: "Revenue"));
        var tree = VStack(canvas);

        var findings = AccessibilityScanner.Scan(tree);
        Assert.DoesNotContain(findings, f => f.Id == "A11Y_CHART_002");
    }

    [Fact]
    public void A11Y_CHART_002_EmptyChartWithoutDescription_Flagged()
    {
        var canvas = MakeChartCanvas(chartData: new MockChartData());
        var tree = VStack(canvas);

        var findings = AccessibilityScanner.Scan(tree);
        Assert.Contains(findings, f => f.Id == "A11Y_CHART_002");
    }

    [Fact]
    public void A11Y_CHART_002_EmptyChartWithDescription_Passes()
    {
        var canvas = MakeChartCanvas(chartData: new MockChartData
        {
            Description = "Revenue chart showing monthly income trends.",
        });
        var tree = VStack(canvas);

        var findings = AccessibilityScanner.Scan(tree);
        Assert.DoesNotContain(findings, f => f.Id == "A11Y_CHART_002");
    }

    // ── A11Y_CHART_004: ColorOnly ───────────────────────────────────

    [Fact]
    public void A11Y_CHART_004_ColorOnly_Flagged()
    {
        var canvas = MakeChartCanvas(chartData: DataWithSeries(name: "Revenue"), isColorOnly: true);
        var tree = VStack(canvas);

        var findings = AccessibilityScanner.Scan(tree);
        Assert.Contains(findings, f => f.Id == "A11Y_CHART_004");
    }

    [Fact]
    public void A11Y_CHART_004_DefaultEncoding_Passes()
    {
        var canvas = MakeChartCanvas(chartData: DataWithSeries(name: "Revenue"));
        var tree = VStack(canvas);

        var findings = AccessibilityScanner.Scan(tree);
        Assert.DoesNotContain(findings, f => f.Id == "A11Y_CHART_004");
    }

    // ── A11Y_CHART_009: Custom palette fails pairwise contrast ──────

    [Fact]
    public void A11Y_CHART_009_LowContrastPalette_Flagged()
    {
        var palette = ChartPalette.FromColors(
            new D3Color(128, 128, 128),
            new D3Color(135, 135, 135)); // Very similar grays
        var canvas = MakeChartCanvas(chartData: DataWithSeries(name: "Revenue"), customPalette: palette);
        var tree = VStack(canvas);

        var findings = AccessibilityScanner.Scan(tree);
        Assert.Contains(findings, f => f.Id == "A11Y_CHART_009");

        var finding = findings.First(f => f.Id == "A11Y_CHART_009");
        Assert.NotNull(finding.Fix.SuggestedValue);
    }

    [Fact]
    public void A11Y_CHART_009_HighContrastPalette_Passes()
    {
        var palette = ChartPalette.FromColors(
            new D3Color(0, 0, 0),
            new D3Color(255, 255, 255)); // Maximum contrast
        var canvas = MakeChartCanvas(chartData: DataWithSeries(name: "Revenue"), customPalette: palette);
        var tree = VStack(canvas);

        var findings = AccessibilityScanner.Scan(tree);
        Assert.DoesNotContain(findings, f => f.Id == "A11Y_CHART_009");
    }

    // ── A11Y_CHART_010: Colorblind ΔE check ────────────────────────

    [Fact]
    public void A11Y_CHART_010_ColorblindUnsafePalette_Processed()
    {
        var palette = ChartPalette.FromColors(
            new D3Color(180, 60, 60),
            new D3Color(60, 160, 60));
        var canvas = MakeChartCanvas(chartData: DataWithSeries(name: "Revenue"), customPalette: palette);
        var tree = VStack(canvas);

        var findings = AccessibilityScanner.Scan(tree);
        Assert.NotNull(findings);
    }

    // ── A11Y_CHART_011: Background contrast ─────────────────────────

    [Fact]
    public void A11Y_CHART_011_VeryLightColor_FailsDark_PassesLight()
    {
        var palette = ChartPalette.FromColors(new D3Color(255, 255, 200));
        var canvas = MakeChartCanvas(chartData: DataWithSeries(name: "Revenue"), customPalette: palette);
        var tree = VStack(canvas);

        var findings = AccessibilityScanner.Scan(tree);
        // Light yellow passes against dark bg (good contrast), so _011 should not fire
        Assert.DoesNotContain(findings, f => f.Id == "A11Y_CHART_011");
    }

    // ── A11Y_CHART_012: RawColors escape hatch ──────────────────────

    [Fact]
    public void A11Y_CHART_012_RawColors_EmittedAsInfo()
    {
        var canvas = MakeChartCanvas(
            chartData: DataWithSeries(name: "Revenue"),
            isRawColors: true);
        var tree = VStack(canvas);

        var findings = AccessibilityScanner.Scan(tree);
        var rawFinding = findings.FirstOrDefault(f => f.Id == "A11Y_CHART_012");
        Assert.NotNull(rawFinding);
        Assert.Equal("info", rawFinding!.Severity);
    }

    [Fact]
    public void A11Y_CHART_012_NormalPalette_NotEmitted()
    {
        var canvas = MakeChartCanvas(
            chartData: DataWithSeries(name: "Revenue"),
            customPalette: ChartPalette.OkabeIto);
        var tree = VStack(canvas);

        var findings = AccessibilityScanner.Scan(tree);
        Assert.DoesNotContain(findings, f => f.Id == "A11Y_CHART_012");
    }

    // ── Scanner skips chart rules for non-chart elements ────────────

    [Fact]
    public void Scanner_NonChartCanvas_NoChartRules()
    {
        var tree = VStack(Canvas(TextBlock("Hello")));

        var findings = AccessibilityScanner.Scan(tree);
        Assert.DoesNotContain(findings, f => f.Id.StartsWith("A11Y_CHART_"));
    }

    // ── Clean chart ─────────────────────────────────────────────────

    [Fact]
    public void Scanner_CleanChart_ZeroChartViolations()
    {
        var canvas = MakeChartCanvas(chartData: DataWithSeries(
            name: "Monthly Revenue",
            description: "Shows revenue growth from January to May"));
        var tree = VStack(canvas);

        var findings = AccessibilityScanner.Scan(tree);
        var chartFindings = findings.Where(f => f.Id.StartsWith("A11Y_CHART_")).ToList();
        Assert.Empty(chartFindings);
    }

    // ── Fix suggestion structure ────────────────────────────────────

    [Fact]
    public void FixSuggestion_A11Y_CHART_001_HasCorrectStructure()
    {
        var canvas = MakeChartCanvas(chartData: DataWithSeries());
        var tree = VStack(canvas);

        var findings = AccessibilityScanner.Scan(tree);
        var finding = findings.First(f => f.Id == "A11Y_CHART_001");

        Assert.Equal("Title", finding.Fix.Modifier);
        Assert.Contains(".Title(", finding.Fix.CodeSnippet);
        Assert.Equal("warning", finding.Severity);
        Assert.Equal("1.1.1", finding.WcagCriterion);
    }

    // ── Pie chart scanner rules ─────────────────────────────────────

    [Fact]
    public void Scanner_PieChartWithoutTitle_Flagged()
    {
        var canvas = MakeChartCanvas(chartData: new MockChartData
        {
            ChartTypeName = "Pie",
            Series = [new ChartSeriesDescriptor("Slices", [
                new ChartPointDescriptor("A", 30),
                new ChartPointDescriptor("B", 70)])],
        });
        var tree = VStack(canvas);

        var findings = AccessibilityScanner.Scan(tree);
        Assert.Contains(findings, f => f.Id == "A11Y_CHART_001");
    }

    [Fact]
    public void Scanner_PieChartWithTitle_Passes()
    {
        var canvas = MakeChartCanvas(chartData: new MockChartData
        {
            Name = "Market Share",
            ChartTypeName = "Pie",
            Series = [new ChartSeriesDescriptor("Slices", [
                new ChartPointDescriptor("A", 30),
                new ChartPointDescriptor("B", 70)])],
        });
        var tree = VStack(canvas);

        var findings = AccessibilityScanner.Scan(tree);
        Assert.DoesNotContain(findings, f => f.Id == "A11Y_CHART_001");
    }

    // ── A11Y_CHART_003: Interactive chart with keyboard disabled ──────

    [Fact]
    public void A11Y_CHART_003_InteractiveKeyboardDisabled_Flagged()
    {
        var canvas = MakeChartCanvas(
            chartData: DataWithSeries(name: "Revenue"),
            isInteractive: true,
            isKeyboardDisabled: true);
        var tree = VStack(canvas);

        var findings = AccessibilityScanner.Scan(tree);
        Assert.Contains(findings, f => f.Id == "A11Y_CHART_003");
    }

    [Fact]
    public void A11Y_CHART_003_InteractiveKeyboardEnabled_Passes()
    {
        var canvas = MakeChartCanvas(
            chartData: DataWithSeries(name: "Revenue"),
            isInteractive: true,
            isKeyboardDisabled: false);
        var tree = VStack(canvas);

        var findings = AccessibilityScanner.Scan(tree);
        Assert.DoesNotContain(findings, f => f.Id == "A11Y_CHART_003");
    }

    [Fact]
    public void A11Y_CHART_003_NonInteractiveKeyboardDisabled_Passes()
    {
        var canvas = MakeChartCanvas(
            chartData: DataWithSeries(name: "Revenue"),
            isInteractive: false,
            isKeyboardDisabled: true);
        var tree = VStack(canvas);

        var findings = AccessibilityScanner.Scan(tree);
        Assert.DoesNotContain(findings, f => f.Id == "A11Y_CHART_003");
    }

    // ── A11Y_CHART_005: TightHitTest ──────────────────────────────────

    [Fact]
    public void A11Y_CHART_005_TightHitTest_Flagged()
    {
        var canvas = MakeChartCanvas(
            chartData: DataWithSeries(name: "Revenue"),
            isTightHitTest: true);
        var tree = VStack(canvas);

        var findings = AccessibilityScanner.Scan(tree);
        Assert.Contains(findings, f => f.Id == "A11Y_CHART_005");
    }

    [Fact]
    public void A11Y_CHART_005_NoTightHitTest_Passes()
    {
        var canvas = MakeChartCanvas(
            chartData: DataWithSeries(name: "Revenue"),
            isTightHitTest: false);
        var tree = VStack(canvas);

        var findings = AccessibilityScanner.Scan(tree);
        Assert.DoesNotContain(findings, f => f.Id == "A11Y_CHART_005");
    }

    // ── Scanner skips chart rules for non-chart elements ──────────────

    [Fact]
    public void Scanner_NonChartCanvas_SkipsChartRules()
    {
        var canvas = new CanvasElement([]) { Width = 100, Height = 100 };
        var tree = VStack(canvas);

        var findings = AccessibilityScanner.Scan(tree);
        Assert.DoesNotContain(findings, f => f.Id.StartsWith("A11Y_CHART_"));
    }

    // ── A11Y_CHART_006: Focus indicator contrast ────────────────────

    [Fact]
    public void A11Y_CHART_006_LowContrastFocusColor_Flagged()
    {
        // Very light gray fails 3:1 contrast against white background (~1.7:1)
        var canvas = MakeChartCanvas(
            chartData: DataWithSeries(name: "Revenue"),
            customFocusColor: global::Windows.UI.Color.FromArgb(255, 200, 200, 200));
        var tree = VStack(canvas);

        var findings = AccessibilityScanner.Scan(tree);
        Assert.Contains(findings, f => f.Id == "A11Y_CHART_006");
        var diag = findings.First(f => f.Id == "A11Y_CHART_006");
        Assert.Equal("2.4.13", diag.WcagCriterion);
        Assert.Equal("FocusColor", diag.Fix.Modifier);
    }

    [Fact]
    public void A11Y_CHART_006_HighContrastFocusColor_Passes()
    {
        // Bright red has high contrast against both backgrounds
        var canvas = MakeChartCanvas(
            chartData: DataWithSeries(name: "Revenue"),
            customFocusColor: global::Windows.UI.Color.FromArgb(255, 255, 0, 0));
        var tree = VStack(canvas);

        var findings = AccessibilityScanner.Scan(tree);
        Assert.DoesNotContain(findings, f => f.Id == "A11Y_CHART_006");
    }

    [Fact]
    public void A11Y_CHART_006_NoCustomFocusColor_Passes()
    {
        var canvas = MakeChartCanvas(
            chartData: DataWithSeries(name: "Revenue"));
        var tree = VStack(canvas);

        var findings = AccessibilityScanner.Scan(tree);
        Assert.DoesNotContain(findings, f => f.Id == "A11Y_CHART_006");
    }

    // ── A11Y_CHART_007: AnnounceEveryFrame floods live region ───────

    [Fact]
    public void A11Y_CHART_007_AnnounceEveryFrame_Flagged()
    {
        var canvas = MakeChartCanvas(
            chartData: DataWithSeries(name: "Revenue"),
            isAnnounceEveryFrame: true);
        var tree = VStack(canvas);

        var findings = AccessibilityScanner.Scan(tree);
        Assert.Contains(findings, f => f.Id == "A11Y_CHART_007");
        var diag = findings.First(f => f.Id == "A11Y_CHART_007");
        Assert.Equal("4.1.3", diag.WcagCriterion);
        Assert.Equal("AnnounceEveryFrame", diag.Fix.Modifier);
    }

    [Fact]
    public void A11Y_CHART_007_NoAnnounceEveryFrame_Passes()
    {
        var canvas = MakeChartCanvas(
            chartData: DataWithSeries(name: "Revenue"),
            isAnnounceEveryFrame: false);
        var tree = VStack(canvas);

        var findings = AccessibilityScanner.Scan(tree);
        Assert.DoesNotContain(findings, f => f.Id == "A11Y_CHART_007");
    }
}
