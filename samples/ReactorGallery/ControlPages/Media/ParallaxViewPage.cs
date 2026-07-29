using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using static Microsoft.UI.Reactor.Factories;
using static WinUIGalleryReactor.SamplePageHost;

namespace WinUIGalleryReactor.ControlPages.Media;

class ParallaxViewPage : Component
{
    // The card is deliberately fixed-size. ParallaxView arranges its child at the child's
    // own desired size, so a background with no explicit width collapses to the width of
    // its text and ends up beside the list instead of behind it.
    const double CardWidth = 480;
    const double CardHeight = 300;
    const double BandHeight = 190;
    const double VerticalShift = 150;

    static Element Band(string label, string color) =>
        Border(TextBlock(label).FontSize(22).SemiBold().Foreground("#FFFFFF").Center())
            .Width(CardWidth).Height(BandHeight).Background(color);

    public override Element Render()
    {
        // ParallaxView does nothing until its Source is bound to a scroller. We
        // capture the foreground ListView on mount, then feed it back in.
        var (scroller, setScroller) = UseState<UIElement?>(null);

        // Taller than the viewport plus the shift (3 x 190 = 570 > 300 + 150), so there is
        // always background left to slide into view.
        var background = Border(VStack(0,
            Band("▲  TOP", "#0F6CBD"),
            Band("●  MIDDLE", "#8764B8"),
            Band("▼  BOTTOM", "#C0397E")))
            .Width(CardWidth).Height(BandHeight * 3);

        var parallax = ParallaxView(background, verticalShift: VerticalShift);
        if (scroller is not null)
            parallax = parallax.Source(scroller);

        // Transparent list so the parallaxing background shows through behind the rows;
        // each row keeps its own scrim so white text stays legible over every band.
        var list = ListView(
            Enumerable.Range(1, 24)
                .Select(i => (Element)Border(
                        TextBlock($"Row {i}").Foreground("#FFFFFF").SemiBold().FontSize(16))
                    .Background("#59000000")
                    .CornerRadius(4)
                    .Padding(12, 8, 12, 8))
                .ToArray())
            .Set(lv => lv.Background = null)
            .OnMount(el => setScroller((UIElement)el));

        return ScrollView(VStack(16,
            PageHeader("ParallaxView", "Shifts a background layer as a foreground surface scrolls, creating a depth effect."),

            SampleCard("Background parallax behind a scrolling list",
                VStack(8,
                    Grid(
                        columns: [GridSize.Star()], rows: [GridSize.Star()],
                        parallax.Grid(row: 0, column: 0),
                        list.Grid(row: 0, column: 0)
                    ).Width(CardWidth).Height(CardHeight).HAlign(HorizontalAlignment.Left),
                    Caption("Scroll the list — the coloured bands behind the rows drift at a different rate (ParallaxView.Source is bound to the list).")
                        .Foreground(Theme.SecondaryText)),
                sourceCode: @"
var (scroller, setScroller) = UseState<UIElement?>(null);

// `background` must be TALLER than the viewport (+ the shift) and have visible
// vertical structure — and it needs an explicit width, because ParallaxView
// arranges its child at the child's own desired size.
var background = Border(VStack(0, band1, band2, band3)).Width(480).Height(570);

var parallax = ParallaxView(background, verticalShift: 150);
if (scroller is not null) parallax = parallax.Source(scroller);

Grid(columns: [GridSize.Star()], rows: [GridSize.Star()],
    parallax.Grid(row: 0, column: 0),
    ListView(rows)
        .Set(lv => lv.Background = null)          // transparent → background shows through
        .OnMount(el => setScroller((UIElement)el)) // bind the scroller
        .Grid(row: 0, column: 0)).Width(480).Height(300)
")
        ).Margin(36, 24, 36, 36));
    }
}
