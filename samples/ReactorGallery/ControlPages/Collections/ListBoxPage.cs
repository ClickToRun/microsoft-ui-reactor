using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Layout;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using static Microsoft.UI.Reactor.Factories;
using static WinUIGalleryReactor.SamplePageHost;

namespace WinUIGalleryReactor;

class ListBoxPage : Component
{
    public override Element Render()
    {
        var (selected, setSelected) = UseState(0);
        var fruits = new[] { "Apple", "Banana", "Cherry", "Date", "Elderberry" };

        var colors = new[] { "Red", "Green", "Blue", "Yellow" };
        var (selectedColor, setSelectedColor) = UseState(-1);

        return ScrollView(
            VStack(16,
                PageHeader("ListBox", "A list of selectable items presented inline."),

                SampleCard("Basic ListBox",
                    VStack(8,
                        ListBox(fruits, selected, setSelected),
                        TextBlock($"Selected: {fruits[selected]}").Foreground(Theme.SecondaryText)
                    ),
                    """
                    var (selected, setSelected) = UseState(0);
                    ListBox(fruits, selected, setSelected)
                    """),

                SampleCard("Styled ListBox",
                    VStack(8,
                        ListBox(colors, selectedColor, setSelectedColor).Width(200),
                        TextBlock(selectedColor >= 0 ? $"Selected: {colors[selectedColor]}" : "Nothing selected")
                            .Foreground(Theme.SecondaryText)
                    ),
                    """
                    // Each sample owns its own state — sharing a setter between two
                    // ListBoxes would make one card silently drive the other.
                    var (selectedColor, setSelectedColor) = UseState(-1);
                    ListBox(colors, selectedColor, setSelectedColor).Width(200)
                    """)
            ).Margin(36, 24, 36, 36)
        );
    }
}
