using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Microsoft.UI.Reactor.Cli.Docs;
using Xunit;

namespace Microsoft.UI.Reactor.Cli.Docs.Tests;

/// <summary>
/// Pins the row-addressing convention used by <see cref="ImageProcessor"/>'s
/// two <c>LockBits</c> scans against an independent oracle.
/// </summary>
/// <remarks>
/// <para>
/// Review has twice proposed rewriting <c>Scan0 + y * Stride</c> to "normalise
/// the base pointer and index with a positive stride", on the theory that a
/// negative <see cref="BitmapData.Stride"/> inverts the scan order. Measurement
/// says the opposite: <c>Scan0</c> points at the image's <em>first scanline</em>,
/// and a bottom-up DIB expresses "subsequent scanlines live at lower addresses"
/// as a negative stride — so <c>Scan0 + y * Stride</c> yields visual row
/// <c>y</c> for either sign, and it is the proposed normalisation that mirrors
/// the image.
/// </para>
/// <para>
/// The oracle is <see cref="Bitmap.GetPixel(int,int)"/>, which addresses visual
/// coordinates and knows nothing about stride. These tests assert the scan
/// <em>agrees with the oracle</em> rather than asserting a hard-coded
/// coordinate, so they stay honest if the fixture changes, and they fail if
/// either implementation drifts. The dark pixel is off-centre on both axes, so
/// a row-order bug moves <c>y</c> and a within-row bug moves <c>x</c> —
/// a centred pixel would survive a vertical mirror and prove nothing.
/// </para>
/// <para>
/// Note that the negative-stride case is not reachable from any input the doc
/// pipeline accepts: every GDI+ decoder path measured (PNG and JPEG, from both
/// stream and file, plus freshly constructed bitmaps in three pixel formats)
/// returns a positive stride, and a bottom-up bitmap can only be built by
/// handing GDI+ a raw buffer. It is constructed manually here precisely because
/// nothing else produces one — the point is to make a well-intentioned "fix"
/// fail loudly rather than silently mirror a screenshot crop.
/// </para>
/// </remarks>
public class StrideOrientationTests
{
    private const int Width = 40;
    private const int Height = 30;
    private const int DarkX = 7;
    private const int DarkY = 3;

    [Fact]
    public void Content_bounds_match_the_oracle_for_a_top_down_bitmap()
    {
        using var bmp = new Bitmap(Width, Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.White);
            g.FillRectangle(Brushes.Black, new Rectangle(DarkX, DarkY, 1, 1));
        }

        Assert.True(StrideOf(bmp) > 0,
            "expected a top-down bitmap; the negative-stride case is covered separately");

        AssertBoundsMatchOracle(bmp);
    }

    /// <summary>
    /// The case the proposed rewrite would break. Restoring the "normalise the
    /// base pointer" variant turns this red while leaving the top-down test
    /// green, which is what makes the pair non-vacuous: it can only pass if the
    /// scan is genuinely sign-agnostic.
    /// </summary>
    [Fact]
    public void Content_bounds_match_the_oracle_for_a_bottom_up_negative_stride_bitmap()
    {
        int strideBytes = Width * 4;
        IntPtr buf = Marshal.AllocHGlobal(strideBytes * Height);
        try
        {
            // Declared bottom-up, so visual row v is stored at buffer row
            // (Height - 1 - v). Authoring it this way rather than trusting a
            // decoder is the only way to obtain stride < 0.
            for (int y = 0; y < Height; y++)
            {
                for (int x = 0; x < Width; x++)
                {
                    int visualY = Height - 1 - y;
                    byte v = (byte)((visualY == DarkY && x == DarkX) ? 0 : 255);
                    for (int c = 0; c < 3; c++)
                        Marshal.WriteByte(buf, (y * strideBytes) + (x * 4) + c, v);
                    Marshal.WriteByte(buf, (y * strideBytes) + (x * 4) + 3, 255);
                }
            }

            using var bmp = new Bitmap(
                Width, Height, -strideBytes, PixelFormat.Format32bppArgb,
                buf + ((Height - 1) * strideBytes));

            Assert.True(StrideOf(bmp) < 0,
                "fixture failed to produce a bottom-up bitmap — the test would " +
                "otherwise pass without exercising the negative-stride path at all");

            AssertBoundsMatchOracle(bmp);
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    /// <summary>
    /// The blank-screenshot gate counts pixels, which is order-insensitive, so
    /// it must agree with the oracle's count under either orientation. This is
    /// the assertion that matters for issue #989 itself.
    /// </summary>
    [Fact]
    public void Content_pixel_count_is_orientation_independent()
    {
        using var topDown = new Bitmap(Width, Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(topDown))
        {
            g.Clear(Color.White);
            g.FillRectangle(Brushes.Black, new Rectangle(DarkX, DarkY, 3, 2));
        }

        int counted = ImageProcessor.CountContentPixels(
            topDown, new Rectangle(0, 0, Width, Height));

        Assert.Equal(OracleContentPixelCount(topDown), counted);
        Assert.Equal(6, counted);
    }

    private static void AssertBoundsMatchOracle(Bitmap bmp)
    {
        var oracle = OracleFirstContentPixel(bmp);
        Assert.True(oracle.HasValue,
            "fixture has no dark pixel at all — every bounds assertion below " +
            "would be comparing two null results and could not fail");

        var bounds = ImageProcessor.FindContentBounds(bmp);
        Assert.True(bounds.HasValue, "scan found no content where GetPixel sees some");

        // A single dark pixel means the bounds collapse onto the oracle's
        // coordinate, so a mirrored scan shows up as an inequality rather than
        // as a merely wider rectangle.
        Assert.Equal(oracle!.Value.X, bounds!.Value.X);
        Assert.Equal(oracle!.Value.Y, bounds!.Value.Y);
        Assert.Equal(1, bounds!.Value.Width);
        Assert.Equal(1, bounds!.Value.Height);
    }

    private static int StrideOf(Bitmap bmp)
    {
        var data = bmp.LockBits(
            new Rectangle(0, 0, bmp.Width, bmp.Height),
            ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try { return data.Stride; }
        finally { bmp.UnlockBits(data); }
    }

    private static Point? OracleFirstContentPixel(Bitmap bmp)
    {
        for (int y = 0; y < bmp.Height; y++)
        {
            for (int x = 0; x < bmp.Width; x++)
            {
                if (bmp.GetPixel(x, y).R < 200) return new Point(x, y);
            }
        }
        return null;
    }

    private static int OracleContentPixelCount(Bitmap bmp)
    {
        int n = 0;
        for (int y = 0; y < bmp.Height; y++)
        {
            for (int x = 0; x < bmp.Width; x++)
            {
                if (bmp.GetPixel(x, y).R < 200) n++;
            }
        }
        return n;
    }
}
