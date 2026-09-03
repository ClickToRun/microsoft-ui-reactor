namespace WctControls;

internal sealed class SwitchPresenterPage : Component
{
    public override Element Render()
    {
        var (choice, setChoice) = UseState(0);
        string[] states = { "Loading", "Ready", "Error" };

        return Gallery.Page(
            "SwitchPresenter",
            "Shows exactly one SwitchCase based on Value (a switch over UI). SwitchCase children are typed objects, not a generated slot, so here a Reactor switch drives the displayed content while Value mirrors the selection.",
            VStack(12,
                Segmented(
                    selectedIndex: choice, onSelectedIndexChanged: setChoice,
                    items: new object[] { "Loading", "Ready", "Error" }),
                SwitchPresenter(
                    value: states[choice],
                    content: Gallery.Box(
                        choice switch { 0 => "#FFE082", 1 => "#A5D6A7", _ => "#EF9A9A" },
                        states[choice], 100))));
    }
}
