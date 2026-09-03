namespace WctControls;

internal sealed class StaggeredPanelPage : Component
{
    public override Element Render() =>
        Gallery.Page(
            "StaggeredPanel",
            "A Pinterest-style panel: children flow top-to-bottom into the shortest column. DesiredColumnWidth drives the column count.",
            StaggeredPanel(
                desiredColumnWidth: 110, columnSpacing: 8, rowSpacing: 8,
                children: new Element[]
                {
                    Gallery.Box("#90CAF9", "1", 60), Gallery.Box("#A5D6A7", "2", 120),
                    Gallery.Box("#FFCC80", "3", 90), Gallery.Box("#CE93D8", "4", 70),
                    Gallery.Box("#80CBC4", "5", 110), Gallery.Box("#EF9A9A", "6", 80),
                })
                .Width(360).Height(300));
}
