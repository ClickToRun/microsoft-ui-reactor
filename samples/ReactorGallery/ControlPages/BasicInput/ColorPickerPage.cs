using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using static Microsoft.UI.Reactor.Factories;
using static WinUIGalleryReactor.SamplePageHost;

namespace WinUIGalleryReactor.ControlPages.BasicInput;

class ColorPickerPage: Component
{
    public override Element Render()
    {
        var (color, setColor) = UseState(global::Windows.UI.Color.FromArgb(255, 0, 120, 215));

        return ScrollView(VStack(16,
            PageHeader("ColorPicker", "A control that lets a user pick a color using a color spectrum, sliders, and text input."),

            SampleCard("Basic ColorPicker",
                VStack(8,
                    ColorPicker(color, c => setColor(c)),
                    Border(VStack())
                        .Width(64).Height(32)
                        .Background(new Microsoft.UI.Xaml.Media.SolidColorBrush(color))
                        .CornerRadius(ThemeResource.CornerRadius("ControlCornerRadius").TopLeft)),
                sourceCode: @"
ColorPicker(color, c => setColor(c))
")
        ).Margin(36, 24, 36, 36));
    }
}
