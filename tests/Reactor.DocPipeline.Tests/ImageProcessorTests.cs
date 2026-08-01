using System.Drawing;
using System.Drawing.Imaging;
using Microsoft.UI.Reactor.Cli.Docs;
using Xunit;

namespace Microsoft.UI.Reactor.Cli.Docs.Tests;

/// <summary>
/// Spec 041 Phase 2.0: <see cref="ImageProcessor.ProcessThumb"/> downscales a
/// captured frame to a fixed-size letterboxed thumbnail for the controls-catalog
/// index. These tests exercise size + aspect-ratio behavior; the content-fidelity
/// path is covered by the golden screenshots produced by the doc-app harness.
/// </summary>
public class ImageProcessorTests
{
    [Fact]
    public void ProcessThumb_produces_target_dimensions()
    {
        // The mark is not decoration: ProcessThumb refuses a uniform frame
        // (see ImageProcessor.IsUniformFill), and a solid fill of any colour is
        // one. This test is about output dimensions, so it needs a frame that
        // clears the blank guard for a reason unrelated to what it asserts.
        var png = MakeMarkedPng(800, 600, Color.CornflowerBlue);

        var bytes = ImageProcessor.ProcessThumb(png, 320, 240);

        using var ms = new MemoryStream(bytes);
        using var bmp = new Bitmap(ms);
        Assert.Equal(320, bmp.Width);
        Assert.Equal(240, bmp.Height);
    }

    [Fact]
    public void ProcessThumb_letterboxes_non_matching_aspect()
    {
        // 1000×100 source against a 320×240 target — wide aspect should fit
        // horizontally with white letterbox top/bottom.
        var png = MakeMarkedPng(1000, 100, Color.Crimson);

        var bytes = ImageProcessor.ProcessThumb(png, 320, 240);

        using var ms = new MemoryStream(bytes);
        using var bmp = new Bitmap(ms);
        Assert.Equal(320, bmp.Width);
        Assert.Equal(240, bmp.Height);
        // Top edge should be the white letterbox, not crimson.
        var topPixel = bmp.GetPixel(160, 4);
        Assert.True(topPixel.R > 240 && topPixel.G > 240 && topPixel.B > 240,
            $"expected letterbox white, got {topPixel}");
    }

    [Fact]
    public void ProcessThumb_rejects_invalid_dimensions()
    {
        var png = MakeSolidPng(100, 100, Color.Black);
        Assert.Throws<ArgumentException>(() => ImageProcessor.ProcessThumb(png, 0, 240));
    }

    [Fact]
    public void ProcessThumb_rejects_non_image_bytes()
    {
        var bogus = new byte[] { 0x01, 0x02, 0x03, 0x04 };
        Assert.Throws<ArgumentException>(() => ImageProcessor.ProcessThumb(bogus));
    }

    [Fact]
    public void Process_none_crop_preserves_full_frame_before_chrome()
    {
        var png = MakePngWithCenteredContent(120, 80, 20, 20);

        var bytes = ImageProcessor.Process(png, ScreenshotCropMode.None);

        using var ms = new MemoryStream(bytes);
        using var bmp = new Bitmap(ms);
        Assert.Equal(128, bmp.Width);
        Assert.Equal(88, bmp.Height);
    }

    [Theory]
    [InlineData(null, nameof(ScreenshotCropMode.Content))]
    [InlineData("", nameof(ScreenshotCropMode.Content))]
    [InlineData("content", nameof(ScreenshotCropMode.Content))]
    [InlineData("none", nameof(ScreenshotCropMode.None))]
    public void ParseCropMode_accepts_supported_values(string? value, string expected)
    {
        Assert.Equal(expected, ImageProcessor.ParseCropMode(value).ToString());
    }

    [Fact]
    public void ParseCropMode_rejects_unknown_values()
    {
        Assert.Throws<ArgumentException>(() => ImageProcessor.ParseCropMode("bounds"));
    }

