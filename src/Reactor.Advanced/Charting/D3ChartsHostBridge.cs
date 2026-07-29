namespace Microsoft.UI.Reactor.Charting;

/// <summary>
/// Charting-side implementation of <see cref="Hosting.IChartingHostBridge"/>
/// (issue #498). Registered with <see cref="Hosting.ReactorHost"/> the first
/// time a chart element activates (see <see cref="ChartingRuntime"/>), so the
/// host can push forced-colors / reduced-motion state into
/// <see cref="D3Charts"/>'s thread-statics without ever statically referencing
/// a Charting type. Apps that never render a chart never register this bridge,
/// so the trimmer drops the entire <c>D3Color</c> / <c>ForcedColorsTheme</c> /
/// <c>D3Charts</c> chain (~7.8&#160;KB) from their AOT image.
/// </summary>
internal sealed class D3ChartsHostBridge : Hosting.IChartingHostBridge
{
    internal static readonly D3ChartsHostBridge Instance = new();

    public object? CaptureForcedColorsTheme() => ForcedColorsTheme.FromSystem();

    public void PushAccessibilityState(bool isForcedColors, bool isReducedMotion, object? forcedColorsTheme)
    {
        D3Charts.IsForcedColors = isForcedColors;
        D3Charts.IsReducedMotion = isReducedMotion;
        D3Charts.ForcedColors = forcedColorsTheme as ForcedColorsTheme;
    }
}
