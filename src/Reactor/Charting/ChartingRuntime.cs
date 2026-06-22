namespace Microsoft.UI.Reactor.Charting;

/// <summary>
/// Charting-side activation entry point (issue #498). Called by chart elements
/// and D3 primitives the first time a chart enters the tree. On first call it
/// registers the Charting subsystem's host bridge and accessibility-scanner
/// extension with the core, then delegates to
/// <see cref="Hosting.ChartingActivation.RequestActivation"/> to lazily spin up
/// the host's WinRT accessibility settings.
/// <para>
/// Routing registration through this single method (instead of the core naming
/// the concrete Charting types) is what lets the trimmer drop the entire
/// charting accessibility chain from chart-free AOT builds.
/// </para>
/// </summary>
internal static class ChartingRuntime
{
    private static int s_registered;

    internal static void Activate()
    {
        if (global::System.Threading.Interlocked.Exchange(ref s_registered, 1) == 0)
        {
            Hosting.ReactorHost.RegisterChartingBridge(D3ChartsHostBridge.Instance);
            Core.AccessibilityScanner.RegisterScanExtension(Accessibility.ChartAccessibilityChecker.Instance);
        }

        Hosting.ChartingActivation.RequestActivation();
    }
}
