namespace WctControls;

internal sealed class DockPanelPage : Component
{
    public override Element Render() =>
        Gallery.Page(
            "DockPanel",
            "A Panel that docks children to its edges. Children flow through the generated Panel slot; the per-child Dock is an attached property (set via a layout modifier, not generated), so here they dock in order with LastChildFill filling the remainder.",
            DockPanel(
                lastChildFill: true,
                children: new Element[]
                {
                    Gallery.Box("#EF9A9A", "A", 80),
                    Gallery.Box("#FFE082", "B", 80),
                    Gallery.Box("#A5D6A7", "Fills", 80),
                })
                .Height(200));
}
