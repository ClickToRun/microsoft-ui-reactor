namespace WctControls;

internal sealed class TokenizingPage : Component
{
    private static readonly string[] Suggestions =
    {
        "Account", "Add friend", "Attach", "Audio", "Calendar", "Camera",
        "Contact", "Favorite", "Link", "Mail", "Map", "Phone", "Pin",
        "Send", "Tags", "Zoom",
    };

    public override Element Render()
    {
        var (text, setText) = UseState("");

        return Gallery.Page(
            "TokenizingTextBox",
            "Type to filter the suggestions and pick one, or type free text + the delimiter (comma) to create a token. TokenItemAdding converts the typed text into a matching suggestion (or keeps it as a new token), and the box is capped at 5 — the WCT gallery pattern.",
            VStack(12,
                TokenizingTextBox(
                    text: text,
                    onTextChanged: setText,
                    onTokenItemAdding: ConvertToken,
                    suggestedItemsSource: Suggestions,
                    header: "Add up to 5 actions",
                    placeholderText: "Add actions",
                    tokenDelimiter: ",",
                    maximumTokens: 5),
                Caption(string.IsNullOrEmpty(text) ? "Pick from suggestions or type a new tag." : $"Editing: {text}")));
    }

    private static void ConvertToken(CommunityToolkit.WinUI.Controls.TokenItemAddingEventArgs e)
        => e.Item = System.Array.Find(
               Suggestions,
               s => s.Contains(e.TokenText, System.StringComparison.CurrentCultureIgnoreCase))
           ?? e.TokenText;
}
