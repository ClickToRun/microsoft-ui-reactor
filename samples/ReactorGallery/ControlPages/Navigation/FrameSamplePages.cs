using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace WinUIGalleryReactor.ControlPages.Navigation.Pages;

// Navigation targets for the Frame events micro-sample.
//
// IMPORTANT, and the whole point of this file: `Frame` is WinUI's navigation control
// and it resolves its target through the XAML type metadata the XAML compiler
// generates. A `Page` declared only in C# -- like the one below -- is absent from
// that metadata, so WinUI cannot resolve it. Reactor refuses such a navigation and
// reports it through `.NavigationFailed(...)`; calling Frame.Navigate anyway
// terminates the process with an access violation.
//
// That is not a gap to be worked around. Per docs/specs/011-navigation-design.md,
// `Frame` exists for interop with apps that already have XAML pages, and Reactor's
// own navigation system is `UseNavigation<TRoute>` + `NavigationHost` -- no XAML, no
// Page subclass, no parameterless-constructor requirement. See the NavigationDemo
// sample and docs/guide/navigation.md.
//
// The sample keeps this code-only page precisely so the refusal is visible in the
// event log, next to a navigation that succeeds.

/// <summary>
/// A code-only <c>Page</c> -- no <c>.xaml</c>, so the XAML metadata chain cannot
/// resolve it. Used by the sample to show a refused navigation being reported rather
/// than taking the process down.
/// </summary>
internal sealed partial class FrameSampleCodeOnlyPage : Page
{
    public FrameSampleCodeOnlyPage()
    {
        Content = new TextBlock
        {
            Text = "This page never renders -- WinUI cannot resolve it.",
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Background = new SolidColorBrush(Color.FromArgb(0x10, 0x80, 0x00, 0xFF));
    }
}
