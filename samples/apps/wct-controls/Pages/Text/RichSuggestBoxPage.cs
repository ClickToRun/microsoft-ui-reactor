namespace WctControls;

internal sealed class RichSuggestBoxPage : Component
{
    private static readonly string[] People =
    {
        "Adele Vance", "Alex Wilber", "Diego Siciliani", "Grady Archie",
        "Isaiah Langer", "Lee Gu", "Megan Bowen", "Patti Fernandez",
    };

    public override Element Render() =>
        Gallery.Page(
            "RichSuggestBox",
            "An ItemsControl wrapping a RichEditBox. Type the “@” prefix to mention someone — SuggestionRequested fires and the suggestion list (ItemsSource) is filtered. Tokens are managed internally, so the Items slot is excluded.",
            RichSuggestBox(
                header: "Message",
                placeholderText: "Type @ to mention someone…",
                prefixes: "@",
                itemsSource: People,
                onSuggestionRequested: OnSuggestion)
                .Width(420).Height(160));

    private static void OnSuggestion(CommunityToolkit.WinUI.Controls.SuggestionRequestedEventArgs e) { }
}
