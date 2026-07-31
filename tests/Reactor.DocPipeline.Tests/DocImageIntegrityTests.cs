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

    /// <summary>
    /// Catalog thumbnails are written by <c>ProcessThumb</c>, which draws no
    /// border and no drop shadow, so the chrome inset must not be applied to
    /// them. A thumbnail whose only content sits in the strip the inset would
    /// trim is a real, non-blank asset; scoring it with the full-size inset
    /// would condemn it and tell an author to restore a file that was never
    /// broken.
    /// </summary>
    [Fact]
    public void Gate_does_not_flag_a_thumbnail_with_edge_content()
    {
        using var tree = new TempGuideTree();
        tree.WriteImage("controls/button-thumb.png", MakeEdgeContentThumb(320, 240));

        var findings = DiagramProcessor.ValidateImageRefs(
            "controls.md.dt", "![Button](images/controls/button-thumb.png)", tree.ImagesDir);

        Assert.Empty(findings);
    }

    /// <summary>
    /// Non-vacuity pair for <see cref="Gate_does_not_flag_a_thumbnail_with_edge_content"/>.
    /// Byte-for-byte the same image under a name without the <c>-thumb</c>
    /// suffix <em>is</em> flagged, because a full-size capture with nothing but
    /// chrome-strip pixels really is a blank frame. Only the filename differs,
    /// so this proves the suffix branch is what carries the behaviour rather
    /// than the gate simply passing everything.
    /// </summary>
    [Fact]
    public void Gate_flags_the_same_image_when_it_is_not_a_thumbnail()
    {
        using var tree = new TempGuideTree();
        tree.WriteImage("controls/button.png", MakeEdgeContentThumb(320, 240));

        var findings = DiagramProcessor.ValidateImageRefs(
            "controls.md.dt", "![Button](images/controls/button.png)", tree.ImagesDir);

        Assert.Equal("REACTOR_DOC_IMAGE_002", Assert.Single(findings).Code);
    }

    /// <summary>
    /// A transparent PNG composites to white wherever it is drawn, so it is as
    /// blank as a solid-white one — and it is the exact shape a never-rendered
    /// composition surface produces.
    /// </summary>
    [Fact]
    public void Gate_flags_a_fully_transparent_image()
    {
        using var tree = new TempGuideTree();
        tree.WriteImage("hooks/usestate.png", MakeSolidPng(499, 196, Color.FromArgb(0, 0, 0, 0)));

        var findings = DiagramProcessor.ValidateImageRefs(
            "hooks.md.dt", "![UseState demo](images/hooks/usestate.png)", tree.ImagesDir);

        Assert.Equal("REACTOR_DOC_IMAGE_002", Assert.Single(findings).Code);
    }

    [Fact]
    public void Gate_ignores_vector_references()
    {        // SVG diagrams are authored, not captured, and System.Drawing cannot
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
        var thumbs = 0;

        foreach (var file in files)
        {
            using var bmp = new Bitmap(file);
            // Same region selection the gate uses, so a thumbnail whose content
            // lives in the chrome-inset strip is scored the way it is in
            // production rather than by a stricter rule only this test applies.
            var region = ImageProcessor.ContentRegionFor(file, bmp.Width, bmp.Height);
            if (region == new Rectangle(0, 0, bmp.Width, bmp.Height) &&
                bmp.Width > 20 && bmp.Height > 20)
            {
                thumbs++;
            }
            var content = ImageProcessor.CountContentPixels(bmp, region);
            if (content == 0)
            {
                blank.Add(global::System.IO.Path.GetRelativePath(imagesDir, file));
                continue;
            }

            var ratio = (double)content / (region.Width * (long)region.Height);
            if (ratio < minRatio)
            {
                minRatio = ratio;
                minFile = global::System.IO.Path.GetRelativePath(imagesDir, file);
            }
        }

        _output.WriteLine(
            $"scanned {files.Length} PNGs ({thumbs} scored whole); sparsest interior = {minRatio:P4} ({minFile})");
        Assert.Empty(blank);
    }

    /// <summary>
    /// Delegates to <see cref="TestImages.CapturedStub"/>, which these tests pin
    /// the fidelity of (see its remarks).
    /// </summary>
    private static byte[] MakeCapturedStub(int w, int h, bool blank)
        => TestImages.CapturedStub(w, h, blank);

    /// <summary>
    /// A thumbnail-shaped image whose only content sits inside the strip the
    /// full-size chrome inset would trim (2&#160;px leading, 10&#160;px trailing).
    /// Real: scored whole it has content. Scored with the inset it is blank.
    /// That difference is the whole point of the <c>-thumb</c> branch.
    /// </summary>
    private static byte[] MakeEdgeContentThumb(int w, int h)
    {
        using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.White);
            using var ink = new SolidBrush(Color.FromArgb(32, 32, 32));
            g.FillRectangle(ink, w - 5, h - 5, 4, 4);
        }
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }

    private static byte[] MakeSolidPng(int w, int h, Color color)
    {
        using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.CompositingMode = global::System.Drawing.Drawing2D.CompositingMode.SourceCopy;
            g.Clear(color);
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
