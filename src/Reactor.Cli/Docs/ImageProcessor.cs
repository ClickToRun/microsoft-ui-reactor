using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace Microsoft.UI.Reactor.Cli.Docs;

/// <summary>
/// Post-processes captured screenshots: auto-crops whitespace,
/// adds a subtle border and drop shadow so images don't blend into the page.
/// </summary>
internal static class ImageProcessor
{
    private const int ContentPadding = 8;   // breathing room inside the border
    private const int ShadowOffset = 2;     // shadow offset (right + down)
    private const int ShadowBlur = 6;       // number of graduated shadow layers
    private const float ShadowMaxAlpha = 0.12f;

    /// <summary>Hard cap on input image size in bytes. TASK-044.</summary>
    public const int MaxImageBytes = 64 * 1024 * 1024; // 64 MiB

    /// <summary>Hard cap on decoded dimensions. TASK-044.</summary>
    public const int MaxImageDimension = 16384;

    /// <summary>
    /// Per-channel value at or above which a pixel counts as background. Shared
    /// by content cropping and the blank-frame guard so both agree on what
    /// "empty" means.
    /// </summary>
    internal const int ContentThreshold = 248;

    /// <summary>
    /// Crops whitespace then downscales to <paramref name="targetW"/>×<paramref name="targetH"/>
    /// preserving aspect (letterboxed with white). Used by <c>kind: catalog-thumb</c>
    /// in <c>doc-manifest.yaml</c> for the controls-catalog index thumbnails (spec 041 §6.3 + §12 Q7).
    /// No border / drop shadow — the thumbnail itself is the visual; the catalog page
    /// renders it inside a table cell where additional chrome would be noise.
    /// </summary>
    /// <exception cref="BlankFrameException">
    /// The frame contains no content — see <see cref="Process"/>.
    /// </exception>
    public static byte[] ProcessThumb(byte[] frameBytes, int targetW = 320, int targetH = 240)
    {
        if (frameBytes is null || frameBytes.Length == 0)
            throw new ArgumentException("Empty image bytes.", nameof(frameBytes));
        if (frameBytes.Length > MaxImageBytes)
            throw new ArgumentException($"Image exceeds {MaxImageBytes / (1024 * 1024)} MiB cap.", nameof(frameBytes));
        if (!HasKnownImageMagic(frameBytes))
            throw new ArgumentException("Image bytes are neither PNG nor JPEG.", nameof(frameBytes));
        if (targetW <= 0 || targetH <= 0)
            throw new ArgumentException("Target dimensions must be positive.", nameof(targetW));

        using var ms = new MemoryStream(frameBytes);
        using var source = new Bitmap(ms);
        if (source.Width > MaxImageDimension || source.Height > MaxImageDimension)
            throw new ArgumentException($"Image dimensions exceed {MaxImageDimension}px cap.", nameof(frameBytes));

        // Trim whitespace to focus the thumb on real content.
        var bounds = FindContentBounds(source)
            ?? throw BlankFrameException.ForFrame(source.Width, source.Height);
        bounds = InflateClamp(bounds, ContentPadding, source.Width, source.Height);
        using var cropped = source.Clone(bounds, PixelFormat.Format32bppArgb);

        // Compute letterbox to preserve aspect.
        double scale = Math.Min((double)targetW / cropped.Width, (double)targetH / cropped.Height);
        int drawW = Math.Max(1, (int)Math.Round(cropped.Width * scale));
        int drawH = Math.Max(1, (int)Math.Round(cropped.Height * scale));
        int offX = (targetW - drawW) / 2;
        int offY = (targetH - drawH) / 2;

        using var result = new Bitmap(targetW, targetH, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(result))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(Color.White);
            g.DrawImage(cropped, new Rectangle(offX, offY, drawW, drawH));
        }

