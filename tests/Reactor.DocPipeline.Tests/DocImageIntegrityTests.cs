using System.Drawing;
using System.Drawing.Imaging;
using Microsoft.UI.Reactor.Cli.Docs;
using Xunit;

namespace Microsoft.UI.Reactor.Cli.Docs.Tests;

/// <summary>
/// Issue #989: <c>docs compile</c> once overwrote 103 committed screenshots with
/// ~3.5&#160;KB solid-white stubs produced by a capture whose doc-app window never
/// painted, and still exited 0. The only reason it was caught was a human
/// noticing the changed-file count in a PR.
/// </summary>
/// <remarks>
/// These tests cover the <c>REACTOR_DOC_IMAGE_002</c> gate that now runs in
/// Phase 6 of every compile. The gate is deliberately a <em>contentless</em>
/// predicate rather than a file-size floor: the committed corpus contains
/// legitimately tiny screenshots (<c>async-loading.png</c> is 89×40 / 2127&#160;B),
/// so any size threshold able to catch a stub would also condemn real assets.
/// </remarks>
public class DocImageIntegrityTests
{
    private readonly ITestOutputHelper _output;

    public DocImageIntegrityTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void Gate_flags_a_blank_screenshot()
    {
        using var tree = new TempGuideTree();
        tree.WriteImage("hooks/usestate.png", MakeCapturedStub(499, 196, blank: true));

        var findings = DiagramProcessor.ValidateImageRefs(
            "hooks.md.dt", "![UseState demo](images/hooks/usestate.png)", tree.ImagesDir);

        var f = Assert.Single(findings);
        Assert.Equal("REACTOR_DOC_IMAGE_002", f.Code);
        Assert.Equal(TierLintSeverity.Error, f.Severity);
    }

    /// <summary>
    /// Positive control. The stub in <see cref="Gate_flags_a_blank_screenshot"/>
    /// carries the same border and drop shadow every processed screenshot does,
    /// so a gate that scored its own chrome as content would report zero
    /// findings on both inputs and this pair is what proves it does not. Only
    /// the painted interior differs between the two images.
    /// </summary>
    [Fact]
    public void Gate_accepts_a_painted_screenshot()
    {
        using var tree = new TempGuideTree();
        tree.WriteImage("hooks/usestate.png", MakeCapturedStub(499, 196, blank: false));

        var findings = DiagramProcessor.ValidateImageRefs(
            "hooks.md.dt", "![UseState demo](images/hooks/usestate.png)", tree.ImagesDir);

        Assert.Empty(findings);
    }

    [Fact]
    public void Gate_ignores_vector_references()
    {
        // SVG diagrams are authored, not captured, and System.Drawing cannot
        // decode them — reporting them as blank would be a false alarm on
        // every compile.
        using var tree = new TempGuideTree();
        tree.WriteText("architecture/overview.svg",
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"10\" height=\"10\"></svg>");

        var findings = DiagramProcessor.ValidateImageRefs(
            "arch.md.dt", "![Overview](images/architecture/overview.svg)", tree.ImagesDir);

        Assert.Empty(findings);
    }

    [Fact]
    public void Gate_does_not_report_an_undecodable_file_as_blank()
    {
        // A truncated or corrupt PNG is a different problem with a different
        // fix. Misfiling it as "blank screenshot — restore it from git" would
        // send an author chasing the wrong thing.
        using var tree = new TempGuideTree();
        tree.WriteBytes("hooks/broken.png", [0x89, 0x50, 0x4E, 0x47, 0x00, 0x00]);

        var findings = DiagramProcessor.ValidateImageRefs(
            "hooks.md.dt", "![Broken](images/hooks/broken.png)", tree.ImagesDir);

        Assert.DoesNotContain(findings, f => f.Code == "REACTOR_DOC_IMAGE_002");
    }

