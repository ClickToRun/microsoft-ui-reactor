namespace WctControls;

internal sealed class HeaderedItemsPage : Component
{
    public override Element Render() =>
        Gallery.Page(
            "HeaderedItemsControl",
            "An ItemsControl with a Header. Items flow through the generated items slot — strings pass straight through, Element items mount via the reconciler.",
            HeaderedItemsControl(
                header: "Fruits",
                items: new object[] { "Apple", "Banana", "Cherry", "Date" })
                .Width(320));
}
