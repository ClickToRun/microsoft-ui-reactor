using Microsoft.UI;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Text;
using Minesweeper.Game;
using static Microsoft.UI.Reactor.Factories;

namespace Minesweeper.Components;

/// <summary>
/// Visual style for a cell. Drives bevel direction, background, glyph color.
/// </summary>
public enum CellSkin
{
    Covered,        // raised bevel, awaiting a click
    CoveredPressed, // covered cell while the user is left-pressing it (looks indented)
    CoveredPreview, // raised → flat: cell is in a chord preview box
    Revealed,       // sunken bevel, shows count or empty
    ExplodedMine,   // sunken, red background — the mine the player clicked
    RevealedMine,   // sunken, neutral — a non-clicked mine shown on loss
    WrongFlag,      // sunken, ✗ overlay — flagged but no mine on loss
}

/// <summary>
/// Props for a single cell. Records carry value equality so the reconciler
/// can skip cells whose state is unchanged between renders.
/// </summary>
public sealed record CellProps(
    int Row,
    int Column,
    Cell Cell,
    bool IsExploded,
    bool IsLost,
    bool IsWon,
    bool IsChordPreview,
    double Size,
    bool IsDarkTheme,
    Action<int, int> OnReveal,
    Action<int, int> OnFlag,
    Action<int, int> OnChord,
    Action<int, int> OnBeginChordPreview,
    Action<bool> OnEndChordPreview);

/// <summary>
/// One cell of the Minesweeper grid. Renders a raised "covered" bevel until
/// revealed, then switches to a sunken bevel showing the adjacent count, a
/// mine, or a flag/question mark. Theme-aware colors.
/// Pointer handling: left-tap reveals, right-tap on a covered cell flags,
/// long-press flags (touch). Right-press-and-hold on a revealed numbered
/// cell shows a "chord preview" highlighting the 8 neighbors; releasing
/// the right button commits the chord.
/// </summary>
public sealed class CellComponent : Component<CellProps>
{
    public override Element Render()
    {
        var p = Props;
        // Per-cell press state — drives the "indented" look while a covered
        // cell is being pressed with the left button. Cleared on release,
        // pointer exit, or capture loss.
        var (isPressed, setPressed) = UseState(false);
        var skin = ComputeSkin(p, isPressed);
        var (bg, glyph, glyphColor) = StyleFor(skin, p.Cell, p.IsDarkTheme);

        // Glyph: shrink for emoji-heavy skins so the bomb / flag don't get
        // clipped at the bottom (emoji baselines sit lower than digits).
        var glyphFontFactor = IsEmojiGlyph(glyph) ? 0.42 : 0.55;
        var glyphContent = string.IsNullOrEmpty(glyph)
            ? (Element)Border(TextBlock(""))                                   // invisible filler
            : ApplyForeground(
                TextBlock(glyph)
                    .FontSize(p.Size * glyphFontFactor)
                    .FontWeight(FontWeights.Bold)
                    .HAlign(HorizontalAlignment.Center)
                    .VAlign(VerticalAlignment.Center)
                    .TextAlignment(TextAlignment.Center)
                    .LineHeight(p.Size)                // pin baseline so emoji descenders don't clip
                    .Set(tb =>
                    {
                        tb.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
                        // TextBlock is not a Control/Border/StackPanel, so .Padding() would
                        // silently no-op here (see ApplyModifiers in Reconciler.cs).
                        tb.Padding = new Thickness(0);
                    }),
                glyphColor);

        var bevel = BuildBevel(skin, p.Size, glyphContent, bg, p.IsDarkTheme)
            .Width(p.Size).Height(p.Size);

        // Wire the input — right-press on a revealed numbered cell shows a
        // chord preview (highlights the 8 neighbors); commit happens on
        // right-release. Right-press on a covered cell flags as usual on
        // RightTapped. Left tap reveals (or chords if double-clicked on
        // a number — see OnTapped). PointerExited cancels an in-flight
        // preview so dragging off the cell aborts the chord.
        var chordableHere = p.Cell.IsRevealed && p.Cell.AdjacentMines > 0;
        var coveredHere = !p.Cell.IsRevealed && p.Cell.Mark != CellMark.Flag;
        bevel = bevel
            .OnPointerPressed((sender, e) =>
            {
                if (p.IsLost || p.IsWon) return;
                var fe = sender as FrameworkElement;
                var pp = fe is null ? null : e.GetCurrentPoint(fe);
                if (pp is null) return;
                if (pp.Properties.IsRightButtonPressed && chordableHere)
                {
                    p.OnBeginChordPreview(p.Row, p.Column);
                    e.Handled = true;
                }
                else if (pp.Properties.IsLeftButtonPressed && coveredHere)
                {
                    setPressed(true);
                }
            })
            .OnPointerReleased((sender, e) =>
            {
                if (isPressed) setPressed(false);
                if (p.IsLost || p.IsWon) return;
                if (p.IsChordPreview)
                {
                    p.OnEndChordPreview(true);
                    e.Handled = true;
                }
            })
            .OnPointerExited((_, _) =>
            {
                if (isPressed) setPressed(false);
                if (p.IsChordPreview) p.OnEndChordPreview(false);
            })
            .OnPointerCaptureLost((_, _) =>
            {
                if (isPressed) setPressed(false);
            })
            .OnPointerCanceled((_, _) =>
            {
                if (isPressed) setPressed(false);
            })
            .OnTapped((_, e) =>
            {
                if (p.IsLost || p.IsWon) return;
                // Shift/Ctrl + left-tap mirrors right-click semantics:
                //   • on a covered cell  → cycle flag → question → clear
                //   • on a revealed number → chord (reveal neighbors, if
                //     flagged-neighbor count matches the cell's number)
                // Useful on trackpads / laptops where right-click is awkward.
                var shift = (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift)
                             & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
                var ctrl = (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control)
                            & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
                if (shift || ctrl)
                {
                    if (!p.Cell.IsRevealed) p.OnFlag(p.Row, p.Column);
                    else if (chordableHere) p.OnChord(p.Row, p.Column);
                    e.Handled = true;
                    return;
                }
                if (chordableHere)
                    p.OnChord(p.Row, p.Column);                // double-click chord shortcut
                else
                    p.OnReveal(p.Row, p.Column);
                e.Handled = true;
            })
            .OnRightTapped((_, e) =>
            {
                if (p.IsLost || p.IsWon) return;
                // RightTapped fires after PointerReleased. If we just resolved
                // a chord on a revealed numbered cell, swallow it; otherwise
                // toggle a flag on a covered cell.
                if (chordableHere) { e.Handled = true; return; }
                if (!p.Cell.IsRevealed) p.OnFlag(p.Row, p.Column);
                e.Handled = true;
            })
            .AutomationName(BuildAutomationName(p));

        // Touch / pen ergonomics: long-press also flags, on covered cells only.
        if (skin == CellSkin.Covered || skin == CellSkin.CoveredPressed)
            bevel = bevel.OnLongPress(_ => p.OnFlag(p.Row, p.Column), enableMouseEmulation: false);

        return bevel;
    }

