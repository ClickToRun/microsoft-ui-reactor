using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using static Microsoft.UI.Reactor.Factories;
using static WinUIGalleryReactor.SamplePageHost;

namespace WinUIGalleryReactor.ControlPages.BasicInput;

class RadioButtonPage: Component
{
    public override Element Render()
    {
        var options = new[] { "Option 1", "Option 2", "Option 3" };
        var (groupIndex, setGroupIndex) = UseState(0);
        var (individualChoice, setIndividualChoice) = UseState("A");

        return ScrollView(VStack(16,
            PageHeader("RadioButton", "A button that allows a user to select a single option from a group."),

            SampleCard("RadioButtons Group",
                VStack(8,
                    RadioButtons(options, groupIndex, i => setGroupIndex(i)),
                    TextBlock($"Selected: {options[groupIndex]}").Foreground(Theme.SecondaryText)),
                sourceCode: @"
RadioButtons(options, groupIndex, i => setGroupIndex(i))
"),

            SampleCard("Individual RadioButton Items",
                VStack(8,
                    RadioButton("Choice A", individualChoice == "A", v => { if (v) setIndividualChoice("A"); }, "choices"),
                    RadioButton("Choice B", individualChoice == "B", v => { if (v) setIndividualChoice("B"); }, "choices"),
                    TextBlock($"Chosen: {individualChoice}").Foreground(Theme.SecondaryText)),
                sourceCode: @"
// RadioButton's third parameter is Action<bool> — it fires for both the check
// and the uncheck, so guard on the value before committing the choice.
RadioButton(""Choice A"", individualChoice == ""A"", v => { if (v) setIndividualChoice(""A""); }, ""choices"")
RadioButton(""Choice B"", individualChoice == ""B"", v => { if (v) setIndividualChoice(""B""); }, ""choices"")
")
        ).Margin(36, 24, 36, 36));
    }
}