    // ── Blank-frame rejection (issue #989) ────────────────────────────────
    // A doc-app window that never painted yields a solid-white frame. Content
    // cropping has nothing to crop to, so the stub used to survive the whole
    // pipeline, gain border + shadow, and be written over the committed asset
    // as a ~3 KB white rectangle. Process must refuse it instead.

    [Theory]
    [InlineData("content")]
    [InlineData("none")]
    public void Process_rejects_blank_frame(string cropMode)
    {
        // Pure white is the literal shape of an unpainted WinUI surface.
        var png = MakeSolidPng(400, 300, Color.White);

        Assert.Throws<BlankFrameException>(
            () => ImageProcessor.Process(png, ImageProcessor.ParseCropMode(cropMode)));
    }

    [Fact]
    public void Process_rejects_near_white_frame()
    {
        // The guard is a threshold, not an equality test: a frame that is
        // merely *almost* white (an unpainted surface under a slightly tinted
        // theme) is just as useless as a pure-white one.
        var png = MakeSolidPng(400, 300, Color.FromArgb(250, 251, 252));

        Assert.Throws<BlankFrameException>(() => ImageProcessor.Process(png));
    }

    /// <summary>
    /// Positive control for <see cref="Process_rejects_blank_frame"/>. Without
    /// it the rejection tests would pass just as well against a
    /// <c>Process</c> that threw unconditionally, so this is what makes the
    /// pair non-vacuous: the same white frame plus one small dark rectangle
    /// must still compile through cleanly.
    /// </summary>
    [Theory]
    [InlineData("content")]
    [InlineData("none")]
    public void Process_accepts_frame_with_minimal_content(string cropMode)
    {
        var png = MakePngWithCenteredContent(400, 300, 4, 4);

        var bytes = ImageProcessor.Process(png, ImageProcessor.ParseCropMode(cropMode));

        using var ms = new MemoryStream(bytes);
        using var bmp = new Bitmap(ms);
        Assert.True(bmp.Width > 0 && bmp.Height > 0);
    }

    [Fact]
    public void ProcessThumb_rejects_blank_frame()
    {
        var png = MakeSolidPng(800, 600, Color.White);

        Assert.Throws<BlankFrameException>(() => ImageProcessor.ProcessThumb(png, 320, 240));
    }

    [Fact]
    public void ProcessThumb_accepts_frame_with_minimal_content()
    {
        var png = MakePngWithCenteredContent(800, 600, 4, 4);

        var bytes = ImageProcessor.ProcessThumb(png, 320, 240);

        using var ms = new MemoryStream(bytes);
        using var bmp = new Bitmap(ms);
        Assert.Equal(320, bmp.Width);
    }

