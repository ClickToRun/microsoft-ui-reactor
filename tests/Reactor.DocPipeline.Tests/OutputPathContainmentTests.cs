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
