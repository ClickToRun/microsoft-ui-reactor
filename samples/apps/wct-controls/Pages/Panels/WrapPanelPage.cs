namespace WctControls;

internal sealed class WrapPanelPage : Component
{
    public override Element Render() =>
        Gallery.Page(
            "WrapPanel",
            "A panel that wraps children onto new lines once they run out of room, with Horizontal/Vertical spacing.",
            WrapPanel(
                horizontalSpacing: 8, verticalSpacing: 8,
                children: new Element[]
                {
                    Gallery.Box("#90CAF9", "One").Width(90), Gallery.Box("#A5D6A7", "Two").Width(120),
                    Gallery.Box("#FFCC80", "Three").Width(80), Gallery.Box("#CE93D8", "Four").Width(140),
                    Gallery.Box("#80CBC4", "Five").Width(100), Gallery.Box("#EF9A9A", "Six").Width(110),
                })
                .Width(360));
}
