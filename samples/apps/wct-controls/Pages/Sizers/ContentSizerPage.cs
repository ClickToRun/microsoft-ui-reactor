namespace WctControls;

internal sealed class ContentSizerPage : Component
{
    public override Element Render() =>
        Gallery.Page(
            "ContentSizer",
            "A draggable sizer (Sizers package) that resizes its target content (vs GridSplitter, which resizes grid definitions). Drag the bar to resize the panel above it.",
            VStack(0,
                Gallery.Box("#FFCC80", "Resizable content — drag the bar below", 140),
                ContentSizer(orientation: Microsoft.UI.Xaml.Controls.Orientation.Horizontal))
                .Width(360));
}
