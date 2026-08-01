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

    /// <summary>
    /// Appends <paramref name="segment"/> to <paramref name="root"/> and returns
    /// the absolute result, throwing when it lands outside
    /// <paramref name="root"/>. <paramref name="describe"/> names the offending
    /// input in the exception.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>Path.Join</c>, never <c>Path.Combine</c>. Combine silently discards
    /// everything before a rooted segment, so a content-derived value like
    /// <c>C:/x</c> relocates the result <em>before</em> any containment test can
    /// see it — and the test then compares an already-escaped path against an
    /// already-escaped root and passes. That is a guard which runs, returns a
    /// correct answer, and answers the wrong question. Join concatenates
    /// unconditionally, which leaves this check as the sole decider.
    /// </para>
    /// <para>
    /// Both steps are needed, and neither subsumes the other: Join alone still
    /// admits a <c>..</c> segment that walks back out, and a containment test
    /// alone is defeated by rooting. Callers previously spelled the pair inline,
    /// which meant each site's safety depended on remembering both halves.
    /// </para>
    /// </remarks>
    internal static string ResolveContained(string root, string segment, string describe)
    {
        var rootFull = Path.GetFullPath(root);
        var full = Path.GetFullPath(Path.Join(rootFull, segment));
        if (!IsUnder(full, rootFull))
            throw new InvalidOperationException($"{describe} would escape '{rootFull}'");
        return full;
    }
}
