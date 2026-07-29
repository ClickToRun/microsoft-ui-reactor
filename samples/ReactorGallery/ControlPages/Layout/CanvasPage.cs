using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Layout;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using static Microsoft.UI.Reactor.Factories;
using static WinUIGalleryReactor.SamplePageHost;

namespace WinUIGalleryReactor;

class CanvasPage : Component
{
    public override Element Render()
    {
        var (offsetX, setOffsetX) = UseState(50.0);
        var (offsetY, setOffsetY) = UseState(30.0);

        // Shapes are painted with Fill, not Background — Background is only applied to
        // Panel / Control / Border, so it is silently dropped on a Rectangle.
        var coral = BrushHelper.Parse("#FF6B6B");
        var teal = BrushHelper.Parse("#4ECDC4");
        var sky = BrushHelper.Parse("#45B7D1");

        return ScrollView(
            VStack(16,
                PageHeader("Canvas", "Supports absolute positioning of child elements."),

                SampleCard("Absolute Positioning",
                    Border(
                        Canvas(
                            Rectangle().Size(80, 80).Fill(coral).Canvas(left: 10, top: 10),
                            Rectangle().Size(80, 80).Fill(teal).Canvas(left: 60, top: 50),
                            Rectangle().Size(80, 80).Fill(sky).Canvas(left: 110, top: 90)
                        )
                    ).Size(250, 200).Background(Theme.SubtleFill).CornerRadius(ThemeResource.CornerRadius("ControlCornerRadius").TopLeft),
                    """
                    // Shapes paint with Fill — Background has no effect on a Rectangle.
                    Canvas(
                        Rectangle().Size(80, 80).Fill(coralBrush).Canvas(left: 10, top: 10),
                        Rectangle().Size(80, 80).Fill(tealBrush).Canvas(left: 60, top: 50)
                    )
                    """),

                SampleCard("Interactive Positioning",
                    VStack(8,
                        Border(
                            Canvas(
                                Border(TextBlock("Drag me!").Center().Foreground("#FFFFFF"))
                                    .Size(100, 40).Background("#5B6ABF").CornerRadius(ThemeResource.CornerRadius("OverlayCornerRadius").TopLeft)
                                    .Canvas(left: offsetX, top: offsetY)
                            )
                        ).Size(300, 150).Background(Theme.SubtleFill).CornerRadius(ThemeResource.CornerRadius("ControlCornerRadius").TopLeft)
                    ),
                    @"Border(TextBlock(""Move"")).Canvas(left: offsetX, top: offsetY)",
                    OptionPanel(
                        TextBlock("Left"), Slider(offsetX, 0, 200, setOffsetX),
                        TextBlock("Top"), Slider(offsetY, 0, 100, setOffsetY)
                    ))
            ).Margin(36, 24, 36, 36)
        );
    }
}
