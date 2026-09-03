namespace WctControls;

internal static class Gallery
{
    public static Element Page(string title, string subtitle, Element body) =>
        ScrollView(VStack(16, Heading(title), Caption(subtitle), body)).Margin(28);

    public static Element Box(string color, string label, double height = 64) =>
        Border(TextBlock(label).Center())
            .Background(color)
            .CornerRadius(6)
            .Height(height);
}