    static bool IsEmojiGlyph(string s) => s.Length > 0 && char.IsSurrogate(s, 0);

    static CellSkin ComputeSkin(CellProps p, bool isPressed)
    {
        var c = p.Cell;
        if (p.IsLost)
        {
            if (c.IsMine && p.IsExploded) return CellSkin.ExplodedMine;
            if (c.IsMine && c.IsRevealed && c.Mark != CellMark.Flag) return CellSkin.RevealedMine;
            if (!c.IsMine && c.Mark == CellMark.Flag) return CellSkin.WrongFlag;
        }
        if (c.IsRevealed) return CellSkin.Revealed;
        // Covered cells inside a chord-preview box flatten so the player can
        // see exactly which neighbors will be revealed if they release the chord.
        if (p.IsChordPreview && c.Mark != CellMark.Flag) return CellSkin.CoveredPreview;
        // Live press feedback — covered cells without a flag look "indented"
        // while the user is left-pressing them.
        if (isPressed && c.Mark != CellMark.Flag) return CellSkin.CoveredPressed;
        return CellSkin.Covered;
    }

    /// <summary>
    /// Background color, glyph string, and glyph color (one each per skin).
    /// Numbers use the classic palette; the dark-mode variants are tuned for
    /// WCAG-AA contrast against the revealed-cell background.
    /// </summary>
    static (object Background, string Glyph, object GlyphColor) StyleFor(CellSkin skin, Cell c, bool isDark)
    {
        string num(int n) => isDark
            ? n switch
            {
                1 => "#64B5F6", 2 => "#81C784", 3 => "#E57373",
                4 => "#7986CB", 5 => "#BA68C8", 6 => "#4DD0E1",
                7 => "#EEEEEE", _ => "#BDBDBD",
            }
            : n switch
            {
                1 => "#1976D2", 2 => "#2E7D32", 3 => "#D32F2F",
                4 => "#0D47A1", 5 => "#7B1FA2", 6 => "#00838F",
                7 => "#212121", _ => "#616161",
            };

        switch (skin)
        {
            case CellSkin.ExplodedMine:
                return ("#D32F2F", "💣", "#FFFFFF");
            case CellSkin.RevealedMine:
                return ((object)Theme.SubtleFill, "💣", (object)Theme.PrimaryText);
            case CellSkin.WrongFlag:
                return ((object)Theme.SubtleFill, "✗", "#D32F2F");
            case CellSkin.Revealed:
                if (c.AdjacentMines > 0)
                    return ((object)Theme.LayerFill, c.AdjacentMines.ToString(), (object)num(c.AdjacentMines));
                return ((object)Theme.LayerFill, "", (object)Theme.PrimaryText);
            case CellSkin.CoveredPreview:
                // Same glyph as Covered but flat background — matches what the
                // cell would look like immediately after release-revealing.
                var pglyph = c.Mark switch
                {
                    CellMark.Flag => "🚩",
                    CellMark.Question => "?",
                    _ => "",
                };
                var pcol = c.Mark == CellMark.Question ? (object)Theme.AccentText : (object)Theme.PrimaryText;
                return ((object)Theme.LayerFill, pglyph, pcol);
            case CellSkin.CoveredPressed:
                // Pressed = same fill as a revealed cell, so it visually
                // "sinks". Glyph (flag/question) still shows through.
                var qglyph = c.Mark switch
                {
                    CellMark.Flag => "🚩",
                    CellMark.Question => "?",
                    _ => "",
                };
                var qcol = c.Mark == CellMark.Question ? (object)Theme.AccentText : (object)Theme.PrimaryText;
                return ((object)Theme.LayerFill, qglyph, qcol);
            default: // Covered
                var glyph = c.Mark switch
                {
                    CellMark.Flag => "🚩",
                    CellMark.Question => "?",
                    _ => "",
                };
                var col = c.Mark == CellMark.Question ? (object)Theme.AccentText : (object)Theme.PrimaryText;
                // Use a saturated medium gray (classic Win 3.x #C0C0C0 in light
                // mode, a darker gray in dark mode) so the white highlight and
                // dark shadow on the bevel both contrast strongly with the fill.
                var coveredFill = isDark ? "#3A3A3A" : "#C0C0C0";
                return (coveredFill, glyph, col);
        }
    }

