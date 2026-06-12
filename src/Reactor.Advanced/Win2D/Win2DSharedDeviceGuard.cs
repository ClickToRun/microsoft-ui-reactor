namespace Microsoft.UI.Reactor.Advanced.Win2D;

/// <summary>
/// Shared guard for the Win2D canvas handlers' <c>UseSharedDevice</c> contract.
/// </summary>
internal static class Win2DSharedDeviceGuard
{
    /// <summary>
    /// <c>UseSharedDevice</c> is a device-construction setting: Win2D evaluates it once when the
    /// control first realizes its device. Changing it on a live control forces an in-place device
    /// recreation that can race the render/teardown and crash (observed intermittent access
    /// violation). Handlers set it only at mount; this verifies the value did not change across a
    /// re-render. In release builds the new value is intentionally ignored (the control keeps its
    /// mount-time device), so a stray toggle is stable rather than crash-prone. In debug builds it
    /// throws a clear, actionable message instead of failing later with a native crash.
    /// </summary>
    public static void EnsureUseSharedDeviceUnchanged(bool oldValue, bool newValue)
    {
#if DEBUG
        if (oldValue != newValue)
        {
            throw new InvalidOperationException(
                "Win2D canvas UseSharedDevice cannot change after mount: it is a device-construction " +
                "setting and toggling it on a live control triggers an in-place device recreation that " +
                "can crash. Remount the canvas (e.g. via a different key) to switch devices. See " +
                "docs/guide/win2d-canvas.md#shared-device.");
        }
#else
        _ = oldValue;
        _ = newValue;
#endif
    }
}
