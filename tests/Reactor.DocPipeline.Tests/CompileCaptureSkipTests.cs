using Microsoft.UI.Reactor.Cli.Docs;
using Xunit;

namespace Microsoft.UI.Reactor.Cli.Docs.Tests;

/// <summary>
/// Issue #989 filed <c>mur docs compile --no-screenshots</c> as the thing that
/// replaced 103 committed screenshots with blank stubs. Phase 3 (capture) is the
/// pipeline's only binary writer and the flag skips it outright, so the flag is
/// non-destructive by construction — but "by construction" is exactly the kind
/// of property that quietly stops being true.
/// </summary>
/// <remarks>
/// <para>
/// What these tests actually prove, stated precisely so nobody reads more into
/// them than is there: the fixture reaches the Phase 3 <em>decision</em>, and on
/// the skip path no capture is attempted at all; on the non-skip path a capture
/// genuinely <em>is</em> attempted (the app is discovered, its manifest parsed,
/// and <c>CaptureAsync</c> entered), it fails for want of a project to launch,
/// the failure is counted and reported, the compile exits non-zero, and the
/// planted image is still byte-identical.
/// </para>
/// <para>
/// What they cannot prove: that a <em>successful</em> capture writes only where
/// it should. That needs a live WinUI desktop and a running doc app, so it lives
/// in the CI non-destructiveness gate (<c>docs-build</c> job in
/// <c>.github/workflows/ci.yml</c>), which runs the real binary against the real
/// corpus and diffs <c>docs/guide/images</c>.
/// </para>
/// <para>
/// <see cref="CompileCommand.Run"/> resolves the repo root from the process
/// working directory and writes to <see cref="Console"/>, both of which are
/// process-global, so this class shares the repo's console-isolation collection.
/// </para>
/// </remarks>
[Collection("ConsoleTests")]
public class CompileCaptureSkipTests
{
    [Fact]
    public void No_screenshots_leaves_committed_images_byte_identical()
    {
        using var repo = new FakeRepo();
        var planted = repo.PlantScreenshot("demo/widget.png");
        var before = global::System.IO.File.ReadAllBytes(planted);
        var beforeStamp = global::System.IO.File.GetLastWriteTimeUtc(planted);

        var (exitCode, output) = repo.Compile("--no-screenshots", "--no-build", "--skip-diagrams", "--skip-reference");

        Assert.Equal(0, exitCode);
        Assert.Contains("Phase 3: Capture (skipped", output);
        // The capture loop must not have run at all — no attempt, not merely a
        // failed one. This is the difference the flag is supposed to make, and
        // the paired test below shows the same fixture does reach it otherwise.
        Assert.DoesNotContain("Capturing for demo", output);
        Assert.Equal(before, global::System.IO.File.ReadAllBytes(planted));
        Assert.Equal(beforeStamp, global::System.IO.File.GetLastWriteTimeUtc(planted));
    }

    [Fact]
    public void Skip_screenshots_alias_behaves_the_same()
    {
        using var repo = new FakeRepo();
        var planted = repo.PlantScreenshot("demo/widget.png");
        var before = global::System.IO.File.ReadAllBytes(planted);

        var (exitCode, output) = repo.Compile("--skip-screenshots", "--no-build", "--skip-diagrams", "--skip-reference");

        Assert.Equal(0, exitCode);
        Assert.Contains("Phase 3: Capture (skipped", output);
        Assert.DoesNotContain("Capturing for demo", output);
        Assert.Equal(before, global::System.IO.File.ReadAllBytes(planted));
    }

    /// <summary>
    /// Control for the two tests above. Without it they would pass just as well
    /// against a harness that never reached the Phase 3 decision — the planted
    /// file would be untouched for the wrong reason and the "skipped" assertion
    /// would be testing nothing.
    /// </summary>
    /// <remarks>
    /// The fixture deliberately ships a <c>.cs</c> file (required by
    /// <c>CompileCommand.DiscoverApps</c>) and a manifest with one screenshot,
    /// so dropping the flag drives a real <c>CaptureAsync</c> call rather than
    /// an empty loop. An earlier version of this fixture had neither, the app
    /// was never discovered, and every assertion here was vacuous.
    /// </remarks>
    [Fact]
    public void Without_the_flag_a_capture_is_attempted_and_its_failure_is_reported()
    {
        using var repo = new FakeRepo();
        var planted = repo.PlantScreenshot("demo/widget.png");
        var before = global::System.IO.File.ReadAllBytes(planted);

        var (exitCode, output) = repo.Compile("--no-build", "--skip-diagrams", "--skip-reference");

        Assert.Contains("═══ Phase 3: Capture ═══", output);
        Assert.DoesNotContain("Phase 3: Capture (skipped", output);

        // Proof the loop body ran: the app was discovered and CaptureAsync was
        // entered far enough to look for a project to launch.
        Assert.Contains("Capturing for demo", output);
        Assert.Contains("No .csproj found", output);

        // Every requested screenshot is accounted for (Requested == Written + Failed).
        Assert.Contains("Captured 0/1 screenshot(s).", output);
        Assert.Contains("1 screenshot(s) failed to capture", output);

        // A capture that produced nothing must not report success — that is how
        // a half-updated corpus reaches `git add -A` unnoticed.
        Assert.Equal(1, exitCode);
        Assert.DoesNotContain("Documentation compiled successfully.", output);

        // And the failure still must not have disturbed the committed asset.
        Assert.Equal(before, global::System.IO.File.ReadAllBytes(planted));
    }

