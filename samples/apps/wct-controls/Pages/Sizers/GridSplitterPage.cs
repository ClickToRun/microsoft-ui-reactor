namespace WctControls;

internal sealed class GridSplitterPage : Component
{
    public override Element Render() =>
        Gallery.Page(
            "GridSplitter",
            "A draggable splitter (from the Sizers package). Drag the bar to resize the two panes.",
            Grid(
                columns: new[] { GridSize.Star(), GridSize.Px(11), GridSize.Star() },
                rows: new[] { GridSize.Star() },
                Pane("Left").Grid(row: 0, column: 0),
                GridSplitter().Grid(row: 0, column: 1),
                Pane("Right").Grid(row: 0, column: 2))
            .Height(260));

    private static Element Pane(string label) =>
        Border(TextBlock(label).Center())
            .Background("AliceBlue")
            .CornerRadius(8);
}
