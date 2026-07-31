using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using static Microsoft.UI.Reactor.Factories;
using static WinUIGalleryReactor.SamplePageHost;

namespace WinUIGalleryReactor.ControlPages.DesignGuidance;

class ThemePage : Component
{
    public override Element Render()
    {
        var (isDark, setIsDark) = UseState(false);

        Element preview = Border(
            VStack(12,
                TextBlock("Theme Preview").Foreground(Theme.PrimaryText).Bold().FontSize(16),
                TextBlock("This card adapts to the current theme.")
                    .Foreground(Theme.SecondaryText),
                HStack(8,
                    Button("Primary Action"),
                    Button("Secondary").Background(Theme.SubtleFill)
                ),
                Border(TextBlock("Nested card").Margin(8).Foreground(Theme.PrimaryText))
                    .Background(Theme.LayerFill)
                    .WithBorder(Theme.SurfaceStroke)
                    .CornerRadius(4)
                    .Margin(0)
            ).Margin(14)
        ).Background(Theme.CardBackground)
         .WithBorder(Theme.CardStroke)
         .CornerRadius(8);

        return ScrollView(
            VStack(16,
                PageHeader("Theme",
                    "Guidance on applying light, dark, and high-contrast themes."),

                SampleCard("Light / Dark Toggle",
                    Border(
                        VStack(12,
                            ToggleSwitch(isDark, b => setIsDark(b),
                                onContent: "Dark", offContent: "Light", header: "Theme"),
                            preview
                        ).Padding(16)
                    ).RequestedTheme(isDark ? ElementTheme.Dark : ElementTheme.Light)
                     .Background(Theme.SolidBackground)
                     .CornerRadius(8),
                    @"var (isDark, setIsDark) = UseState(false);
Border(VStack(12,
    ToggleSwitch(isDark, b => setIsDark(b), onContent: ""Dark"", offContent: ""Light"", header: ""Theme""),
    preview).Padding(16))
    .RequestedTheme(isDark ? ElementTheme.Dark : ElementTheme.Light)
    .Background(Theme.SolidBackground)"),

                SampleCard("Theme Tokens Reference",
                    VStack(8,
                        HStack(8,
                            Border(VStack()).Background(Theme.Accent).Width(24).Height(24).CornerRadius(4),
                            TextBlock("Theme.Accent — brand accent color").Foreground(Theme.PrimaryText).FontSize(13)),
                        HStack(8,
                            Border(VStack()).Background(Theme.CardBackground).WithBorder(Theme.CardStroke).Width(24).Height(24).CornerRadius(4),
                            TextBlock("Theme.CardBackground — card surfaces").Foreground(Theme.PrimaryText).FontSize(13)),
                        HStack(8,
                            Border(VStack()).Background(Theme.SubtleFill).WithBorder(Theme.SurfaceStroke).Width(24).Height(24).CornerRadius(4),
                            TextBlock("Theme.SubtleFill — subtle backgrounds").Foreground(Theme.PrimaryText).FontSize(13))
                    ),
                    @"Theme.Accent, Theme.CardBackground,
Theme.SubtleFill, Theme.SolidBackground")
            ).Margin(36, 24, 36, 36)
        );
    }
}
