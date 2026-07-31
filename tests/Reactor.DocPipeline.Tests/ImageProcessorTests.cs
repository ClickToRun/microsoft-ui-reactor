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
        var png = MakeSolidPng(800, 600, Color.CornflowerBlue);

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
        var png = MakeSolidPng(1000, 100, Color.Crimson);

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
    /// The blank check has to survive the sparse (step-2) scan that content
    /// cropping uses. A single dark pixel on an odd row/column is invisible to
    /// that scan, so a full-resolution confirmation pass must run before the
    /// frame is declared blank — otherwise a real screenshot could be thrown
    /// away, which is a worse failure than the one being fixed.
    /// </summary>
    [Fact]
    public void Process_accepts_content_missed_by_the_sparse_scan()
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
}
