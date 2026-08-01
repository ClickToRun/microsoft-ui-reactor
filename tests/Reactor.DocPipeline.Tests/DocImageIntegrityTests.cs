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
            "hooks.md.dt", "![UseState demo](images/hooks/usestate.png)", tree.ImagesDir, tree.GuideDir);

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
            "hooks.md.dt", "![UseState demo](images/hooks/usestate.png)", tree.ImagesDir, tree.GuideDir);

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
            "controls.md.dt", "![Button](images/controls/button-thumb.png)", tree.ImagesDir, tree.GuideDir);

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
            "controls.md.dt", "![Button](images/controls/button.png)", tree.ImagesDir, tree.GuideDir);

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
            "hooks.md.dt", "![UseState demo](images/hooks/usestate.png)", tree.ImagesDir, tree.GuideDir);

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
            "arch.md.dt", "![Overview](images/architecture/overview.svg)", tree.ImagesDir, tree.GuideDir);

        Assert.Empty(findings);
    }

    /// <summary>
    /// A raster file that exists, carries valid magic and sits inside the size
    /// caps but cannot be decoded must be reported, not silently accepted.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the gate's own fail-open. The blankness scan is wrapped in a
    /// catch that returns "not blank" on any decode fault, so before this
    /// change an undecodable image produced <em>zero</em> findings — the
    /// compile printed nothing and exited 0, which is the same silent-success
    /// shape as issue #989 itself, one layer up.
    /// </para>
    /// <para>
    /// It is reachable rather than theoretical: any corruption that survives
    /// the magic check but defeats the decoder lands here. The fixture keeps a
    /// real PNG signature and replaces the body, because a file with no
    /// signature at all is turned away by <c>HasRasterMagic</c> before the
    /// decode step and would not exercise the catch. Note the sibling test
    /// below: a *truncated* PNG does not reach this path — GDI+ decodes it —
    /// so this branch and the blank branch each own a distinct real shape.
    /// </para>
    /// </remarks>
    [Fact]
    public void Gate_reports_an_undecodable_image_instead_of_passing_it()
    {
        using var tree = new TempGuideTree();
        var valid = MakeCapturedStub(200, 150, blank: false);

        // Keep the 8-byte PNG signature so HasRasterMagic still admits the file
        // to the decode step, but replace the body so the decoder cannot read it.
        var corrupt = new byte[valid.Length];
        global::System.Array.Copy(valid, corrupt, 8);
        for (var i = 8; i < corrupt.Length; i++) corrupt[i] = (byte)(i * 31 % 251);
        tree.WriteImage("controls/half-written.png", corrupt);

        var findings = DiagramProcessor.ValidateImageRefs(
            "controls.md.dt",
            "![Half written](images/controls/half-written.png)",
            tree.ImagesDir,
            tree.GuideDir);

        // Guard the premise: if the fixture had not written the file, or the
        // magic check had turned it away, this test would be asserting
        // something other than what it claims.
        Assert.True(
            global::System.IO.File.Exists(
                global::System.IO.Path.Join(tree.ImagesDir, "controls", "half-written.png")),
            "fixture did not write the file — every assertion below would be about a missing file");

        var finding = Assert.Single(findings);
        Assert.Equal("REACTOR_DOC_IMAGE_003", finding.Code);
        Assert.Contains("decode", finding.Message, global::System.StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Non-vacuity pair: the intact original of the very same fixture is
    /// accepted. Only the body bytes differ, so the two together show the new
    /// code fires on undecodability rather than on the fixture in general.
    /// </summary>
    [Fact]
    public void Gate_accepts_the_intact_original_of_that_image()
    {
        using var tree = new TempGuideTree();
        tree.WriteImage("controls/fully-written.png", MakeCapturedStub(200, 150, blank: false));

        var findings = DiagramProcessor.ValidateImageRefs(
            "controls.md.dt",
            "![Fully written](images/controls/fully-written.png)",
            tree.ImagesDir,
            tree.GuideDir);

        Assert.Empty(findings);
    }

    /// <summary>
    /// A PNG cut off mid-write — what an interrupted capture leaves behind — is
    /// reported as <c>REACTOR_DOC_IMAGE_002</c>, not as a decode failure.
    /// </summary>
    /// <remarks>
    /// This is a measurement, not an aspiration, and it is the reason the
    /// undecodable branch above uses a corrupted body rather than a truncation:
    /// GDI+ decodes a truncated PNG rather than throwing, yielding the
    /// unwritten scanlines as blank, so the realistic interrupted-write shape
    /// lands on the blank gate and never reaches the catch. Pinning it here
    /// means that if a future decoder change starts throwing instead, this test
    /// moves rather than silently swapping which code fires.
    /// </remarks>
    [Fact]
    public void Gate_reports_a_truncated_capture_as_blank_not_as_corrupt()
    {
        using var tree = new TempGuideTree();
        var valid = MakeCapturedStub(200, 150, blank: false);
        var truncated = new byte[valid.Length / 3];
        global::System.Array.Copy(valid, truncated, truncated.Length);
        tree.WriteImage("controls/interrupted.png", truncated);

        var findings = DiagramProcessor.ValidateImageRefs(
            "controls.md.dt",
            "![Interrupted](images/controls/interrupted.png)",
            tree.ImagesDir,
            tree.GuideDir);

        Assert.Equal("REACTOR_DOC_IMAGE_002", Assert.Single(findings).Code);
    }

    /// <summary>
    /// A file too short to hold a signature is skipped as "not a raster", not
    /// crashed on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This guards the regression risk introduced by reading the header with
    /// <c>ReadExactly</c> instead of <c>Read</c>: <c>ReadExactly</c> throws
    /// <c>EndOfStreamException</c> on a file shorter than the buffer, a throw
    /// site the old code did not have. The test proves that throw is absorbed
    /// rather than escaping <c>ValidateImageRefs</c> and taking a whole compile
    /// down on one stray short file. Verified by making the exception escape
    /// both catches, which fails exactly this test and nothing else.
    /// </para>
    /// <para>
    /// It <em>now</em> also proves the dedicated <c>EndOfStreamException</c>
    /// catch is load-bearing, which it did not when first written. The original
    /// claim here was that it was, and the measurement refuted it: deleting the
    /// catch changed no test, because <c>EndOfStreamException</c> derives from
    /// <c>IOException</c> and a blanket handler covered it. That blanket handler
    /// has since been removed — it was swallowing genuine read faults and
    /// returning "not a raster", the fail-open
    /// <c>Gate_reports_a_locked_image_instead_of_skipping_it</c> pins — which is
    /// exactly the tightening the old comment anticipated. With it gone, this
    /// inner catch is the only thing keeping a 4-byte stub from being reported
    /// as undecodable, and deleting it now fails this test.
    /// </para>
    /// <para>
    /// Nor does it prove the fail-open the <c>ReadExactly</c> change actually
    /// fixes — a short read returning fewer bytes without being at EOF, which
    /// silently skipped blank-frame validation for a valid PNG.
    /// <c>HasRasterMagic</c> takes a path and opens its own
    /// <c>FileStream</c>, so there is no seam to inject a stream that
    /// under-reads, and a local <c>FileStream</c> will not do it on demand.
    /// That change is kept because the contract of <c>Stream.Read</c> permits
    /// the short read, not because anything here demonstrates one.
    /// </para>
    /// </remarks>
    [Fact]
    public void Gate_skips_a_file_too_short_to_carry_a_signature()
    {
        using var tree = new TempGuideTree();
        tree.WriteImage("controls/stub.png", [0x89, 0x50, 0x4E, 0x47]);

        var findings = DiagramProcessor.ValidateImageRefs(
            "controls.md.dt",
            "![Stub](images/controls/stub.png)",
            tree.ImagesDir,
            tree.GuideDir);

        // Premise guard: a missing file would also produce no raster finding,
        // for an entirely different reason.
        var path = global::System.IO.Path.Join(tree.ImagesDir, "controls", "stub.png");
        Assert.True(
            global::System.IO.File.Exists(path),
            "fixture did not write the file — the assertion below would be about a missing file");
        Assert.Equal(4, new global::System.IO.FileInfo(path).Length);

        Assert.DoesNotContain(
            findings,
            f => f.Code is "REACTOR_DOC_IMAGE_002" or "REACTOR_DOC_IMAGE_003");
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
            "hooks.md.dt", "![Broken](images/hooks/broken.png)", tree.ImagesDir, tree.GuideDir);

        Assert.DoesNotContain(findings, f => f.Code == "REACTOR_DOC_IMAGE_002");
    }

    /// <summary>
    /// A raster the gate cannot open — locked by another process — is reported
    /// as <c>REACTOR_DOC_IMAGE_003</c>, not skipped as "not a raster".
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the gate's own fail-open, one level below the one
    /// <c>Gate_reports_an_undecodable_image_instead_of_passing_it</c> closes.
    /// <c>ComputeRasterVerdict</c>'s catch deliberately admits
    /// <c>IOException</c> and <c>UnauthorizedAccessException</c> and reports
    /// them, on the stated reasoning that the verdict spans "corrupt" and
    /// "couldn't read right now". But the magic-bytes pre-check runs
    /// <em>first</em> and used to swallow those same two exceptions and return
    /// <c>false</c>, which the caller reads as "not a raster" and skips. So a
    /// locked file never reached the catch that was written for it: the
    /// documented behaviour and the actual control flow disagreed, and the
    /// direction of the disagreement was silent success.
    /// </para>
    /// <para>
    /// That is the exact shape this pipeline exists to stop — a gate that skips
    /// analysis is a gate that passes — and it is invisible to every other test
    /// here because they all hand the gate a readable file.
    /// </para>
    /// </remarks>
    [Fact]
    public void Gate_reports_a_locked_image_instead_of_skipping_it()
    {
        using var tree = new TempGuideTree();
        tree.WriteImage("controls/locked.png", MakeCapturedStub(200, 150, blank: false));
        var path = global::System.IO.Path.Join(tree.ImagesDir, "controls", "locked.png");

        // Hold an exclusive handle for the duration of the scan, which is what
        // another process mid-write looks like to the gate.
        using (var hold = new global::System.IO.FileStream(
                   path,
                   global::System.IO.FileMode.Open,
                   global::System.IO.FileAccess.Read,
                   global::System.IO.FileShare.None))
        {
            // Premise guard: if this platform let a second reader in, the test
            // would be scanning a perfectly readable file and asserting nothing.
            var lockHolds = false;
            try
            {
                using var probe = global::System.IO.File.OpenRead(path);
            }
            catch (global::System.IO.IOException)
            {
                lockHolds = true;
            }

            Assert.True(lockHolds, "the exclusive handle did not block a second reader — this test cannot measure what it claims");

            var findings = DiagramProcessor.ValidateImageRefs(
                "controls.md.dt",
                "![Locked](images/controls/locked.png)",
                tree.ImagesDir,
                tree.GuideDir);

            Assert.Equal("REACTOR_DOC_IMAGE_003", Assert.Single(findings).Code);
        }

        // Non-vacuity: the same file, same fixture, once the handle is gone.
        // Only the lock differs, so the finding above turns on the lock and not
        // on anything about the image.
        Assert.Empty(DiagramProcessor.ValidateImageRefs(
            "controls.md.dt",
            "![Locked](images/controls/locked.png)",
            tree.ImagesDir,
            tree.GuideDir));
    }

    /// <summary>
    /// A reference's <c>../</c> run is page-relative escaping emitted by
    /// DocAssembler for the page's depth. Resolving it the way a renderer does
    /// — against the page's own directory — is what makes a wrong-depth prefix
    /// detectable: normalising the run away instead would land every variant
    /// below on the same existing file and report nothing, while the rendered
    /// page 404s. Each row differs from the passing one only in the prefix, so
    /// the assertion turns on the traversal and nothing else.
    /// </summary>
    [Theory]
    // page depth 0 (docs/guide/hooks.md) — no escaping needed
    [InlineData("", "images/x/shot.png", true)]
    [InlineData("", "../images/x/shot.png", false)]          // one ../ too many
    [InlineData("", "../../images/x/shot.png", false)]       // two too many
    // page depth 1 (docs/guide/recipes/login.md) — exactly one ../
    [InlineData("recipes", "../images/x/shot.png", true)]
    [InlineData("recipes", "images/x/shot.png", false)]      // missing the ../
    [InlineData("recipes", "../../images/x/shot.png", false)] // one too many
    // page depth 2 (docs/guide/recipes/auth/oauth.md) — exactly two
    [InlineData("recipes/auth", "../../images/x/shot.png", true)]
    [InlineData("recipes/auth", "../images/x/shot.png", false)]
    public void Image_ref_must_carry_the_right_traversal_for_its_page_depth(
        string pageSubdir, string reference, bool shouldResolve)
    {
        using var tree = new TempGuideTree();
        tree.WriteImage("x/shot.png", MakeCapturedStub(499, 196, blank: false));

        var pageDir = pageSubdir.Length == 0
            ? tree.GuideDir
            : global::System.IO.Path.Join(
                tree.GuideDir, pageSubdir.Replace('/', global::System.IO.Path.DirectorySeparatorChar));

        var findings = DiagramProcessor.ValidateImageRefs(
            "topic.md.dt", $"![shot]({reference})", tree.ImagesDir, pageDir);

        if (shouldResolve)
        {
            Assert.Empty(findings);
        }
        else
        {
            Assert.Equal("REACTOR_DOC_IMAGE_001", Assert.Single(findings).Code);
        }
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

            var ratio = (double)content / ((double)region.Width * region.Height);
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

        /// <summary>
        /// Directory a top-level guide page compiles to. References resolve
        /// relative to this, so <c>images/x.png</c> from here lands in
        /// <see cref="ImagesDir"/> exactly as it does in the real tree.
        /// </summary>
        public string GuideDir => global::System.IO.Path.Join(_root, "guide");

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
