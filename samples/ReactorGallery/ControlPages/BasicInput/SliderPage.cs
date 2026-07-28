using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using static Microsoft.UI.Reactor.Factories;
using static WinUIGalleryReactor.SamplePageHost;

namespace WinUIGalleryReactor.ControlPages.BasicInput;

class SliderPage: Component
{
    public override Element Render()
    {
        var (value, setValue) = UseState(50.0);
        var (stepValue, setStepValue) = UseState(0.0);
        var (verticalValue, setVerticalValue) = UseState(30.0);

        return ScrollView(VStack(16,
            PageHeader("Slider", "A control that lets the user select from a range of values by moving a thumb along a track."),

            SampleCard("Basic Slider",
                VStack(8,
                    Slider(value, 0, 100, v => setValue(v)).Header("Volume"),
                    TextBlock($"Value: {value:F0}").Foreground(Theme.SecondaryText)),
                sourceCode: @"
Slider(value, 0, 100, v => setValue(v)).Header(""Volume"")
"),

            SampleCard("Slider with Step Frequency",
                VStack(8,
                    Slider(stepValue, 0, 100, v => setStepValue(v)).StepFrequency(10).Header("Step by 10"),
                    TextBlock($"Value: {stepValue:F0}").Foreground(Theme.SecondaryText)),
                sourceCode: @"
Slider(stepValue, 0, 100, v => setStepValue(v)).StepFrequency(10).Header(""Step by 10"")
"),

            SampleCard("Vertical Slider",
                Slider(verticalValue, 0, 100, v => setVerticalValue(v))
                    .Orientation(Orientation.Vertical)
                    .Height(200),
                sourceCode: @"
Slider(verticalValue, 0, 100, v => setVerticalValue(v))
    .Orientation(Orientation.Vertical)
    .Height(200)
")
        ).Margin(36, 24, 36, 36));
    }
}
