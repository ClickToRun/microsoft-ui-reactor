using Microsoft.UI.Reactor.Cli.Docs;
using Xunit;

namespace Microsoft.UI.Reactor.Cli.Docs.Tests;

/// <summary>
/// Issue #989 filed <c>mur docs compile --no-screenshots</c> as the thing that
/// replaced 103 committed screenshots with blank stubs. Phase 3 (capture) is the
/// pipeline's only binary writer and the flag skips it outright, so the flag is
/// non-destructive by construction — but "by construction" is exactly the kind
/// of property that quietly stops being true. These tests pin it down.
/// </summary>
/// <remarks>
/// <see cref="CompileCommand.Run"/> resolves the repo root from the process
/// working directory and writes to <see cref="Console"/>, both of which are
/// process-global, so this class shares the repo's console-isolation collection.
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
        Assert.Equal(before, global::System.IO.File.ReadAllBytes(planted));
    }

    /// <summary>
    /// Control for the two tests above. Without it they would pass just as well
    /// against a harness that never reached the Phase 3 decision at all — the
    /// planted file would be untouched for the wrong reason and the "skipped"
    /// assertion would be testing nothing. Dropping the flag must produce the
    /// un-skipped banner from the same fixture.
    /// </summary>
    [Fact]
    public void Without_the_flag_the_capture_phase_is_entered()
    {
        using var repo = new FakeRepo();
        repo.PlantScreenshot("demo/widget.png");

        var (_, output) = repo.Compile("--no-build", "--skip-diagrams", "--skip-reference");

        Assert.Contains("═══ Phase 3: Capture ═══", output);
        Assert.DoesNotContain("Phase 3: Capture (skipped", output);
    }

    /// <summary>
    /// Minimal repo the doc compiler will accept: a <c>.git</c> marker for root
    /// discovery, a <c>Directory.Build.props</c> carrying the version token
    /// source, one doc app, one template, and a committed screenshot to guard.
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

            // A doc app with no doc-manifest.yaml: enough for the app to be
            // discovered (so Phase 3 is reached) without any screenshot being
            // requested, which would need a live WinUI desktop.
            global::System.IO.Directory.CreateDirectory(
                global::System.IO.Path.Join(_root, "docs", "_pipeline", "apps", "demo"));

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
