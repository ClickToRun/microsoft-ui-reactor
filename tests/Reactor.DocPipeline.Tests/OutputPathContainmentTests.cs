using Microsoft.UI.Reactor.Cli.Docs;
using Xunit;

namespace Microsoft.UI.Reactor.Cli.Docs.Tests;

/// <summary>
/// The compiled output path is built from a topic id that comes out of
/// <c>Path.GetRelativePath</c>, which yields a rooted path across volumes and a
/// ../-prefixed one when the template resolves outside the templates root. The
/// original <c>Path.Combine</c> discarded the output directory outright for the
/// rooted case.
/// </summary>
/// <remarks>
/// <para>
/// Both directions matter and they fail differently: <c>Path.Join</c> alone
/// fixes the rooted case, and a <c>Path.IsPathRooted</c> guard alone fixes it
/// too — but neither catches traversal, which needs the containment check. The
/// theory below would pass against either half-fix if it only covered one, so
/// it covers both.
/// </para>
/// <para>
/// Scope, stated precisely because the gap was measured rather than assumed:
/// these pin <see cref="DocPaths.IsUnder"/> and the Combine-vs-Join
/// difference, and mutating <c>IsUnder</c> (dropping the trailing-separator
/// normalisation) does turn the prefix-sibling case red. They do <em>not</em>
/// cover the call site in <c>Run</c> — reverting line 518 to
/// <c>Path.Combine</c> and deleting the guard leaves the whole suite green.
/// Reaching that guard needs a topic id that is rooted or traversing, which
/// <c>Path.GetRelativePath</c> only produces for a templates root on another
/// volume or behind a link, and that is not constructible portably in a unit
/// test. The guard is therefore defence-in-depth against a future refactor,
/// not a currently reachable bug, and is deliberately shipped without
/// call-site coverage rather than with a test that would pass either way.
/// </para>
/// </remarks>
public class OutputPathContainmentTests
{
    private static readonly string Root =
        global::System.IO.Path.GetFullPath(
            global::System.IO.Path.Join(global::System.IO.Path.GetTempPath(), "guide-out"));

    [Theory]
    [InlineData("hooks")]
    [InlineData("recipes/login")]
    public void Ordinary_topic_ids_stay_inside_the_output_directory(string topicId)
    {
        var full = global::System.IO.Path.GetFullPath(
            global::System.IO.Path.Join(Root, $"{topicId}.md"));

        Assert.True(DocPaths.IsUnder(full, Root),
            $"'{topicId}' should compile inside the output dir but resolved to {full}");
    }

    [Theory]
    [InlineData("../escaped")]              // traversal: Join keeps the base, containment rejects
    [InlineData("../../etc/passwd")]
    [InlineData("recipes/../../escaped")]   // traversal that only appears mid-path
    public void Traversing_topic_ids_are_rejected(string topicId)
    {
        var full = global::System.IO.Path.GetFullPath(
            global::System.IO.Path.Join(Root, $"{topicId}.md"));

        Assert.False(DocPaths.IsUnder(full, Root),
            $"'{topicId}' escapes the output dir ({full}) and must be rejected");
    }

    /// <summary>
    /// The case that motivated the change. <c>Path.Combine</c> returns the
    /// rooted segment alone, so the output directory is silently dropped;
    /// <c>Path.Join</c> keeps it and the result stays contained. Asserting on
    /// both calls in one test is what makes this a measurement of the
    /// difference rather than a restatement of the fix.
    /// </summary>
    [Fact]
    public void Rooted_topic_id_drops_the_base_under_Combine_but_not_under_Join()
    {
        var rooted = global::System.IO.Path.IsPathRooted("/etc/passwd")
            ? "/etc/passwd"
            : global::System.IO.Path.Join(global::System.IO.Path.GetTempPath(), "elsewhere");

        var combined = global::System.IO.Path.Combine(Root, $"{rooted}.md");
        var joined = global::System.IO.Path.GetFullPath(
            global::System.IO.Path.Join(Root, $"{rooted}.md"));

        Assert.False(DocPaths.IsUnder(global::System.IO.Path.GetFullPath(combined), Root),
            "Path.Combine should drop the base for a rooted segment — if this fails the premise changed");
        Assert.True(DocPaths.IsUnder(joined, Root),
            "Path.Join must preserve the base so the rooted segment stays contained");
    }

    /// <summary>
    /// The nested case, which is the one a per-file guard cannot catch on its
    /// own: a rooted <em>directory</em> segment relocates the base that a later
    /// containment test measures against, so the file check compares an escaped
    /// path to an escaped root and reports success.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is not a restatement of the test above. There the escaped value was
    /// the thing being checked; here it is the thing being checked
    /// <em>against</em> — the guard runs, returns a correct answer, and answers
    /// the wrong question. That distinction is why
    /// <see cref="DocPaths.ResolveContained"/> exists rather than a convention
    /// of calling Join before IsUnder at each site.
    /// </para>
    /// <para>
    /// Note what the helper does <em>not</em> do here: it does not reject the
    /// rooted segment, it neutralises it. <c>Path.Join</c> keeps the base, so
    /// the result is already inside and there is nothing to reject. The first
    /// draft of this test asserted a throw and failed — worth recording,
    /// because "rooted input is refused" and "rooted input cannot escape" are
    /// different guarantees and only the second one is true. Traversal is the
    /// case that genuinely needs the throw, covered separately below.
    /// </para>
    /// <para>
    /// Non-vacuity: the first two assertions reconstruct the old two-step form
    /// and require it to <em>pass</em> on input that escapes, so they fail if
    /// the premise stops holding; the third requires the helper to contain the
    /// same input. A helper that threw unconditionally would fail this test and
    /// <see cref="Contained_segments_resolve_and_are_returned"/>; one that never
    /// threw would fail
    /// <see cref="Traversing_segments_are_rejected_by_the_helper"/>.
    /// </para>
    /// </remarks>
    [Fact]
    public void A_rooted_directory_segment_escapes_the_base_a_file_guard_measures_against()
    {
        var rootedDir = global::System.IO.Path.IsPathRooted("/evil")
            ? "/evil"
            : global::System.IO.Path.Join(global::System.IO.Path.GetTempPath(), "evil");

        // The pre-fix shape: Combine for the directory, then Join + IsUnder for
        // the file. The file guard is genuinely executed and genuinely passes.
        var escapedBase = global::System.IO.Path.GetFullPath(CombineAsTheOldCodeDid(Root, rootedDir));
        var file = global::System.IO.Path.GetFullPath(
            global::System.IO.Path.Join(escapedBase, "shot.png"));

        Assert.False(DocPaths.IsUnder(escapedBase, Root),
            "premise: a rooted directory segment must escape under Combine");
        Assert.True(DocPaths.IsUnder(file, escapedBase),
            "premise: the per-file guard passes, because it is measuring against the escaped base");

        var resolved = DocPaths.ResolveContained(Root, rootedDir, "Topic id");
        Assert.True(DocPaths.IsUnder(resolved, Root),
            $"the helper must keep a rooted segment inside the root, but resolved to {resolved}");
    }