    /// <summary>
    /// Bevel renderer. Three modes:
    ///   • <see cref="CellSkin.Covered"/> — classic Win 3.x raised button:
    ///     bright top+left, dark bottom+right, both clearly visible against
    ///     the fill so all four sides read as "raised".
    ///   • <see cref="CellSkin.CoveredPressed"/> — indented look:
    ///     dark top+left, bright bottom+right (inverted bevel), so pressing
    ///     a covered cell shows a clear "pushed in" effect.
    ///   • everything else — flat with a thin neutral 1-px stroke.
    /// </summary>
    static Element BuildBevel(CellSkin skin, double size, Element content, object background, bool isDark)
    {
        var inner = WithBackground(Border(content), background);

        if (skin != CellSkin.Covered && skin != CellSkin.CoveredPressed)
            return inner.WithBorder(Theme.CardStroke, 1);

        // Strong, opaque bevel colors so both edges read well in either theme.
        // White-on-light-gray is the classic Win 3.x highlight; we match it
        // by using near-white in light mode and a lighter gray in dark mode
        // (full white would over-pop on a dark fill).
        var highlight = isDark
            ? new SolidColorBrush(ColorHelper.FromArgb(255, 110, 110, 110))   // dark mode: light-gray highlight
            : new SolidColorBrush(ColorHelper.FromArgb(255, 255, 255, 255));  // light mode: pure white
        var shadow = isDark
            ? new SolidColorBrush(ColorHelper.FromArgb(255, 12, 12, 12))      // dark mode: near-black
            : new SolidColorBrush(ColorHelper.FromArgb(255, 128, 128, 128));  // light mode: classic mid-gray

        // Two stacked Borders — outer paints one pair of edges, inner paints
        // the other pair. Swap which gets which based on raised vs pressed.
        // Pressed (indented): dark on top+left, bright on bottom+right.
        // Raised:             bright on top+left, dark on bottom+right.
        var (topLeftBrush, bottomRightBrush) = skin == CellSkin.CoveredPressed
            ? (shadow, highlight)
            : (highlight, shadow);

        var withBR = inner
            .BorderThickness(0, 0, 2, 2)
            .BorderBrush(bottomRightBrush);

        return Border(withBR)
            .BorderThickness(2, 2, 0, 0)
            .BorderBrush(topLeftBrush);
    }

    static BorderElement WithBackground(BorderElement el, object bg) => bg switch
    {
        ThemeRef tr => el.Background(tr),
        string hex => el.Background(hex),
        _ => el,
    };

    static TextBlockElement ApplyForeground(TextBlockElement el, object fg) => fg switch
    {
        ThemeRef tr => el.Foreground(tr),
        string hex => el.Foreground(hex),
        _ => el,
    };

    static string BuildAutomationName(CellProps p)
    {
        var c = p.Cell;
        if (c.IsRevealed)
        {
            if (c.IsMine) return $"Row {p.Row + 1}, Column {p.Column + 1}, mine";
            if (c.AdjacentMines == 0) return $"Row {p.Row + 1}, Column {p.Column + 1}, empty";
            return $"Row {p.Row + 1}, Column {p.Column + 1}, {c.AdjacentMines} adjacent mines";
        }
        return c.Mark switch
        {
            CellMark.Flag => $"Row {p.Row + 1}, Column {p.Column + 1}, flagged",
            CellMark.Question => $"Row {p.Row + 1}, Column {p.Column + 1}, question marked",
            _ => $"Row {p.Row + 1}, Column {p.Column + 1}, hidden",
        };
    }
}
