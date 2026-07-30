using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using static Microsoft.UI.Reactor.Factories;
using static WinUIGalleryReactor.SamplePageHost;

namespace WinUIGalleryReactor.ControlPages.Media;

class ImagePage : Component
{
    // Ships with the app: samples/ReactorGallery/Assets/SampleImages (copied to the
    // output folder by the csproj, which is what ms-appx:/// resolves against).
    const string SampleImage = "ms-appx:///Assets/SampleImages/LandscapeSample.png";

    // Images carry no accessible name of their own, so screen readers need one
    // supplied (REACTOR_A11Y_002). Decorative images use .AccessibilityHidden() instead.
    const string SampleImageAltText = "A stylised mountain lake at dusk";

    public override Element Render()
    {
        var (width, setWidth) = UseState(300.0);

        return ScrollView(
            VStack(16,
                PageHeader("Image",
                    "A control that displays an image from a file or URI."),

                SampleCard("Image from URI",
                    Image(SampleImage)
                        .AutomationName(SampleImageAltText)
                        .Width(width).Height(width),
                    @"Image(""ms-appx:///Assets/SampleImages/LandscapeSample.png"")
    .AutomationName(""A stylised mountain lake at dusk"")   // alt text for screen readers
    .Width(300).Height(300)
// ms-appx:/// resolves against the app folder — the asset must be copied
// to the output directory (a <Content Include=...> item in the csproj).",
                    options: OptionPanel(
                        Slider(width, 50, 500, v => setWidth(v)).AutomationName("Image width")
                    )),

                SampleCard("Image with Border",
                    Border(
                        Image(SampleImage)
                            .AutomationName(SampleImageAltText)
                            .Width(200).Height(200)
                    ).CornerRadius(ThemeResource.CornerRadius("OverlayCornerRadius").TopLeft)
                     .WithBorder(Theme.CardStroke),
                    @"Border(
    Image(""ms-appx:///Assets/SampleImages/LandscapeSample.png"")
        .AutomationName(""A stylised mountain lake at dusk"")
        .Width(200).Height(200)
).CornerRadius(12)
 .WithBorder(Theme.CardStroke)")
            ).Margin(36, 24, 36, 36)
        );
    }
}
