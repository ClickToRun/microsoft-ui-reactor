namespace WctControls;

internal sealed class SegmentedPage : Component
{
    public override Element Render()
    {
        var (choice, setChoice) = UseState(0);
        string[] views = { "List", "Grid", "Details" };

        return Gallery.Page(
            "Segmented",
            "A single-choice selector. SelectionChanged is bound two-way to SelectedIndex via [WrapControlled].",
            VStack(12,
                Segmented(
                    selectedIndex: choice,
                    onSelectedIndexChanged: setChoice,
                    items: new object[] { "List", "Grid", "Details" }),
                Caption($"Selected view: {views[choice]}")));
    }
}
