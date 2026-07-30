using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using static Microsoft.UI.Reactor.Factories;

namespace WinUIGalleryReactor;

class SettingsPage : Component
{
    public override Element Render()
    {
        return ScrollView(
            VStack(24,
                // Page header
                TextBlock("Settings")
                    .FontSize(28)
                    .Bold()
                    .Foreground(Theme.PrimaryText),

                Component<DeepLinkSettingsCard>(),

                // About section card
                Border(
                    VStack(12,
                        TextBlock("About this app")
                            .Foreground(Theme.PrimaryText)
                            .SemiBold(),

                        Border(VStack())
                            .Height(1)
                            .Background(Theme.DividerStroke),

                        HStack(16,
                            // App icon placeholder
                            Border(
                                TextBlock("\uE80F")
                                    .FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets")
                                    .FontSize(24)
                                    .Foreground(Theme.AccentText)
                                    .Center()
                            )
                            .Background(Theme.SubtleFill)
                            .CornerRadius(8)
                            .Width(48).Height(48),

                            VStack(2,
                                TextBlock("WinUI Gallery (Reactor)")
                                    .Foreground(Theme.PrimaryText)
                                    .SemiBold(),
                                TextBlock("Version 1.0")
                                    .Foreground(Theme.SecondaryText)
                                    .FontSize(12)
                            ).VAlign(VerticalAlignment.Center)
                        ),

                        TextBlock("This app is built with Reactor, a declarative component-based UI framework for WinUI 3. It demonstrates how to recreate the WinUI Gallery experience using reactive hooks and a composable element DSL.")
                            .Foreground(Theme.SecondaryText)
                            .FontSize(13)
                            .TextWrapping(TextWrapping.Wrap)
                    )
                )
                .Padding(20)
                .Background(Theme.CardBackground)
                .WithBorder(Theme.CardStroke)
                .CornerRadius(8)
                .MaxWidth(600),

                // Links section card
                Border(
                    VStack(12,
                        TextBlock("Links")
                            .Foreground(Theme.PrimaryText)
                            .SemiBold(),

                        Border(VStack())
                            .Height(1)
                            .Background(Theme.DividerStroke),

                        HyperlinkButton("Source code on GitHub",
                            new Uri("https://github.com/AhmedWaleed/WinUI-Gallery")),

                        HyperlinkButton("WinUI Gallery (original)",
                            new Uri("https://github.com/microsoft/WinUI-Gallery")),

                        HyperlinkButton("Fluent Design guidelines",
                            new Uri("https://learn.microsoft.com/en-us/windows/apps/design/"))
                    )
                )
                .Padding(20)
                .Background(Theme.CardBackground)
                .WithBorder(Theme.CardStroke)
                .CornerRadius(8)
                .MaxWidth(600),

                // Framework info card
                Border(
                    VStack(8,
                        TextBlock("Built with Reactor")
                            .Foreground(Theme.PrimaryText)
                            .SemiBold(),

                        Border(VStack())
                            .Height(1)
                            .Background(Theme.DividerStroke),

                        HStack(8,
                            TextBlock("Framework").Foreground(Theme.SecondaryText).FontSize(13).Width(120),
                            TextBlock("Reactor (declarative C# DSL)").Foreground(Theme.PrimaryText).FontSize(13)
                        ),
                        HStack(8,
                            TextBlock("Platform").Foreground(Theme.SecondaryText).FontSize(13).Width(120),
                            TextBlock("WinUI 3 / Windows App SDK").Foreground(Theme.PrimaryText).FontSize(13)
                        ),
                        HStack(8,
                            TextBlock("Rendering").Foreground(Theme.SecondaryText).FontSize(13).Width(120),
                            TextBlock("Virtual DOM reconciler").Foreground(Theme.PrimaryText).FontSize(13)
                        ),
                        HStack(8,
                            TextBlock("State").Foreground(Theme.SecondaryText).FontSize(13).Width(120),
                            TextBlock("React-style hooks").Foreground(Theme.PrimaryText).FontSize(13)
                        )
                    )
                )
                .Padding(20)
                .Background(Theme.CardBackground)
                .WithBorder(Theme.CardStroke)
                .CornerRadius(8)
                .MaxWidth(600)

            ).Margin(36, 24, 36, 48)
        );
    }
}

/// <summary>
/// Surfaces the <c>reactor-gallery://</c> scheme, and — for the unpackaged build only —
/// lets the user take back the HKCU registration the app makes on launch.
/// </summary>
/// <remarks>
/// Split out as its own component so the (stateful) toggle doesn't force the rest of
/// the settings page to re-render.
/// </remarks>
class DeepLinkSettingsCard : Component
{
    public override Element Render()
    {
        var (isRegistered, setIsRegistered) = UseState(GalleryProtocol.IsRegistered);

        var exampleUri = GalleryRoutes.UriForTag("button");

        // Packaged builds declare the scheme in Package.appxmanifest, so Windows
        // installs and removes it with the app. There is nothing for the user to do
        // and nothing they *could* do — an app can't revoke a manifest-declared
        // protocol — so the card simply shows no registration UI at all.
        var registration = GalleryProtocol.IsManagedByPackage
            ? null
            : VStack(8,
                ToggleSwitch(isRegistered, on =>
                {
                    // Re-read the real state rather than trusting the requested value:
                    // if the registry write fails, the switch must snap back.
                    if (on) GalleryProtocol.Register();
                    else GalleryProtocol.Unregister();
                    setIsRegistered(GalleryProtocol.IsRegistered);
                }, "Handling links", "Not handling links")
                    .Header("Handle reactor-gallery:// links"),

                TextBlock(isRegistered
                    ? "Registered for the current user (HKCU). An unpackaged app has no package manifest to declare the scheme, so the gallery registers itself on every launch."
                    : "Not registered — reactor-gallery:// links won't open this app until you turn this back on or restart the gallery.")
                    .Foreground(Theme.SecondaryText)
                    .FontSize(12)
                    .TextWrapping(TextWrapping.Wrap)
            );

        var children = new List<Element>
        {
            TextBlock("Deep links")
                .Foreground(Theme.PrimaryText)
                .SemiBold(),

            Border(VStack())
                .Height(1)
                .Background(Theme.DividerStroke),

            TextBlock("Any page in the gallery has a shareable link. Use the link button in the title bar to copy the link for the page you're on.")
                .Foreground(Theme.SecondaryText)
                .FontSize(13)
                .TextWrapping(TextWrapping.Wrap),

            TextBlock(exampleUri)
                .IsTextSelectionEnabled()
                .FontFamily("Cascadia Code, Consolas, monospace")
                .Foreground(Theme.PrimaryText)
                .FontSize(12),
        };

        if (registration is not null)
            children.Add(registration);

        return Border(VStack(12, children.ToArray()))
            .Padding(20)
            .Background(Theme.CardBackground)
            .WithBorder(Theme.CardStroke)
            .CornerRadius(8)
            .MaxWidth(600);
    }
}
