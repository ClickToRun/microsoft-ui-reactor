namespace WctControls;

internal sealed class CameraPage : Component
{
    public override Element Render()
    {
        var (camError, setCamError) = UseState<string?>(null);

        return Gallery.Page(
            "CameraPreview",
            "An imperative WCT control — its StartAsync-on-mount / Stop-on-unmount lifecycle is declared once via [WrapLifecycle], so the call site is a plain declarative element. PreviewFailed surfaces via OnPreviewFailed.",
            SettingsCard(
                header: "Live camera",
                description: camError is null ? "Streaming (or starting…)" : $"Unavailable — {camError}",
                content: CameraPreview(
                    isFrameSourceGroupButtonVisible: true,
                    onPreviewFailed: setCamError).Size(440, 280)));
    }
}
