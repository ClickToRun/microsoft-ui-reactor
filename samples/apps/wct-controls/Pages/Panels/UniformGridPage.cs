namespace WctControls;

internal sealed class UniformGridPage : Component
{
    public override Element Render() =>
        Gallery.Page(
            "UniformGrid",
            "A Grid-derived panel that arranges children into equal-sized cells. Columns/Rows drive the shape; children fill cells in order.",
            UniformGridElement.UniformGrid(
                columns: 3, rows: 2,
                children: new Element[]
                {
                    Gallery.Box("#90CAF9", "1"), Gallery.Box("#A5D6A7", "2"), Gallery.Box("#FFCC80", "3"),
                    Gallery.Box("#CE93D8", "4"), Gallery.Box("#80CBC4", "5"), Gallery.Box("#EF9A9A", "6"),
                })
                .Width(360));
}
