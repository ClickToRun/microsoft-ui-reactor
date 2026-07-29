using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using static Microsoft.UI.Reactor.Factories;
using static WinUIGalleryReactor.SamplePageHost;

namespace WinUIGalleryReactor.ControlPages.Media;

class MediaPlayerElementPage : Component
{
    // W3C's HTML5 media test clip (Sintel trailer, CC-BY Blender Foundation), served by
    // w3.org itself rather than a third-party CDN that can start refusing requests.
    const string SampleVideo = "https://media.w3.org/2010/05/sintel/trailer.mp4";

    public override Element Render()
    {
        // MediaPlayerElement renders its own opaque error text when a source fails to open.
        // Capture OnMediaFailed instead so the card can explain what actually happened.
        var (failure, setFailure) = UseState<string?>(null);

        Element player = failure is null
            ? (MediaPlayerElement(SampleVideo) with { OnMediaFailed = message => setFailure(message) })
                .Height(280).Width(480)
            : Border(
                VStack(8,
                    BodyStrong("Sample stream unavailable"),
                    TextBlock("This card streams its media over the network. The request failed — you are probably offline, or the sample URL is being blocked.")
                        .TextWrapping().Foreground(Theme.SecondaryText),
                    Caption(failure).TextWrapping().Foreground(Theme.SecondaryText),
                    Button("Try again", () => setFailure(null)).HAlign(HorizontalAlignment.Left)
                ).Padding(16))
                .Height(280).Width(480)
                .Background(Theme.SubtleFill)
                .WithBorder(Theme.CardStroke)
                .CornerRadius(ThemeResource.CornerRadius("ControlCornerRadius").TopLeft);

        return ScrollView(VStack(16,
            PageHeader("MediaPlayerElement", "Embeds audio/video playback with built-in transport controls."),

            SampleCard("Video with transport controls",
                VStack(8,
                    player,
                    Caption("Streams over the network — playback requires an internet connection.")
                        .Foreground(Theme.SecondaryText)),
                sourceCode: @"
var (failure, setFailure) = UseState<string?>(null);

failure is null
    ? (MediaPlayerElement(""https://media.w3.org/2010/05/sintel/trailer.mp4"")
        with { OnMediaFailed = message => setFailure(message) })
        .Height(280).Width(480)
    // Handle OnMediaFailed so a dead source explains itself instead of leaving
    // the player showing its opaque built-in error text.
    : Border(VStack(8, BodyStrong(""Sample stream unavailable""), ...))

// Transport controls are enabled by default; AutoPlay is opt-in:
//   .Set(mpe => mpe.AutoPlay = true)
")
        ).Margin(36, 24, 36, 36));
    }
}
