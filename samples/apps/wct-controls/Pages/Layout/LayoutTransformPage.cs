namespace WctControls;

internal sealed class LayoutTransformPage : Component
{
    public override Element Render()
    {
        var (angle, setAngle) = UseState(20.0);

        return Gallery.Page(
            "LayoutTransformControl",
            "Applies a render Transform to its single Child while still affecting layout. Transform is a one-way prop, driven live here by a Reactor Slider.",
            VStack(16,
                LayoutTransformControl(
                    transform: new Microsoft.UI.Xaml.Media.RotateTransform { Angle = angle },
                    content: Gallery.Box("#FFB74D", "Rotated", 90).Width(180)),
                Slider(value: angle, min: 0, max: 360, onValueChanged: setAngle).Width(300),
                Caption($"Angle: {angle:0}°")));
    }
}
