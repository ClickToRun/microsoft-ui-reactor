namespace WctControls;

internal sealed class ColorPickerPage : Component
{
    public override Element Render()
    {
        var (color, setColor) = UseState(Microsoft.UI.Colors.DodgerBlue);
        string hex = $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";

        return Gallery.Page(
            "ColorPicker",
            "A full color picker. Color ↔ ColorChanged follows the {Prop}Changed convention, so Color is a two-way controlled prop.",
            VStack(12,
                SettingsCard(header: "Selected", description: hex),
                ColorPicker(
                    color: color,
                    onColorChanged: setColor,
                    isAlphaEnabled: true)));
    }
}
