using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Markdown;

namespace Microsoft.UI.Reactor.Advanced;

// Markdown DSL entry points. Relocated from the core Factories partial
// (src/Reactor/Elements/Dsl.cs) by spec 062 §7 Track B (B2) when the markdown
// subsystem moved into Reactor.Advanced. A `public static partial class Factories`
// cannot span assemblies, so these two factory METHODS move into Advanced's
// Factories mirror (the same partial Win2D uses). This is the one deliberate,
// preview-window source break (§7 / §12 Q4): a markdown app author adds
//   using static Microsoft.UI.Reactor.Advanced.Factories;
// alongside the core `using static Microsoft.UI.Reactor.Factories;`. Everything
// else keeps its namespace — MarkdownOptions stays in Microsoft.UI.Reactor.Markdown
// and both methods return the base Element, so no derived record is named across
// the boundary.
public static partial class Factories
{
    /// <summary>
    /// Render a markdown string as a Reactor element tree.
    /// </summary>
    public static Element Markdown(string markdown) =>
        MarkdownBuilder.Build(markdown, null);

    /// <summary>
    /// Render a markdown string as a Reactor element tree with custom rendering options.
    /// </summary>
    public static Element Markdown(string markdown, MarkdownOptions options) =>
        MarkdownBuilder.Build(markdown, options);
}
