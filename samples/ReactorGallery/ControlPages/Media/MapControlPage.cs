using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using static Microsoft.UI.Reactor.Factories;
using static WinUIGalleryReactor.SamplePageHost;

namespace WinUIGalleryReactor.ControlPages.Media;

class MapControlPage : Component
{
    /// <summary>
    /// Maps tiles are a metered service, so no token ships with the gallery. Set this
    /// environment variable to your own key to see the live control.
    /// </summary>
    const string TokenVariable = "REACTOR_GALLERY_MAP_TOKEN";

    public override Element Render()
    {
        // Without a token MapControl renders an empty white viewport with no error of any
        // kind, which reads as a broken sample. Detect the missing token and say so.
        var token = Environment.GetEnvironmentVariable(TokenVariable);
        var hasToken = !string.IsNullOrWhiteSpace(token);

        Element map = hasToken
            ? MapControl(mapServiceToken: token, zoomLevel: 4).Height(320).Width(480)
                .Background(Theme.SubtleFill).CornerRadius(6)
            : Border(
                VStack(8,
                    BodyStrong("No map service token configured"),
                    TextBlock($"MapControl downloads its tiles from a metered maps service, so the gallery ships without a key and the map would otherwise render as a blank white rectangle.")
                        .TextWrapping().Foreground(Theme.SecondaryText),
                    TextBlock($"Set the {TokenVariable} environment variable to your own key and restart the gallery to see the live control:")
                        .TextWrapping().Foreground(Theme.SecondaryText),
                    SourceBlock($"setx {TokenVariable} \"<your-maps-key>\"")
                ).Padding(16))
                .Height(320).Width(480)
                .Background(Theme.SubtleFill)
                .WithBorder(Theme.CardStroke)
                .CornerRadius(6);
        return ScrollView(VStack(16,
            PageHeader("MapControl", "Displays an interactive map. Tiles require a maps service token."),

            SampleCard("Interactive map",
                VStack(8,
                    map,
                    Caption(hasToken
                        ? $"Rendering with the token from {TokenVariable}. Pan and zoom with mouse or touch."
                        : $"Showing the no-token placeholder. Set {TokenVariable} to render the live map.")
                        .Foreground(Theme.SecondaryText)),
                sourceCode: @"
// A token is required — without one the control renders an empty viewport and
// reports no error, so check for it and explain the gap instead.
var token = Environment.GetEnvironmentVariable(""REACTOR_GALLERY_MAP_TOKEN"");

Element map = string.IsNullOrWhiteSpace(token)
    ? Border(VStack(8, BodyStrong(""No map service token configured""), ...)).Height(320).Width(480)
    : MapControl(mapServiceToken: token, zoomLevel: 4).Height(320).Width(480);

// Pan and zoom with mouse/touch. Center and layers can be set via
//   .Set(map => { map.Center = ...; })
")
        ).Margin(36, 24, 36, 36));
    }
}
