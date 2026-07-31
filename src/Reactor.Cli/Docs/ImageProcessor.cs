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
        var bounds = FindContentBounds(source, ContentThreshold)
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
        var contentBounds = FindContentBounds(source, ContentThreshold)
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
    /// least one pixel below <see cref="ContentThreshold"/>. Cheap probe used
    /// by the capture poller to hold out for a painted frame; returns
    /// <see langword="true"/> for anything it cannot decode so an unexpected
    /// format falls through to the normal validation path instead of being
    /// silently discarded as "blank".
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
            return FindContentBounds(bmp, ContentThreshold) is not null;
        }
        catch (ArgumentException)
        {
            // GDI+ rejected the bytes — let the caller's normal path report it.
            return true;
        }
    }

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
    /// Locates the tight bounding box of non-background content, or
    /// <see langword="null"/> when the bitmap has none at all.
    /// </summary>
    /// <remarks>
    /// The scan samples every other row/column for speed, exactly as it always
    /// has. A frame that looks empty under that sampling is re-scanned at full
    /// resolution before we report <see langword="null"/> — a false "blank"
    /// verdict costs a real screenshot, so the cheap scan is only allowed to
    /// say "yes, content".
    /// </remarks>
    private static Rectangle? FindContentBounds(Bitmap bmp, int threshold) =>
        ScanContentBounds(bmp, threshold, step: 2) ?? ScanContentBounds(bmp, threshold, step: 1);

    private static Rectangle? ScanContentBounds(Bitmap bmp, int threshold, int step)
    {
        int top = -1, bottom = -1, left = -1, right = -1;

        // Scan from top
        for (int y = 0; y < bmp.Height; y++)
        {
            if (RowHasContent(bmp, y, threshold, step)) { top = y; break; }
        }

        // No sampled pixel anywhere is below the threshold — nothing to bound.
        if (top < 0) return null;

        // Scan from bottom
        for (int y = bmp.Height - 1; y >= top; y--)
        {
            if (RowHasContent(bmp, y, threshold, step)) { bottom = y; break; }
        }

        // Scan from left
        for (int x = 0; x < bmp.Width; x++)
        {
            if (ColumnHasContent(bmp, x, top, bottom, threshold, step)) { left = x; break; }
        }

        // Scan from right
        for (int x = bmp.Width - 1; x >= left; x--)
        {
            if (ColumnHasContent(bmp, x, top, bottom, threshold, step)) { right = x; break; }
        }

        return new Rectangle(left, top, right - left + 1, bottom - top + 1);
    }

    private static bool RowHasContent(Bitmap bmp, int y, int threshold, int step)
    {
        for (int x = 0; x < bmp.Width; x += step)
        {
            var p = bmp.GetPixel(x, y);
            if (p.R < threshold || p.G < threshold || p.B < threshold) return true;
        }
        return false;
    }

    private static bool ColumnHasContent(Bitmap bmp, int x, int yStart, int yEnd, int threshold, int step)
    {
        for (int y = yStart; y <= yEnd; y += step)
        {
            var p = bmp.GetPixel(x, y);
            if (p.R < threshold || p.G < threshold || p.B < threshold) return true;
        }
        return false;
    }

    /// <summary>
    /// Counts pixels darker than <see cref="ContentThreshold"/> inside
    /// <paramref name="region"/>. Used by the committed-corpus gate, which has
    /// to scan hundreds of images per compile, so this reads the locked bits a
    /// row at a time rather than going through <see cref="Bitmap.GetPixel"/>.
    /// </summary>
    internal static int CountContentPixels(Bitmap bmp, Rectangle region)
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
                global::System.Runtime.InteropServices.Marshal.Copy(
                    data.Scan0 + (y * data.Stride), row, 0, row.Length);
                for (int i = 0; i < row.Length; i += 4)
                {
                    // Format32bppArgb is BGRA in memory.
                    if (row[i + 3] == 0) continue; // fully transparent — not content
                    if (row[i] < ContentThreshold || row[i + 1] < ContentThreshold || row[i + 2] < ContentThreshold)
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
    /// Region of a <em>processed</em> screenshot that excludes the chrome
    /// <see cref="AddBorderAndShadow"/> itself draws — the 1&#160;px border ring and
    /// the <see cref="ShadowOffset"/>&#160;+&#160;<see cref="ShadowBlur"/> strip along the
    /// right and bottom edges. Without this inset a blank capture would still
    /// score its own border as "content" and the gate could never fire.
    /// </summary>
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
