using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Text;
using Minesweeper.Game;
using static Microsoft.UI.Reactor.Factories;

namespace Minesweeper.Components;

/// <summary>
/// Big round face button at the top of the board. Click to reset. The face
/// emoji changes with game phase: 🙂 idle / playing, 😎 won, 😵 lost. While
/// the player is actively pressing on the board, switch to 😮 to mimic the
/// classic "holding a cell" feedback.
/// </summary>
public static class SmileyButton
{
    public static Element Render(GamePhase phase, bool isPressing, Action onReset, double size = 48)
    {
        var face = phase switch
        {
            GamePhase.Won => "😎",
            GamePhase.Lost => "😵",
            _ => isPressing ? "😮" : "🙂",
        };

        return Button(
                TextBlock(face)
                    .FontSize(size * 0.55)
                    .HAlign(HorizontalAlignment.Center)
                    .VAlign(VerticalAlignment.Center),
                onReset)
            .Width(size).Height(size)
            .CornerRadius(size / 2)
            .HorizontalContentAlignment(HorizontalAlignment.Center)
            .VerticalContentAlignment(VerticalAlignment.Center)
            .Padding(0)
            .AutomationName($"Reset game (status: {DescribePhase(phase)})");
    }

    static string DescribePhase(GamePhase p) => p switch
    {
        GamePhase.Won => "won",
        GamePhase.Lost => "lost",
        GamePhase.Playing => "in progress",
        _ => "ready",
    };
}