    /// <summary>
    /// The real corpus must pass. This is the calibration test: it fails if the
    /// gate is ever tightened past what genuine screenshots satisfy, and the
    /// logged minimum is the documented margin behind the <c>== 0</c> threshold.
    /// </summary>
    [Fact]
    public void Committed_screenshot_corpus_has_no_blank_images()
    {
        var imagesDir = global::System.IO.Path.Join(FindRepoRoot(), "docs", "guide", "images");
        Assert.True(global::System.IO.Directory.Exists(imagesDir), $"images dir not found: {imagesDir}");

        var files = global::System.IO.Directory
            .GetFiles(imagesDir, "*.png", global::System.IO.SearchOption.AllDirectories);

        // Guard against a mis-resolved path producing a confident false
        // all-clear: an empty enumeration would otherwise "pass" below.
        Assert.True(files.Length >= 200,
            $"expected the full committed corpus, found only {files.Length} PNGs under {imagesDir}");

        var blank = new List<string>();
        var minRatio = double.MaxValue;
        var minFile = "";

        foreach (var file in files)
        {
            using var bmp = new Bitmap(file);
            var interior = ImageProcessor.InteriorRegion(bmp.Width, bmp.Height);
            var content = ImageProcessor.CountContentPixels(bmp, interior);
            if (content == 0)
            {
                blank.Add(global::System.IO.Path.GetRelativePath(imagesDir, file));
                continue;
            }

            var ratio = (double)content / (interior.Width * (long)interior.Height);
            if (ratio < minRatio)
            {
                minRatio = ratio;
                minFile = global::System.IO.Path.GetRelativePath(imagesDir, file);
            }
        }

        _output.WriteLine($"scanned {files.Length} PNGs; sparsest interior = {minRatio:P4} ({minFile})");
        Assert.Empty(blank);
    }

    /// <summary>
    /// Builds the exact artifact the pre-fix pipeline produced: an unpainted
    /// (or painted) source frame composited onto a canvas with the drop shadow
    /// and 1&#160;px gray-300 border that <c>ImageProcessor.AddBorderAndShadow</c>
    /// draws. Hand-rolled rather than routed through <c>ImageProcessor.Process</c>
    /// because <c>Process</c> now refuses blank frames outright — this test has
    /// to be able to produce the artifact that fix prevents.
    /// </summary>
    private static byte[] MakeCapturedStub(int w, int h, bool blank)
    {
        const int shadowOffset = 2;
        const int shadowBlur = 6;

        using var bmp = new Bitmap(w + shadowOffset + shadowBlur, h + shadowOffset + shadowBlur,
            PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = global::System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Color.White);

            for (int i = shadowBlur; i >= 1; i--)
            {
                var alpha = (int)(0.12f * (1f - (float)i / shadowBlur) * 255);
                if (alpha <= 0) continue;
                using var shadow = new SolidBrush(Color.FromArgb(alpha, 0, 0, 0));
                g.FillRectangle(shadow, shadowOffset + i, shadowOffset + i, w - 1, h - 1);
            }

            // The captured frame itself, drawn over the shadow.
            using (var surface = new SolidBrush(Color.White))
            {
                g.FillRectangle(surface, 0, 0, w, h);
            }

            if (!blank)
            {
                using var ink = new SolidBrush(Color.FromArgb(32, 32, 32));
                g.FillRectangle(ink, w / 2, h / 2, 8, 8);
            }

            using var border = new Pen(Color.FromArgb(209, 213, 219), 1);
            g.DrawRectangle(border, 0, 0, w - 1, h - 1);
        }

        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }

    private static string FindRepoRoot()
    {
        var dir = new global::System.IO.DirectoryInfo(global::System.AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (global::System.IO.File.Exists(global::System.IO.Path.Join(dir.FullName, "Reactor.slnx")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new global::System.InvalidOperationException(
            "Could not locate repo root (Reactor.slnx) from test base dir.");
    }

    /// <summary>
    /// Minimal <c>docs/guide/images</c> tree. <c>ValidateImageRefs</c> resolves
    /// <c>images/&lt;rel&gt;</c> against the <em>parent</em> of the images root,
    /// so the layout has to mirror the real one.
    /// </summary>
    private sealed class TempGuideTree : global::System.IDisposable
    {
        private readonly string _root;

        public TempGuideTree()
        {
            _root = global::System.IO.Path.Join(
                global::System.IO.Path.GetTempPath(),
                "reactor-doc-images-" + global::System.Guid.NewGuid().ToString("N"));
            global::System.IO.Directory.CreateDirectory(ImagesDir);
        }

        public string ImagesDir => global::System.IO.Path.Join(_root, "guide", "images");

        public void WriteImage(string relative, byte[] png) => WriteBytes(relative, png);

        public void WriteBytes(string relative, byte[] bytes)
        {
            var full = Prepare(relative);
            global::System.IO.File.WriteAllBytes(full, bytes);
        }

        public void WriteText(string relative, string text)
        {
            var full = Prepare(relative);
            global::System.IO.File.WriteAllText(full, text);
        }

        private string Prepare(string relative)
        {
            var full = global::System.IO.Path.Join(ImagesDir, relative.Replace('/', global::System.IO.Path.DirectorySeparatorChar));
            global::System.IO.Directory.CreateDirectory(global::System.IO.Path.GetDirectoryName(full)!);
            return full;
        }

        public void Dispose()
        {
            try { global::System.IO.Directory.Delete(_root, recursive: true); }
            catch (global::System.IO.IOException) { /* best effort */ }
        }
    }
}
