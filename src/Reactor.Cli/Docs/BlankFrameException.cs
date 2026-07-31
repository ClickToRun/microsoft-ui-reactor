namespace Microsoft.UI.Reactor.Cli.Docs;

/// <summary>
/// Thrown when a captured frame contains no content — every pixel sits at or
/// above <see cref="ImageProcessor.ContentThreshold"/>.
/// </summary>
/// <remarks>
/// <para>
/// This is the shape a doc-app capture takes when the window never painted:
/// no interactive desktop, the capture server polled before first paint, or a
/// component switch that failed silently. The frame is a solid-white surface,
/// which survives content cropping (there is nothing to crop <em>to</em>),
/// picks up the border and drop shadow like any other screenshot, and encodes
/// to a few kilobytes.
/// </para>
/// <para>
/// Historically that stub was written straight over the committed asset, so a
/// full compile in a headless session replaced the entire screenshot corpus
/// with white rectangles and still exited 0. Callers must treat this as a
/// failed capture and leave the existing file alone.
/// </para>
/// </remarks>
internal sealed class BlankFrameException : Exception
{
    /// <summary>Diagnostic code surfaced to the console and to CI logs.</summary>
    public const string DiagnosticCode = "REACTOR_DOC_SHOT_001";

    public BlankFrameException(string message) : base(message) { }

    public static BlankFrameException ForFrame(int width, int height) =>
        new($"{DiagnosticCode}: captured frame is blank ({width}×{height}, no pixel below " +
            $"{ImageProcessor.ContentThreshold}). The doc app window most likely never " +
            "painted — screenshot capture needs an interactive desktop.");
}
