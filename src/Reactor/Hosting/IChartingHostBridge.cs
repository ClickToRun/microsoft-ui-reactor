namespace Microsoft.UI.Reactor.Hosting;

/// <summary>
/// Indirection seam (issue #498) that lets the Charting subsystem push
/// accessibility state (forced-colors / reduced-motion) into its own
/// thread-statics without the host statically referencing any Charting type.
/// <para>
/// The interface lives in core; the implementation lives in Charting and is
/// registered via <see cref="ReactorHost.RegisterChartingBridge"/> the first
/// time a chart element activates. Because core never names the concrete
/// implementation, the trimmer drops the entire Charting accessibility chain
/// (<c>D3Color</c>, <c>ForcedColorsTheme</c>, <c>D3Charts</c>) from apps that
/// never render a chart.
/// </para>
/// </summary>
internal interface IChartingHostBridge
{
    /// <summary>
    /// Captures the current system forced-colors theme as an opaque payload.
    /// Returns <c>null</c> when not in forced-colors mode or when the platform
    /// query is unavailable. The host stores the payload as <see cref="object"/>
    /// and round-trips it back through <see cref="PushAccessibilityState"/>.
    /// </summary>
    object? CaptureForcedColorsTheme();

    /// <summary>
    /// Pushes the host's accessibility state into the charting subsystem's
    /// thread-statics so the about-to-mount chart sees correct forced-colors /
    /// reduced-motion values. <paramref name="forcedColorsTheme"/> is the
    /// opaque payload previously produced by <see cref="CaptureForcedColorsTheme"/>.
    /// </summary>
    void PushAccessibilityState(bool isForcedColors, bool isReducedMotion, object? forcedColorsTheme);
}
