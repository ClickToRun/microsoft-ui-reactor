using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using static Microsoft.UI.Reactor.Factories;

namespace Microsoft.UI.Reactor.AppTests.Host.Fixtures;

internal static class MarkdownFixtures
{
    internal static Element HeadingsAndFormatting(RenderContext ctx) =>
        ScrollView(
            Microsoft.UI.Reactor.Advanced.Factories.Markdown("# Main Heading\n\nThis is a paragraph with **bold text** and *italic text*.\n\n## Sub Heading\n\nAnother paragraph here.")
                .AutomationId("MarkdownContent")
        ).AutomationId("MarkdownScroller");

    internal static Element CodeBlockAndLinks(RenderContext ctx) =>
        ScrollView(
            Microsoft.UI.Reactor.Advanced.Factories.Markdown("Here is a [link](https://example.com) and `inline code`.\n\n```csharp\nvar x = 42;\nConsole.WriteLine(x);\n```\n\nEnd of document.")
                .AutomationId("CodeBlockContent")
        ).AutomationId("CodeBlockScroller");
}
