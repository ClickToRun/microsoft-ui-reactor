namespace WctControls;

internal sealed class HeaderedTreePage : Component
{
    public override Element Render() =>
        Gallery.Page(
            "HeaderedTreeView",
            "Derives from WinUI TreeView, so its hierarchy is data-bound through ItemsSource (not a flat Children/Items slot). Header + ItemsSource are surfaced as declarative props.",
            HeaderedTreeView(
                header: "Library",
                itemsSource: new[] { "Documents", "Pictures", "Music", "Videos" })
                .Width(320).Height(220));
}
