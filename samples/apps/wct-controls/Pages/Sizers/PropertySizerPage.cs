namespace WctControls;

internal sealed class PropertySizerPage : Component
{
    public override Element Render()
    {
        var (width, setWidth) = UseState(220.0);
        var token = UseRef(0L);

        return Gallery.Page(
            "PropertySizer",
            "A sizer that drives a single bound double dependency property (e.g. a pane width), clamped by Minimum/Maximum. Drag the bar to resize the pane — Binding is pushed from state one-way and its changes are observed back into state.",
            HStack(0,
                Gallery.Box("#A5D6A7", $"Pane — {width:0} px", 160).Width(width),
                PropertySizer(binding: width, minimum: 120, maximum: 360)
                    .Height(160)
                    .OnMount(fe => token.Current = ((CommunityToolkit.WinUI.Controls.PropertySizer)fe).RegisterPropertyChangedCallback(
                        CommunityToolkit.WinUI.Controls.PropertySizer.BindingProperty,
                        (s, _) => setWidth(((CommunityToolkit.WinUI.Controls.PropertySizer)s).Binding)))
                    .OnUnmount(fe => ((CommunityToolkit.WinUI.Controls.PropertySizer)fe).UnregisterPropertyChangedCallback(
                        CommunityToolkit.WinUI.Controls.PropertySizer.BindingProperty, token.Current))));
    }
}