    /// <summary>
    /// The failed-capture exit code must not depend on <c>--ci</c>. A local
    /// <c>mur docs compile</c> that exits 0 after refreshing zero of N
    /// screenshots is exactly the silence issue #989 was reported through.
    /// </summary>
    [Fact]
    public void Failed_capture_fails_the_compile_without_ci()
    {
        using var repo = new FakeRepo();
        repo.PlantScreenshot("demo/widget.png");

        var (withoutCi, _) = repo.Compile("--no-build", "--skip-diagrams", "--skip-reference");
        var (withCi, _) = repo.Compile("--ci", "--no-build", "--skip-diagrams", "--skip-reference");

        Assert.Equal(1, withoutCi);
        Assert.Equal(withCi, withoutCi);
    }

    /// <summary>
    /// Minimal repo the doc compiler will accept: a <c>.git</c> marker for root
    /// discovery, a <c>Directory.Build.props</c> carrying the version token
    /// source, one doc app (with the <c>.cs</c> file discovery requires and a
    /// manifest requesting one screenshot), one template, and a committed
    /// screenshot to guard.
    /// </summary>
    private sealed class FakeRepo : global::System.IDisposable
    {
        private readonly string _root;
        private readonly string _originalCwd;

        public FakeRepo()
        {
            _originalCwd = global::System.IO.Directory.GetCurrentDirectory();
            _root = global::System.IO.Path.Join(
                global::System.IO.Path.GetTempPath(),
                "reactor-doc-compile-" + global::System.Guid.NewGuid().ToString("N"));

            global::System.IO.Directory.CreateDirectory(global::System.IO.Path.Join(_root, ".git"));
            global::System.IO.File.WriteAllText(
                global::System.IO.Path.Join(_root, "Directory.Build.props"),
                "<Project>\n  <PropertyGroup>\n    <ReactorPublicVersion>0.1.0-test</ReactorPublicVersion>\n  </PropertyGroup>\n</Project>\n");

            var appDir = global::System.IO.Path.Join(_root, "docs", "_pipeline", "apps", "demo");
            global::System.IO.Directory.CreateDirectory(appDir);

            // DiscoverApps requires at least one .cs file; without it the app is
            // skipped and Phase 3's loop never executes.
            global::System.IO.File.WriteAllText(
                global::System.IO.Path.Join(appDir, "App.cs"),
                "// Fixture marker for CompileCommand.DiscoverApps.\n");

            // One requested screenshot, and deliberately no .csproj: capture is
            // genuinely attempted and fails at the launch step, which is the
            // furthest a headless test can drive it.
            global::System.IO.File.WriteAllText(
                global::System.IO.Path.Join(appDir, "doc-manifest.yaml"),
                """
                app:
                  title: "Demo"
                  width: 400
                  height: 300
                  startup-delay: 0

                screenshots:
                  - id: widget
                    description: "Fixture screenshot."
                    component: WidgetDemo
                    region: client
                    format: png

                """);

            var templatesDir = global::System.IO.Path.Join(_root, "docs", "_pipeline", "templates");
            global::System.IO.Directory.CreateDirectory(templatesDir);
            global::System.IO.File.WriteAllText(
                global::System.IO.Path.Join(templatesDir, "demo.md.dt"),
                """
                ---
                title: "Demo"
                app: demo
                order: 1
                audience: beginner
                goal: |
                  Fixture template for the capture-skip tests.
                tier: stub
                ---

                # Demo

                Placeholder body.

                """);

            global::System.IO.Directory.CreateDirectory(
                global::System.IO.Path.Join(_root, "docs", "guide", "images"));
        }

        /// <summary>Writes a screenshot with known bytes and returns its path.</summary>
        public string PlantScreenshot(string relative)
        {
            var full = global::System.IO.Path.Join(_root, "docs", "guide", "images",
                relative.Replace('/', global::System.IO.Path.DirectorySeparatorChar));
            global::System.IO.Directory.CreateDirectory(global::System.IO.Path.GetDirectoryName(full)!);

            // Real PNG bytes rather than a sentinel string: a future guard that
            // decodes committed images must not skip this file as undecodable.
            using var bmp = new global::System.Drawing.Bitmap(40, 30,
                global::System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = global::System.Drawing.Graphics.FromImage(bmp))
            {
                g.Clear(global::System.Drawing.Color.White);
                using var ink = new global::System.Drawing.SolidBrush(global::System.Drawing.Color.Black);
                g.FillRectangle(ink, 8, 8, 12, 12);
            }
            bmp.Save(full, global::System.Drawing.Imaging.ImageFormat.Png);

            // Backdate so a rewrite with identical bytes would still be caught
            // by the timestamp assertion.
            global::System.IO.File.SetLastWriteTimeUtc(full,
                global::System.DateTime.UtcNow.AddHours(-1));
            return full;
        }

        public (int ExitCode, string Output) Compile(params string[] args)
        {
            var stdout = Console.Out;
            var stderr = Console.Error;
            var buffer = new StringWriter();
            try
            {
                global::System.IO.Directory.SetCurrentDirectory(_root);
                Console.SetOut(buffer);
                Console.SetError(buffer);
                var exit = CompileCommand.Run(args);
                return (exit, buffer.ToString());
            }
            finally
            {
                Console.SetOut(stdout);
                Console.SetError(stderr);
                global::System.IO.Directory.SetCurrentDirectory(_originalCwd);
            }
        }

        public void Dispose()
        {
            try { global::System.IO.Directory.Delete(_root, recursive: true); }
            catch (global::System.IO.IOException) { /* best effort */ }
        }
    }
}
