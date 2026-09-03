namespace WctControls;

internal sealed class GaugePage : Component
{
    public override Element Render()
    {
        var (volume, setVolume) = UseState(35.0);

        return Gallery.Page(
            "RadialGauge",
            "A circular gauge whose Value is a two-way controlled prop, driven live by a Reactor Slider.",
            SettingsCard(
                header: "Volume",
                description: $"{volume:0} %",
                content: HStack(16,
                    Slider(value: volume, min: 0, max: 100, onValueChanged: setVolume).Width(240),
                    RadialGauge(
                        value: volume, minimum: 0, maximum: 100, unit: "%",
                        onValueChanged: setVolume).Size(132, 132))));
    }
}
