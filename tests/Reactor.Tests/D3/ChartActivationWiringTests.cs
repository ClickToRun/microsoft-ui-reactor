using Microsoft.UI.Reactor.Charting;
using Microsoft.UI.Reactor.Charting.Accessibility;
using Microsoft.UI.Reactor.Core;
using Xunit;
using static Microsoft.UI.Reactor.Factories;

namespace Microsoft.UI.Reactor.Tests.D3;

/// <summary>
/// Regression pin for issue #498 (review M2): the chart accessibility rules now
/// live in the Charting subsystem and are contributed to the core scanner only
/// when the charting runtime activates. Activation is therefore load-bearing for
/// accessibility correctness — if a chart entry path stopped funneling through
/// <see cref="ChartingRuntime.Activate"/>, chart a11y diagnostics would silently
/// disappear.
/// <para>
/// Unlike <c>ChartScannerRuleTests</c>, this class deliberately performs **no**
/// manual <c>AccessibilityScanner.RegisterScanExtension(...)</c> call. It drives
/// the same production activation funnel that the chart DSL uses internally (the
/// implicit <c>operator Element</c> and the <c>D3Charts</c> static constructor
/// both call <see cref="ChartingRuntime.Activate"/>), then asserts the core
/// scanner emits chart diagnostics — proving activation wires the extension
/// end-to-end.
/// </para>
/// </summary>
public class ChartActivationWiringTests
{
    private sealed class MockChartData : IChartAccessibilityData
    {
        public string? Name { get; init; }
        public string? Description { get; init; }
        public IReadOnlyList<ChartSeriesDescriptor> Series { get; init; } = [];
        public IReadOnlyList<ChartAxisDescriptor> Axes { get; init; } = [];
        public ChartViewport? Viewport { get; init; }
        public string ChartTypeName { get; init; } = "Line";
    }

    private static MockChartData DataWithSeries()
    {
        var points = Enumerable.Range(0, 5)
            .Select(i => new ChartPointDescriptor(i.ToString(), (i + 1) * 10.0))
            .ToArray();
        return new MockChartData
        {
            Series = [new ChartSeriesDescriptor("Series 1", points)],
            Axes = [
                new ChartAxisDescriptor(ChartAxisType.X, "X", 0, 4),
                new ChartAxisDescriptor(ChartAxisType.Y, "Y", 10, 50),
            ],
        };
    }

    [Fact]
    public void Activate_RegistersScanExtension_SoChartDiagnosticsAreProduced()
    {
        // The production activation entry point — what every chart construction
        // funnels through. No manual scanner registration anywhere in this class.
        ChartingRuntime.Activate();

        // An untitled chart should trip A11Y_CHART_001 ("chart has no
        // Title/AutomationName"). It can only fire if Activate() contributed the
        // chart accessibility checker to the core scanner.
        var canvas = (CanvasElement)new CanvasElement([]) { Width = 400, Height = 300 }
            .SetAttached(new ChartA11yData(DataWithSeries()));
        var tree = VStack(canvas);

        var findings = AccessibilityScanner.Scan(tree);

        Assert.Contains(findings, f => f.Id == "A11Y_CHART_001");
    }
}
