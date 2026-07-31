using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using static Microsoft.UI.Reactor.Factories;
using static WinUIGalleryReactor.SamplePageHost;

namespace WinUIGalleryReactor.ControlPages.DialogsAndFlyouts;

class ContentDialogPage : Component
{
    public override Element Render()
    {
        var (showBasic, setShowBasic) = UseState(false);
        var (showConfirm, setShowConfirm) = UseState(false);
        var (result, setResult) = UseState("(none)");

        return ScrollView(
            VStack(16,
                PageHeader("ContentDialog",
                    "A modal dialog box that displays content and action buttons."),

                SampleCard("Basic Dialog",
                    VStack(8,
                        Button("Show Dialog", () => setShowBasic(true)),
                        ContentDialog("Welcome", TextBlock("Thank you for using this app!"), "OK") with
                        {
                            IsOpen = showBasic,
                            OnClosed = _ => setShowBasic(false),
                        }
                    ),
                    @"Button(""Show Dialog"", () => setShowBasic(true)),
ContentDialog(""Welcome"", TextBlock(""Thank you!""), ""OK"") with {
    IsOpen = showBasic,
    OnClosed = _ => setShowBasic(false),
}"),

                SampleCard("Confirmation Dialog",
                    VStack(8,
                        Button("Delete Item", () => setShowConfirm(true)),
                        TextBlock($"Last result: {result}").Foreground(Theme.SecondaryText),
                        ContentDialog("Confirm Delete",
                            TextBlock("Are you sure you want to delete this item? This action cannot be undone."),
                            "Delete") with
                        {
                            IsOpen = showConfirm,
                            SecondaryButtonText = "Cancel",
                            OnClosed = r =>
                            {
                                setResult(r.ToString());
                                setShowConfirm(false);
                            },
                        }
                    ),
                    @"ContentDialog(""Confirm Delete"",
    TextBlock(""Are you sure?""), ""Delete"") with {
    IsOpen = showConfirm,
    SecondaryButtonText = ""Cancel"",
    OnClosed = r => { setResult(r.ToString()); setShowConfirm(false); },
}")
            ).Margin(36, 24, 36, 36)
        );
    }
}
