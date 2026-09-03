namespace WctControls;

internal sealed class MetadataPage : Component
{
    private static readonly CommunityToolkit.WinUI.Controls.MetadataItem[] Items =
    {
        new() { Label = "By Megan Bowen" },
        new() { Label = "June 11, 2026" },
        new() { Label = "5 min read" },
        new() { Label = "Reactor, WinUI" },
    };

    public override Element Render() =>
        Gallery.Page(
            "MetadataControl",
            "A compact metadata line (author • date • tags) joined by a Separator. Its Items are a typed IEnumerable<MetadataItem> — surfaced as a declarative collection prop you just pass, no escape hatch.",
            MetadataControl(separator: " • ", items: Items));
}
