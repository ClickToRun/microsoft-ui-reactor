namespace WctControls;

internal sealed class ConstrainedBoxPage : Component
{
    public override Element Render() =>
        Gallery.Page(
            "ConstrainedBox",
            "A ContentPresenter that constrains its single child to an aspect ratio (or pixel multiple). Here the child is locked to 16:9 regardless of available width.",
            ConstrainedBox(
                aspectRatio: new CommunityToolkit.WinUI.Controls.AspectRatio(16, 9),
                content: Gallery.Box("#B39DDB", "16 : 9"))
                .Width(360));
}
