namespace WctControls;

internal sealed class HeaderedContentPage : Component
{
    public override Element Render() =>
        Gallery.Page(
            "HeaderedContentControl",
            "A ContentControl with a Header (an object? value prop) above its single content child.",
            HeaderedContentControl(
                header: "Shipping address",
                content: Border(
                    VStack(6,
                        TextBlock("1 Microsoft Way"),
                        TextBlock("Redmond, WA 98052")))
                    .Background("AliceBlue").CornerRadius(8).Margin(0))
                .Width(360));
}
