using System.Drawing;
using System.Drawing.Imaging;
using Microsoft.UI.Reactor.Cli.Docs;
using Xunit;

namespace Microsoft.UI.Reactor.Cli.Docs.Tests;

/// <summary>
/// Issue #989: the capture phase used to overwrite a committed screenshot with
/// whatever the doc app returned, including the solid-white frame a window that
/// never painted produces. These tests cover
/// <see cref="ScreenshotCapture.ProcessAndWrite"/> — the exact seam where the
/// decision to touch the filesystem is made.
/// </summary>
/// <remarks>
/// This is the strongest headless proof available for the write guard: the full
/// <c>CaptureAsync</c> loop needs a live WinUI desktop and a doc-app subprocess,
/// so exercising it in a unit test is not possible. The end-to-end guarantee is
/// covered instead by the <c>docs-build</c> CI job, which runs a real compile
/// and then <c>git diff --exit-code -- docs/guide/images</c>.
/// </remarks>
public class ScreenshotWriteGuardTests
{
    [Fact]
    public void Blank_frame_does_not_overwrite_an_existing_screenshot()
    {
        using var dir = new TempDir();
        var path = dir.Path("widget.png");
        var committed = MakePng(120, 90, painted: true);
        global::System.IO.File.WriteAllBytes(path, committed);

        Assert.Throws<BlankFrameException>(
            () => ScreenshotCapture.ProcessAndWrite(MakePng(400, 300, painted: false), path, Config()));

        Assert.Equal(committed, global::System.IO.File.ReadAllBytes(path));
    }

    [Fact]
    public void Blank_frame_does_not_create_a_new_screenshot()
    {
        using var dir = new TempDir();
        var path = dir.Path("widget.png");

        Assert.Throws<BlankFrameException>(
            () => ScreenshotCapture.ProcessAndWrite(MakePng(400, 300, painted: false), path, Config()));

        Assert.False(global::System.IO.File.Exists(path),
            "a blank capture must not leave a stub behind for the next reader to trust");
    }

    /// <summary>
    /// Control. Without it the two tests above would pass against a
    /// <c>ProcessAndWrite</c> that never wrote anything at all, which would be a
    /// worse bug than the one being fixed. A real frame must still replace the
    /// committed bytes.
    /// </summary>
    [Fact]
    public void Painted_frame_overwrites_the_existing_screenshot()
    {
        using var dir = new TempDir();
        var path = dir.Path("widget.png");
        var committed = MakePng(120, 90, painted: true);
        global::System.IO.File.WriteAllBytes(path, committed);

        ScreenshotCapture.ProcessAndWrite(MakePng(400, 300, painted: true), path, Config());

        Assert.NotEqual(committed, global::System.IO.File.ReadAllBytes(path));
    }

    [Fact]
    public void Blank_frame_is_refused_for_catalog_thumbs_too()
    {
        using var dir = new TempDir();
        var path = dir.Path("widget-thumb.png");
        var committed = MakePng(120, 90, painted: true);
        global::System.IO.File.WriteAllBytes(path, committed);

        var thumb = Config();
        thumb.Kind = "catalog-thumb";

        Assert.Throws<BlankFrameException>(
            () => ScreenshotCapture.ProcessAndWrite(MakePng(400, 300, painted: false), path, thumb));

        Assert.Equal(committed, global::System.IO.File.ReadAllBytes(path));
    }

    /// <summary>
    /// The unpainted-composition-surface case, at the write seam. Before the
    /// guard blended against white this frame was the one that got through:
    /// every channel is zero, so an RGB-only threshold read it as content, and
    /// it was written out as the solid-white stub the guard exists to stop.
    /// </summary>
    [Fact]
    public void Transparent_frame_does_not_overwrite_an_existing_screenshot()
    {
        using var dir = new TempDir();
        var path = dir.Path("widget.png");
        var committed = MakePng(120, 90, painted: true);
        global::System.IO.File.WriteAllBytes(path, committed);

        Assert.Throws<BlankFrameException>(
            () => ScreenshotCapture.ProcessAndWrite(MakeTransparentPng(400, 300), path, Config()));

        Assert.Equal(committed, global::System.IO.File.ReadAllBytes(path));
    }

    private static ScreenshotConfig Config() =>
        new() { Id = "widget", Format = "png", Crop = "content" };

    private static byte[] MakeTransparentPng(int w, int h)
    {
        using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.CompositingMode = global::System.Drawing.Drawing2D.CompositingMode.SourceCopy;
            g.Clear(Color.FromArgb(0, 0, 0, 0));
        }

        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }

    private static byte[] MakePng(int w, int h, bool painted)
    {
        using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.White);
            if (painted)
            {
                using var ink = new SolidBrush(Color.FromArgb(24, 24, 24));
                g.FillRectangle(ink, w / 4, h / 4, w / 2, h / 2);
            }
        }

        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }

    private sealed class TempDir : global::System.IDisposable
    {
        private readonly string _root = global::System.IO.Path.Join(
            global::System.IO.Path.GetTempPath(),
            "reactor-shot-guard-" + global::System.Guid.NewGuid().ToString("N"));

        public TempDir() => global::System.IO.Directory.CreateDirectory(_root);

        public string Path(string name) => global::System.IO.Path.Join(_root, name);

        public void Dispose()
        {
            try { global::System.IO.Directory.Delete(_root, recursive: true); }
            catch (global::System.IO.IOException) { /* best effort */ }
        }
    }
}
