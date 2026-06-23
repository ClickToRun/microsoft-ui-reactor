namespace Microsoft.UI.Reactor.Core;

/// <summary>
/// Narrow view of the accessibility scanner's per-walk context, handed to a
/// registered <see cref="IScanExtension"/> (issue #498). Exposes just the
/// helpers a subsystem checker needs without leaking the scanner's private
/// internals or coupling the core to the subsystem.
/// </summary>
internal interface IScanContext
{
    /// <summary>Type name of the nearest enclosing component, if any.</summary>
    string? CurrentComponent { get; }

    /// <summary>AutomationId declared on the element, or null.</summary>
    string? GetAutomationId(Element el);

    /// <summary>True when the element declares a non-empty AutomationName.</summary>
    bool HasAutomationName(Element el);

    /// <summary>Builds the rich diagnostic context (parent/heading/sibling clues) for an element.</summary>
    A11yContext BuildContext(Element el);
}

/// <summary>
/// Per-element accessibility check contributed by a control family (today:
/// Charting). The core <see cref="AccessibilityScanner"/> invokes the registered
/// extension for every element during its tree walk. Registering through this
/// seam — instead of the core naming the subsystem's checker — keeps the
/// subsystem's accessibility types out of chart-free AOT builds (issue #498).
/// </summary>
internal interface IScanExtension
{
    /// <summary>
    /// Runs the subsystem's checks against <paramref name="el"/>, appending any
    /// findings to <paramref name="findings"/>.
    /// </summary>
    void Check(Element el, IScanContext ctx, List<A11yDiagnostic> findings);
}