        using var output = new MemoryStream();
        result.Save(output, ImageFormat.Png);
        return output.ToArray();
    }

    /// <summary>
    /// Processes a captured frame, adds border + drop shadow, and returns PNG bytes.
    /// </summary>
    /// <exception cref="BlankFrameException">
    /// The frame contains no content — every pixel is at or above
    /// <see cref="ContentThreshold"/>. A doc app whose window never painted (no
    /// interactive desktop, capture polled too early) yields exactly this, and
    /// writing it would silently replace a good committed screenshot with a
    /// solid-white stub. Callers must treat it as a failed capture, not a result.
    /// </exception>
    public static byte[] Process(byte[] frameBytes, ScreenshotCropMode cropMode = ScreenshotCropMode.Content)
    {
        // SECURITY (TASK-044): validate magic bytes and size before handing
        // attacker-controllable data to GDI+. GDI+ has a long history of
        // decode-time vulnerabilities; pre-filter to known formats and bound
        // the input size.
        if (frameBytes is null || frameBytes.Length == 0)
            throw new ArgumentException("Empty image bytes.", nameof(frameBytes));
        if (frameBytes.Length > MaxImageBytes)
            throw new ArgumentException($"Image exceeds {MaxImageBytes / (1024 * 1024)} MiB cap.", nameof(frameBytes));
        if (!HasKnownImageMagic(frameBytes))
            throw new ArgumentException("Image bytes are neither PNG nor JPEG.", nameof(frameBytes));

        using var ms = new MemoryStream(frameBytes);
        using var source = new Bitmap(ms);
        if (source.Width > MaxImageDimension || source.Height > MaxImageDimension)
            throw new ArgumentException($"Image dimensions exceed {MaxImageDimension}px cap.", nameof(frameBytes));

        // Blank check runs before the crop switch so it covers `crop: none`
        // too — the question is whether the *frame* has content, not whether
        // this particular crop mode would have trimmed it away.
        var contentBounds = FindContentBounds(source)
            ?? throw BlankFrameException.ForFrame(source.Width, source.Height);

        var bounds = cropMode switch
        {
            ScreenshotCropMode.Content => InflateClamp(
                contentBounds,
                ContentPadding,
                source.Width,
                source.Height),
            ScreenshotCropMode.None => new Rectangle(0, 0, source.Width, source.Height),
            _ => throw new ArgumentOutOfRangeException(nameof(cropMode), cropMode, "Unknown screenshot crop mode.")
        };

        using var cropped = source.Clone(bounds, PixelFormat.Format32bppArgb);

        // 2. Add border + shadow
        using var result = AddBorderAndShadow(cropped);

        // 3. Encode as PNG
        using var output = new MemoryStream();
        result.Save(output, ImageFormat.Png);
        return output.ToArray();
    }

    /// <summary>
    /// True when <paramref name="frameBytes"/> decodes to an image with at
    /// least one visible content pixel. Cheap probe used by the capture poller
    /// to hold out for a painted frame; returns <see langword="true"/> for
    /// anything it cannot decode so an unexpected format falls through to the
    /// normal validation path instead of being silently discarded as "blank".
    /// </summary>
    internal static bool FrameHasContent(byte[] frameBytes)
    {
        if (frameBytes is null || frameBytes.Length == 0) return false;
        if (frameBytes.Length > MaxImageBytes || !HasKnownImageMagic(frameBytes)) return true;
        try
        {
            using var ms = new MemoryStream(frameBytes);
            using var bmp = new Bitmap(ms);
            if (bmp.Width > MaxImageDimension || bmp.Height > MaxImageDimension) return true;
            // Deliberately not FindContentBounds: this runs on every poll of a
            // still-blank window, and the bounds scan is a GetPixel walk over
            // the whole frame (twice, with the full-resolution confirmation).
            // The locked-bits probe below short-circuits on the first content
            // pixel and reads a row at a time.
            return HasContentPixel(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height));
        }
        catch (Exception ex) when (ex is ArgumentException or OutOfMemoryException
                                      or global::System.Runtime.InteropServices.ExternalException)
        {
            // GDI+ rejected the bytes — let the caller's normal path report it.
            // All three arms mean the same thing here: GDI+ signals a corrupt or
            // unsupported image as ArgumentException *or* ExternalException
            // depending on the fault, and famously reports a malformed decode as
            // OutOfMemoryException with no memory pressure involved. This runs
            // inside the capture poll loop, so letting any of them escape would
            // abort the whole pass on one bad frame rather than polling again.
            return true;
        }
    }

    /// <summary>
    /// True when <paramref name="b"/>/<paramref name="g"/>/<paramref name="r"/>
    /// at alpha <paramref name="a"/> is visible content once composited over
    /// the white canvas <see cref="AddBorderAndShadow"/> draws.
    /// </summary>
    /// <remarks>
    /// Alpha is not optional here. A composition surface that never rendered
    /// comes back as transparent black — every channel zero, including alpha —
    /// and a naive RGB-only test scores every one of those pixels as content
    /// because 0 &lt; <see cref="ContentThreshold"/>. The frame would sail past
    /// the blank guard, get drawn over white by <c>AddBorderAndShadow</c>, and
    /// be written out as the same solid-white stub the guard exists to stop.
    /// Blending against white first is what makes "content" mean "visible in
    /// the file we are about to write".
    /// </remarks>
    private static bool IsContent(byte b, byte g, byte r, byte a)
    {
        if (a == 0) return false;
        if (a == 255) return b < ContentThreshold || g < ContentThreshold || r < ContentThreshold;

        // Source-over composite against opaque white, rounded.
        int inv = 255 - a;
        int cb = ((b * a) + (255 * inv) + 127) / 255;
        int cg = ((g * a) + (255 * inv) + 127) / 255;
        int cr = ((r * a) + (255 * inv) + 127) / 255;
        return cb < ContentThreshold || cg < ContentThreshold || cr < ContentThreshold;
    }

    /// <summary>
    /// True as soon as any pixel in <paramref name="region"/> is visible
    /// content. Same predicate as <see cref="CountContentPixels"/> but stops at
    /// the first hit — use it whenever the count itself is not needed.
    /// </summary>
    internal static bool HasContentPixel(Bitmap bmp, Rectangle region) =>
        ScanRegion(bmp, region, stopAtFirst: true) > 0;

    internal static ScreenshotCropMode ParseCropMode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            string.Equals(value, "content", StringComparison.OrdinalIgnoreCase))
        {
            return ScreenshotCropMode.Content;
        }

        if (string.Equals(value, "none", StringComparison.OrdinalIgnoreCase))
        {
            return ScreenshotCropMode.None;
        }

        throw new ArgumentException(
            $"Unsupported screenshot crop mode '{value}'. Expected 'content' or 'none'.",
            nameof(value));
    }

    /// <summary>
    /// Locates the tight bounding box of visible content, or
    /// <see langword="null"/> when the bitmap has none at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One exact pass over the locked bits. The scan this replaced sampled every
    /// other column (<c>x += 2</c>) for speed, which had two consequences: a
    /// frame whose only content sat on odd columns was reported blank, and — the
    /// worse of the two — a frame with content on <em>both</em> odd and even
    /// columns returned a box drawn only around the even ones, silently cropping
    /// real pixels away. The second failure was invisible because the result was
    /// a plausible-looking screenshot, just missing an edge.
    /// </para>
    /// <para>
    /// Sampling is not needed for speed here: the row-buffer read below is far
    /// cheaper per pixel than <see cref="Bitmap.GetPixel"/>, so the exact pass
    /// costs less than the sampled one it replaces.
    /// </para>
    /// </remarks>
    internal static Rectangle? FindContentBounds(Bitmap bmp)
    {
        var full = new Rectangle(0, 0, bmp.Width, bmp.Height);
        if (full.Width <= 0 || full.Height <= 0) return null;

        var data = bmp.LockBits(full, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var row = new byte[full.Width * 4];
            int top = -1, bottom = -1, left = int.MaxValue, right = -1;

            for (int y = 0; y < full.Height; y++)
            {
                // Scan0 + y * Stride addresses visual row y for either sign of
                // Stride: Scan0 points at the image's first scanline, and a
                // bottom-up DIB expresses "subsequent scanlines are at lower
                // addresses" as a negative Stride. Normalising the base pointer
                // and indexing with |Stride| mirrors such an image vertically —
                // see StrideOrientationTests, which pins this against GetPixel.
                global::System.Runtime.InteropServices.Marshal.Copy(
                    data.Scan0 + (y * data.Stride), row, 0, row.Length);

                int rowLeft = -1, rowRight = -1;
                for (int x = 0, i = 0; x < full.Width; x++, i += 4)
                {
                    // Format32bppArgb is BGRA in memory.
                    if (!IsContent(row[i], row[i + 1], row[i + 2], row[i + 3])) continue;
                    if (rowLeft < 0) rowLeft = x;
                    rowRight = x;
                }

                if (rowLeft < 0) continue;
                if (top < 0) top = y;
                bottom = y;
                if (rowLeft < left) left = rowLeft;
                if (rowRight > right) right = rowRight;
            }

            if (top < 0) return null;
            return new Rectangle(left, top, right - left + 1, bottom - top + 1);
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }

    /// <summary>
    /// Counts visible content pixels inside <paramref name="region"/>. Used by
    /// the committed-corpus gate, which has to scan hundreds of images per
    /// compile, so this reads the locked bits a row at a time rather than
    /// going through <see cref="Bitmap.GetPixel"/>.
    /// </summary>
    internal static int CountContentPixels(Bitmap bmp, Rectangle region) =>
        ScanRegion(bmp, region, stopAtFirst: false);

    private static int ScanRegion(Bitmap bmp, Rectangle region, bool stopAtFirst)
    {
        region = Rectangle.Intersect(region, new Rectangle(0, 0, bmp.Width, bmp.Height));
        if (region.Width <= 0 || region.Height <= 0) return 0;

        var data = bmp.LockBits(region, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var row = new byte[region.Width * 4];
            int count = 0;
            for (int y = 0; y < region.Height; y++)
            {
                // Sign-agnostic row addressing — see FindContentBounds. Counting
                // is order-insensitive anyway, so this site is unaffected by the
                // orientation either way; kept identical so the two scans can't
                // drift apart.
                global::System.Runtime.InteropServices.Marshal.Copy(
                    data.Scan0 + (y * data.Stride), row, 0, row.Length);
                for (int i = 0; i < row.Length; i += 4)
                {
                    // Format32bppArgb is BGRA in memory.
                    if (!IsContent(row[i], row[i + 1], row[i + 2], row[i + 3])) continue;
                    if (stopAtFirst) return 1;
                    count++;
                }
            }
            return count;
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }

    /// <summary>
    /// Region of a <em>processed screenshot</em> that excludes the chrome
    /// <see cref="AddBorderAndShadow"/> itself draws — the 1&#160;px border ring and
    /// the <see cref="ShadowOffset"/>&#160;+&#160;<see cref="ShadowBlur"/> strip along the
    /// right and bottom edges. Without this inset a blank capture would still
    /// score its own border as "content" and the gate could never fire.
    /// </summary>
    /// <remarks>
    /// Only meaningful for output of <see cref="Process"/>. Thumbnails
    /// (<see cref="ProcessThumb"/>) and hand-authored assets carry no chrome,
    /// so insetting them would discard real edge content — pass their full
    /// rectangle instead. <see cref="ContentRegionFor"/> makes that choice.
    /// </remarks>
    internal static Rectangle InteriorRegion(int width, int height)
    {
        const int LeadingInset = 2;                                    // 1px border + 1px antialias margin
        const int TrailingInset = ShadowOffset + ShadowBlur + 2;       // shadow strip + border + margin
        var w = width - LeadingInset - TrailingInset;
        var h = height - LeadingInset - TrailingInset;
        if (w <= 0 || h <= 0)
        {
            // Too small to inset meaningfully (thumbnails and hand-authored
            // assets can be tiny). Fall back to the whole image rather than an
            // empty region — an empty region would count zero and false-fire.
            return new Rectangle(0, 0, width, height);
        }
        return new Rectangle(LeadingInset, LeadingInset, w, h);
    }

    /// <summary>
    /// Filename suffix the pipeline reserves for catalog thumbnails.
    /// </summary>
    internal const string ThumbSuffix = "-thumb";

    /// <summary>
    /// True when <paramref name="path"/> carries the reserved catalog-thumbnail
    /// suffix, i.e. it was written by <see cref="ProcessThumb"/> and has no chrome.
    /// </summary>
    internal static bool HasThumbSuffix(string path) =>
        Path.GetFileNameWithoutExtension(path)
            .EndsWith(ThumbSuffix, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Region of a committed image the blank-screenshot gate should score.
    /// </summary>
    /// <remarks>
    /// Full-size captures go through <see cref="AddBorderAndShadow"/>, so their
    /// own chrome has to be excluded or the gate can never fire. Catalog
    /// thumbnails are written by <see cref="ProcessThumb"/>, which draws no
    /// border and no shadow — insetting one would silently ignore up to 10&#160;px
    /// of real content along its right and bottom edges and could report a
    /// perfectly good thumbnail as blank.
    /// <para>
    /// The filename is the only signal a committed file on disk carries, so
    /// <see cref="ThumbSuffix"/> is <em>reserved</em>: <c>docs compile</c> rejects a
    /// non-<c>catalog-thumb</c> manifest entry whose id ends in it
    /// (<c>REACTOR_DOC_SHOT_002</c>). Without that reservation a full-size
    /// screenshot could be named <c>foo-thumb</c>, get scored whole, and hide a
    /// blank capture behind its own border — the exact failure this gate exists
    /// to catch. The inference is sound because the reservation makes the
    /// collision unrepresentable, not because the convention is usually followed.
    /// </para>
    /// </remarks>
    internal static Rectangle ContentRegionFor(string path, int width, int height) =>
        HasThumbSuffix(path)
            ? new Rectangle(0, 0, width, height)
            : InteriorRegion(width, height);

    private static Bitmap AddBorderAndShadow(Bitmap source)
    {
        int w = source.Width;
        int h = source.Height;

        // Canvas: image + space for shadow on right/bottom edges
        int canvasW = w + ShadowOffset + ShadowBlur;
        int canvasH = h + ShadowOffset + ShadowBlur;

        var result = new Bitmap(canvasW, canvasH, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(result);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.White);

        // Draw drop shadow: graduated semi-transparent rectangles offset behind the image
        for (int i = ShadowBlur; i >= 1; i--)
        {
            float t = (float)i / ShadowBlur;               // 1.0 → 0.0 as we get closer
            int alpha = (int)(ShadowMaxAlpha * (1f - t) * 255);
            if (alpha <= 0) continue;

            using var brush = new SolidBrush(Color.FromArgb(alpha, 0, 0, 0));
            g.FillRectangle(brush,
                ShadowOffset + i,
                ShadowOffset + i,
                w - 1,
                h - 1);
        }

        // Draw the image
        g.DrawImage(source, 0, 0, w, h);

        // Draw 1px border
        using var borderPen = new Pen(Color.FromArgb(209, 213, 219), 1); // gray-300
        g.DrawRectangle(borderPen, 0, 0, w - 1, h - 1);

        return result;
    }

    private static Rectangle InflateClamp(Rectangle r, int padding, int maxW, int maxH)
    {
        int x = Math.Max(0, r.X - padding);
        int y = Math.Max(0, r.Y - padding);
        int right = Math.Min(maxW, r.Right + padding);
        int bottom = Math.Min(maxH, r.Bottom + padding);
        return new Rectangle(x, y, right - x, bottom - y);
    }

    /// <summary>
    /// Returns true iff <paramref name="bytes"/> starts with PNG or JPEG
    /// magic bytes. PNG: 89 50 4E 47 0D 0A 1A 0A. JPEG: FF D8 FF (any ext).
    /// TASK-044.
    /// </summary>
    internal static bool HasKnownImageMagic(byte[] bytes)
    {
        if (bytes.Length >= 8
            && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47
            && bytes[4] == 0x0D && bytes[5] == 0x0A && bytes[6] == 0x1A && bytes[7] == 0x0A)
            return true;
        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
            return true;
        return false;
    }
}

internal enum ScreenshotCropMode
{
    Content,
    None
}