    /// <summary>
    /// The scan that preceded this one sampled every other column (<c>x += 2</c>),
    /// which had two failure modes. The visible one: content living only on odd
    /// columns looked blank. The dangerous one: content on <em>both</em> odd and
    /// even columns produced a bounding box drawn around only the even ones, so
    /// the crop silently shaved real pixels off an otherwise plausible-looking
    /// screenshot.
    /// </summary>
    /// <remarks>
    /// Asserting only that <c>Process</c> succeeds would not catch the second
    /// mode — the sampled scan succeeds there too, it just returns the wrong
    /// box. So this measures the output dimensions, which are a direct function
    /// of the bounds: leftmost content at x=41 and rightmost at x=101 spans 61
    /// columns, plus <c>ContentPadding</c> on each side and the border/shadow
    /// chrome. Under the old sampled scan the left edge would have snapped to
    /// x=42 and the right to x=100, giving a narrower image.
    /// </remarks>
    [Fact]
    public void Process_bounds_content_on_odd_columns_exactly()
    {
        using var bmp = new Bitmap(200, 200, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.White);
        }
        bmp.SetPixel(41, 101, Color.Black);  // odd column, odd row — leftmost
        bmp.SetPixel(101, 41, Color.Black);  // odd column, odd row — rightmost
        bmp.SetPixel(70, 70, Color.Black);   // even/even, so the sampled scan saw *something*

        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);

        var bytes = ImageProcessor.Process(ms.ToArray());

        using var outMs = new MemoryStream(bytes);
        using var outBmp = new Bitmap(outMs);

        // Content spans x∈[41,101] and y∈[41,101] → 61×61, padded by
        // ContentPadding (8) on each side → 77×77, plus the 8px chrome canvas.
        Assert.Equal(77 + 8, outBmp.Width);
        Assert.Equal(77 + 8, outBmp.Height);
    }

    /// <summary>
    /// The blank verdict itself must not be sampled either: a single dark pixel
    /// on an odd column is a real screenshot and rejecting it would be a worse
    /// failure than the one being fixed.
    /// </summary>
    [Fact]
    public void Process_accepts_a_single_pixel_on_an_odd_column()
    {
        using var bmp = new Bitmap(200, 200, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.White);
        }
        bmp.SetPixel(101, 101, Color.Black); // odd row and odd column

        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);

        var bytes = ImageProcessor.Process(ms.ToArray());

        Assert.NotEmpty(bytes);
    }

    [Fact]
    public void FrameHasContent_distinguishes_blank_from_painted()
    {
        Assert.False(ImageProcessor.FrameHasContent(MakeSolidPng(64, 64, Color.White)));
        Assert.True(ImageProcessor.FrameHasContent(MakePngWithCenteredContent(64, 64, 8, 8)));
    }

    [Fact]
    public void FrameHasContent_treats_undecodable_bytes_as_content()
    {
        // Poll-loop guard: an unexpected payload must fall through to the
        // normal validation path (which reports it accurately) rather than be
        // silently discarded as "still blank" until the deadline expires.
        Assert.True(ImageProcessor.FrameHasContent(new byte[] { 0x01, 0x02, 0x03, 0x04 }));
    }

    /// <summary>
    /// A composition surface that never rendered comes back as transparent
    /// black — every channel zero, <em>including alpha</em>. Before the guard
    /// blended against white, an RGB-only threshold scored all of those pixels
    /// as content (0 &lt; 248), so the very frame most likely to be produced by
    /// a window that never painted was the one shape that sailed straight
    /// through and got written out as the solid-white stub.
    /// </summary>
    [Theory]
    [InlineData("content")]
    [InlineData("none")]
    public void Process_rejects_transparent_frame(string cropMode)
    {
        var png = MakeSolidPng(400, 300, Color.FromArgb(0, 0, 0, 0));

        Assert.Throws<BlankFrameException>(
            () => ImageProcessor.Process(png, ImageProcessor.ParseCropMode(cropMode)));
    }

    [Fact]
    public void ProcessThumb_rejects_transparent_frame()
    {
        Assert.Throws<BlankFrameException>(
            () => ImageProcessor.ProcessThumb(MakeSolidPng(800, 600, Color.FromArgb(0, 0, 0, 0)), 320, 240));
    }

    /// <summary>
    /// Non-vacuity pair for <see cref="Process_rejects_transparent_frame"/>:
    /// translucency is not by itself blankness. A half-opaque black pixel
    /// composites to mid-grey over white, which is visible content and must
    /// survive.
    /// </summary>
    [Fact]
    public void Process_accepts_translucent_content_that_composites_dark()
    {
        using var bmp = new Bitmap(120, 90, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.FromArgb(0, 0, 0, 0));
            using var ink = new SolidBrush(Color.FromArgb(128, 0, 0, 0));
            g.FillRectangle(ink, 40, 30, 20, 20);
        }
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);

        Assert.NotEmpty(ImageProcessor.Process(ms.ToArray()));
    }

    [Fact]
    public void FrameHasContent_is_false_for_a_transparent_frame()
    {
        Assert.False(ImageProcessor.FrameHasContent(MakeSolidPng(64, 64, Color.FromArgb(0, 0, 0, 0))));
    }

    /// <summary>
    /// The poller must not stop waiting on a frame <see cref="ImageProcessor.Process"/>
    /// will then refuse. <c>Process</c> rejects a uniform fill outright, so
    /// <c>FrameHasContent</c> has to agree: otherwise a themed window whose
    /// background painted before its content did satisfies the very first poll,
    /// and the capture is failed outright when waiting out the remaining
    /// deadline would have produced the real frame.
    /// </summary>
    /// <remarks>
    /// A uniformly <em>dark</em> fill is the only case that separates the two
    /// predicates. <c>IsContent</c> asks "darker than near-white", so every
    /// pixel of a dark fill scores as content and the cheap probe alone answers
    /// "painted". The white and transparent fills covered above are rejected by
    /// the content scan on its own, so they cannot tell the two apart — which is
    /// why this gap survived the existing coverage.
    /// </remarks>
    [Fact]
    public void FrameHasContent_agrees_with_Process_on_a_uniformly_dark_frame()
    {
        var dark = MakeSolidPng(64, 64, Color.FromArgb(255, 32, 32, 32));

        Assert.Throws<BlankFrameException>(() => ImageProcessor.Process(dark));
        Assert.False(ImageProcessor.FrameHasContent(dark));

        // Positive control. Without it this test would also pass if
        // FrameHasContent were changed to return false unconditionally, which
        // would hang every capture on the deadline instead of failing it.
        using var bmp = new Bitmap(64, 64, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.FromArgb(255, 32, 32, 32));
            using var ink = new SolidBrush(Color.FromArgb(255, 200, 200, 200));
            g.FillRectangle(ink, 20, 20, 10, 10);
        }
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        var painted = ms.ToArray();

        Assert.NotEmpty(ImageProcessor.Process(painted));
        Assert.True(ImageProcessor.FrameHasContent(painted));
    }

    /// <summary>
    /// The poll-loop probe and the corpus gate must agree on what "content"
    /// means, or a frame the poller waits out would be one the gate accepts
    /// (or worse, the reverse). They share <c>IsContent</c>; this pins the two
    /// entry points to the same answer over the same region.
    /// </summary>
    [Theory]
    [InlineData(0, 0, 0, 0)]       // transparent black — the dangerous case
    [InlineData(255, 255, 255, 255)] // opaque white
    [InlineData(255, 0, 0, 0)]     // opaque black
    [InlineData(128, 0, 0, 0)]     // translucent black
    [InlineData(255, 250, 251, 252)] // near-white
    public void HasContentPixel_agrees_with_CountContentPixels(int a, int r, int g, int b)
    {
        using var bmp = new Bitmap(24, 16, PixelFormat.Format32bppArgb);
        using (var gfx = Graphics.FromImage(bmp))
        {
            gfx.CompositingMode = global::System.Drawing.Drawing2D.CompositingMode.SourceCopy;
            gfx.Clear(Color.FromArgb(a, r, g, b));
        }
        var region = new Rectangle(0, 0, bmp.Width, bmp.Height);

        Assert.Equal(
            ImageProcessor.CountContentPixels(bmp, region) > 0,
            ImageProcessor.HasContentPixel(bmp, region));
    }

    /// <summary>
    /// <see cref="ImageProcessor.InteriorRegion"/> must exclude every pixel the
    /// pipeline's own <c>AddBorderAndShadow</c> draws. If it did not, a blank
    /// screenshot would always score its border as content and the committed-
    /// corpus gate could never fire.
    /// </summary>
    [Fact]
    public void InteriorRegion_excludes_pipeline_chrome()
    {
        var processed = ImageProcessor.Process(MakePngWithCenteredContent(120, 80, 20, 20));
        using var ms = new MemoryStream(processed);
        using var bmp = new Bitmap(ms);

        var interior = ImageProcessor.InteriorRegion(bmp.Width, bmp.Height);

        // Chrome lives on the outer ring: border at 0 and w-9/h-9 (the source
        // edge), shadow strip beyond it.
        Assert.True(interior.X >= 2);
        Assert.True(interior.Y >= 2);
        Assert.True(interior.Right <= bmp.Width - 8);
        Assert.True(interior.Bottom <= bmp.Height - 8);
    }

    /// <summary>
    /// Catalog thumbnails come out of <c>ProcessThumb</c>, which draws no border
    /// and no shadow, so the chrome inset must not be applied to them. Applying
    /// it would discard up to 10&#160;px along the right and bottom edges and
    /// could report a perfectly good thumbnail as blank. The pipeline's
    /// <c>-thumb</c> filename suffix is the only signal available from a file on
    /// disk.
    /// </summary>
    [Theory]
    [InlineData("images/controls/button-thumb.png")]
    [InlineData(@"C:\repo\docs\guide\images\controls\button-thumb.png")]
    [InlineData("images/controls/BUTTON-THUMB.PNG")]
    public void ContentRegionFor_scans_thumbnails_whole(string path)
    {
        Assert.Equal(new Rectangle(0, 0, 320, 240), ImageProcessor.ContentRegionFor(path, 320, 240));
    }

    [Fact]
    public void ContentRegionFor_insets_full_size_captures()
    {
        var region = ImageProcessor.ContentRegionFor("images/controls/button.png", 320, 240);

        Assert.Equal(ImageProcessor.InteriorRegion(320, 240), region);
        Assert.NotEqual(new Rectangle(0, 0, 320, 240), region);
    }

    /// <summary>
    /// The failure the thumb branch exists to prevent, made concrete: content
    /// that lives only in the strip the chrome inset would trim. Scored whole
    /// it is content; scored with the full-size inset it vanishes.
    /// </summary>
    [Fact]
    public void Thumbnail_content_in_the_inset_strip_is_not_scored_as_blank()
    {
        using var bmp = new Bitmap(320, 240, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.White);
            using var ink = new SolidBrush(Color.Black);
            g.FillRectangle(ink, 315, 235, 4, 4); // inside the 10px trailing inset
        }

        Assert.False(ImageProcessor.HasContentPixel(bmp, ImageProcessor.InteriorRegion(320, 240)));
        Assert.True(ImageProcessor.HasContentPixel(bmp, ImageProcessor.ContentRegionFor("x-thumb.png", 320, 240)));
    }

    [Fact]
    public void InteriorRegion_falls_back_to_full_rect_when_too_small_to_inset()
    {
        // Thumbnails carry no chrome at all and can be smaller than the inset.
        // An empty region would count zero content and false-fire the gate.
        var r = ImageProcessor.InteriorRegion(8, 8);

        Assert.Equal(new Rectangle(0, 0, 8, 8), r);
    }

    private static byte[] MakeSolidPng(int w, int h, Color color)
    {
        using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(color);
        }
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }

    /// <summary>
    /// A solid fill with one contrasting pixel, so the frame is not a uniform
    /// fill and clears the blank guard while staying trivially predictable for
    /// dimension and letterbox assertions.
    /// </summary>
    private static byte[] MakeMarkedPng(int w, int h, Color color)
    {
        using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(color);
        }
        bmp.SetPixel(0, 0, Color.FromArgb(255, 16, 16, 16));
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }

    /// <summary>
    /// The scan locks bits as <c>Format32bppArgb</c> regardless of how the file
    /// decoded, which raises a fair question: JPEG decodes as 24bpp and the
    /// blank gate explicitly accepts <c>.jpg</c>, so if that lock threw, every
    /// JPEG would fall into the decode-failure catch and be silently reported
    /// as "not blank" — a gate that skips a whole format while reporting clean.
    /// It doesn't: <c>LockBits</c> with an explicit format converts rather than
    /// throwing. This pins that, because the failure mode would be invisible.
    /// </summary>
    /// <remarks>
    /// Non-vacuous by construction: the blank and content variants of each
    /// format must give <em>opposite</em> answers. If the lock threw, both arms
    /// would land in the same catch and the pair would agree.
    /// </remarks>
    [Theory]
    [InlineData(PixelFormat.Format24bppRgb, "png")]
    [InlineData(PixelFormat.Format24bppRgb, "jpeg")]
    [InlineData(PixelFormat.Format32bppArgb, "png")]
    [InlineData(PixelFormat.Format8bppIndexed, "png")]
    [InlineData(PixelFormat.Format1bppIndexed, "png")]
    public void Content_detection_survives_every_pixel_format_the_gate_accepts(
        PixelFormat format, string container)
    {
        var withContent = MakeInFormat(format, container, content: true);
        var blank = MakeInFormat(format, container, content: false);

        using var contentBmp = LoadBitmap(withContent);
        using var blankBmp = LoadBitmap(blank);
        var region = new Rectangle(0, 0, 80, 60);

        var contentCount = ImageProcessor.CountContentPixels(contentBmp, region);
        var blankCount = ImageProcessor.CountContentPixels(blankBmp, region);

        // Report the inputs, not just the verdict: if the lock silently
        // degraded, these would be 0 and 0 and the assertions below would be
        // comparing two failures to each other.
        Assert.True(contentCount > 0, $"expected content pixels, got {contentCount} ({format}/{container})");
        Assert.Equal(0, blankCount);
        Assert.True(ImageProcessor.HasContentPixel(contentBmp, region));
        Assert.False(ImageProcessor.HasContentPixel(blankBmp, region));

        // And end to end, which is what the gate actually calls.
        Assert.True(ImageProcessor.FrameHasContent(withContent));
        Assert.False(ImageProcessor.FrameHasContent(blank));
        Assert.Throws<BlankFrameException>(
            () => ImageProcessor.Process(blank, ImageProcessor.ParseCropMode("content")));
    }

    private static Bitmap LoadBitmap(byte[] bytes) => new(new MemoryStream(bytes));

    private static byte[] MakeInFormat(PixelFormat format, string container, bool content)
    {
        var imageFormat = container == "jpeg" ? ImageFormat.Jpeg : ImageFormat.Png;

        // Indexed formats can't back a Graphics, so draw at 32bpp and convert.
        var indexed = format is PixelFormat.Format8bppIndexed or PixelFormat.Format1bppIndexed;
        using var src = new Bitmap(80, 60, indexed ? PixelFormat.Format32bppArgb : format);
        using (var g = Graphics.FromImage(src))
        {
            g.Clear(Color.White);
            if (content)
            {
                using var brush = new SolidBrush(Color.Black);
                g.FillRectangle(brush, 10, 10, 20, 20);
            }
        }

        using var ms = new MemoryStream();
        if (indexed)
        {
            using var converted = src.Clone(new Rectangle(0, 0, 80, 60), format);
            converted.Save(ms, imageFormat);
        }
        else
        {
            src.Save(ms, imageFormat);
        }
        return ms.ToArray();
    }

    private static byte[] MakePngWithCenteredContent(int w, int h, int contentW, int contentH)
    {
        using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.White);
            using var brush = new SolidBrush(Color.Black);
            g.FillRectangle(brush, (w - contentW) / 2, (h - contentH) / 2, contentW, contentH);
        }

        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }

    /// <summary>
    /// A frame that renders as one flat white sheet is uniform even when its
    /// pixels reach that white through different alpha values.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The regression this pins is a disagreement between two predicates in this
    /// file. <c>IsContent</c> composites source-over white for
    /// <c>0 &lt; a &lt; 255</c>; <c>IsUniformFill</c> used to compare the stored
    /// BGRA bytes, normalising only <c>a == 0</c>. So the pixels below — both
    /// visibly white — compared as different, the frame scored as varied, and a
    /// contentless capture passed the blankness gate and was written over a
    /// committed screenshot. The direction is what makes it worth a test:
    /// the gate fails <em>open</em>, which is the exact failure this file exists
    /// to prevent.
    /// </para>
    /// <para>
    /// Non-vacuity: the assertion is <c>True</c> and the pre-fix code returned
    /// <c>False</c>, measured. The paired test below supplies the other
    /// direction, so neither is satisfied by a constant.
    /// </para>
    /// </remarks>
    [Fact]
    public void Uniform_fill_compares_composited_colour_not_stored_bytes()
    {
        using var bmp = new Bitmap(8, 8, PixelFormat.Format32bppArgb);
        for (int y = 0; y < bmp.Height; y++)
        {
            for (int x = 0; x < bmp.Width; x++)
            {
                // Alternate opaque white with half-transparent white. Both
                // composite to (255,255,255) over a white page; their stored
                // bytes differ in the alpha channel alone.
                var a = ((x + y) % 2 == 0) ? 255 : 128;
                bmp.SetPixel(x, y, Color.FromArgb(a, 255, 255, 255));
            }
        }

        // Premise: the two pixel kinds really are stored differently, so the old
        // byte comparison had something to disagree about. Without this the test
        // would still pass if SetPixel silently flattened alpha.
        Assert.NotEqual(bmp.GetPixel(0, 0).A, bmp.GetPixel(1, 0).A);

        Assert.True(
            ImageProcessor.IsUniformFill(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height)),
            "a frame that renders as one flat white sheet must read as uniform regardless of the alpha it got there through");
    }

    /// <summary>
    /// The other direction: compositing must not flatten a frame that genuinely
    /// varies. Guards against "return true" satisfying the test above.
    /// </summary>
    [Fact]
    public void Uniform_fill_still_rejects_a_frame_with_two_visible_colours()
    {
        using var bmp = new Bitmap(8, 8, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp)) g.Clear(Color.White);
        bmp.SetPixel(4, 4, Color.FromArgb(255, 0, 0, 0));

        Assert.False(
            ImageProcessor.IsUniformFill(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height)),
            "a white sheet with one black pixel composites to two distinct colours and is not uniform");
    }

    /// <summary>
    /// The catalog-thumb stem is derived once, and adding the suffix to an id
    /// that already carries it is not a second append.
    /// </summary>
    /// <remarks>
    /// <c>ScreenshotCapture</c> picks the filename it writes and
    /// <c>DocAssembler</c> picks the URL that points at it. They used to hold
    /// separate copies of this rule, so a divergence would have surfaced as a
    /// broken image link rather than a compile error. The double-suffix case had
    /// no diagnostic at all: the reserved-suffix check in <c>CompileCommand</c>
    /// exempts <c>catalog-thumb</c> by design, since for a thumb the suffix is
    /// correct rather than a collision.
    /// </remarks>
    [Theory]
    [InlineData("widget", true, "widget-thumb")]
    [InlineData("widget", false, "widget")]
    [InlineData("widget-thumb", true, "widget-thumb")]
    [InlineData("widget-THUMB", true, "widget-THUMB")]
    [InlineData("widget-thumb", false, "widget-thumb")]
    [InlineData("thumbnail", true, "thumbnail-thumb")]
    public void Thumb_aware_file_base_appends_at_most_once(string id, bool isThumb, string expected)
        => Assert.Equal(expected, ImageProcessor.ThumbAwareFileBase(id, isThumb));

    /// <summary>
    /// Applying the rule twice changes nothing — the property both call sites
    /// depend on, stated directly rather than inferred from the cases above.
    /// </summary>
    [Fact]
    public void Thumb_aware_file_base_is_idempotent()
        => Assert.All(
            new[] { "widget", "widget-thumb", "a-thumb-b", "thumb" }
                .Select(id => ImageProcessor.ThumbAwareFileBase(id, isThumb: true)),
            once => Assert.Equal(once, ImageProcessor.ThumbAwareFileBase(once, isThumb: true)));
}
