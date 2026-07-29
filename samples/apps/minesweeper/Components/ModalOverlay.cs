using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Text;
using static Microsoft.UI.Reactor.Factories;

namespace Minesweeper.Components;

/// <summary>
/// Lightweight modal-dialog primitive. We don't use WinUI's ContentDialog
/// because it requires a XamlRoot that isn't reliably available when the
/// reconciler mounts the dialog content (the framework picks up the
/// content's XamlRoot, but freshly-mounted content isn't in the visual
/// tree yet — so ShowAsync silently fails).
///
/// This overlay is just a Border that fills the available space, dims the
/// backdrop, and centers a card with the dialog body. It's stacked on top
/// of the main content via a Grid so it sits above the board without
/// disrupting layout.
/// </summary>
public static class ModalOverlay
{
    public static Element Render(bool isOpen, string title, Element body, params (string Label, Action OnClick, bool IsPrimary)[] buttons)
    {
        if (!isOpen)
            // Collapsed placeholder so the Grid still reserves a child slot
            // (cheaper than re-keying every modal each time IsOpen flips).
            return Border(TextBlock("")).Width(0).Height(0);

        var buttonRow = HStack(8,
            buttons.Select(b =>
                b.IsPrimary
                    // NOTE: .AccentButton() (like .ApplyStyle) applies at mount only,
                    // so a button whose IsPrimary flips on an in-place update keeps its
                    // original style. Safe here because IsPrimary is fixed per call site
                    // and the overlay remounts its subtree when isOpen flips.
                    ? Button(b.Label, b.OnClick).MinWidth(96).AccentButton()
                    : Button(b.Label, b.OnClick).MinWidth(96)
            ).Cast<Element>().ToArray()
        ).HAlign(HorizontalAlignment.Right);

        var card = Border(
            VStack(16,
                TextBlock(title).FontSize(20).FontWeight(FontWeights.SemiBold)
                    .Foreground(Theme.PrimaryText),
                body,
                buttonRow
            ).Padding(24)
        )
        .Background(Theme.SolidBackground)
        .CornerRadius(8)
        .WithBorder(Theme.CardStroke, 1)
        .HAlign(HorizontalAlignment.Center)
        .VAlign(VerticalAlignment.Center)
        .MinWidth(380);

        // Backdrop fills the parent Grid cell, intercepts pointer events so
        // the underlying board can't be clicked, and centers the card.
        return Border(card)
            .Background("#80000000")
            .HAlign(HorizontalAlignment.Stretch)
            .VAlign(VerticalAlignment.Stretch)
            .OnTapped((_, e) => e.Handled = true)
            .OnRightTapped((_, e) => e.Handled = true);
    }
}