    /// <summary>
    /// Reproduces the exact call the fix removed, so the tests above measure
    /// <c>Path.Combine</c>'s real behaviour rather than a hand-rolled model of
    /// it. Named for its purpose because the call is the subject of the test,
    /// not an oversight in it.
    /// </summary>
    /// <remarks>
    /// Static analysis flags this call for silently dropping <c>Root</c> when
    /// <paramref name="segment"/> is rooted. That is correct, and it is the
    /// property under test: <c>A_rooted_directory_segment_escapes_the_base…</c>
    /// asserts <c>IsUnder(escapedBase, Root)</c> is <em>false</em>, which only
    /// holds because the base is dropped. Switching this to <c>Path.Join</c> —
    /// the suggested remedy, offered as being "without changing test
    /// functionality" — makes that assertion fail; measured, not assumed.
    /// <para>
    /// Hand-rolling the drop instead would silence the analyser and cost the
    /// test its point: it would then pin this file's model of
    /// <c>Path.Combine</c> rather than <c>Path.Combine</c>, and a future
    /// framework change to those semantics would go unnoticed by the one test
    /// written to notice it.
    /// </para>
    /// </remarks>
    [global::System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security", "CA3006:Path.Combine may silently drop earlier arguments",
        Justification = "The dropped base is the behaviour under test; see remarks.")]
    private static string CombineAsTheOldCodeDid(string root, string segment)
        => global::System.IO.Path.Combine(root, segment);

    [Theory]
    [InlineData("hooks")]
    [InlineData("recipes/login")]
    public void Contained_segments_resolve_and_are_returned(string segment)
    {
        var resolved = DocPaths.ResolveContained(Root, segment, "Topic id");

        Assert.True(DocPaths.IsUnder(resolved, Root));
        Assert.Equal(
            global::System.IO.Path.GetFullPath(global::System.IO.Path.Join(Root, segment)),
            resolved);
    }

    /// <summary>
    /// Join alone does not make the helper safe — a traversal segment keeps the
    /// base and still walks out of it — so the containment half must remain.
    /// </summary>
    [Theory]
    [InlineData("../escaped")]
    [InlineData("recipes/../../escaped")]
    public void Traversing_segments_are_rejected_by_the_helper(string segment)
    {
        Assert.Throws<global::System.InvalidOperationException>(
            () => DocPaths.ResolveContained(Root, segment, "Topic id"));
    }

    /// <summary>
    /// A sibling directory sharing a prefix is not "inside". Without the
    /// trailing separator in <see cref="DocPaths.IsUnder"/> this passes
    /// containment and writes outside the tree.
    /// </summary>
    [Fact]
    public void Prefix_sibling_directory_is_not_treated_as_inside()
    {
        Assert.False(DocPaths.IsUnder(Root + "-old" + global::System.IO.Path.DirectorySeparatorChar + "x.md", Root));
    }

    /// <summary>
    /// A root that already ends in a separator must not gain a second one.
    /// This is the case that made consolidating the third copy in
    /// <c>ScreenshotCapture</c> more than cosmetic: that copy appended
    /// <see cref="global::System.IO.Path.DirectorySeparatorChar"/>
    /// unconditionally, so a root like <c>C:\</c> became <c>C:\\</c> and
    /// <em>every</em> path under it was rejected as an escape.
    /// <see cref="DocPaths.IsUnder"/> guards the append with an
    /// <c>EndsWith</c> check, so it does not have that behaviour.
    /// </summary>
    [Fact]
    public void Root_that_already_ends_in_a_separator_is_handled()
    {
        var driveRoot = global::System.IO.Path.GetPathRoot(
            global::System.IO.Path.GetFullPath(Root))!;

        Assert.EndsWith(
            global::System.IO.Path.DirectorySeparatorChar.ToString(), driveRoot);

        var child = driveRoot + "some-file.md";

        Assert.True(DocPaths.IsUnder(child, driveRoot),
            "a file at the drive root must count as inside the drive root");

        // The formulation that was inlined in ScreenshotCapture, shown failing
        // on the same input — so this test documents a real difference rather
        // than asserting that two spellings of the same thing agree.
        Assert.False(
            child.StartsWith(
                driveRoot + global::System.IO.Path.DirectorySeparatorChar,
                global::System.StringComparison.OrdinalIgnoreCase),
            "unconditional separator append double-separates an already-rooted path");
    }
}
