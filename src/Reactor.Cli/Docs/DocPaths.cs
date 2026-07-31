namespace Microsoft.UI.Reactor.Cli.Docs;

/// <summary>
/// Path predicates shared by the doc pipeline's containment checks.
/// </summary>
/// <remarks>
/// Both callers use this to decide whether a path derived from repository
/// content — a topic id from <c>GetRelativePath</c>, an image reference out of
/// a compiled page — is allowed to be written to or read from. Two copies of a
/// containment rule drift, and the copy that stops being fixed is the one that
/// decides a security-relevant question, so it lives in exactly one place.
/// </remarks>
internal static class DocPaths
{
    /// <summary>
    /// True when <paramref name="candidate"/> sits inside <paramref name="root"/>.
    /// Both must already be absolute (call <c>Path.GetFullPath</c> first) —
    /// this compares text and does no normalisation of its own.
    /// </summary>
    /// <remarks>
    /// The trailing separator is load-bearing: without it a sibling directory
    /// sharing a prefix, such as <c>docs/guide-old</c> against <c>docs/guide</c>,
    /// satisfies the check and escapes the tree.
    /// </remarks>
    internal static bool IsUnder(string candidate, string root)
    {
        var rooted = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        return candidate.StartsWith(rooted, StringComparison.OrdinalIgnoreCase);
    }
}
