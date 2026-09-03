using System.Runtime.InteropServices.WindowsRuntime;

namespace WctControls;

internal sealed class ImageCropperPage : Component
{
    public override Element Render()
    {
        var (shape, setShape) = UseState(0);
        var image = UseMemo(SampleBitmap, System.Array.Empty<object>());

        return Gallery.Page(
            "ImageCropper",
            "Crops/zooms/rotates an image. Both Source (a sample bitmap) and CropShape are bound declaratively as element props.",
            VStack(12,
                Segmented(
                    selectedIndex: shape,
                    onSelectedIndexChanged: setShape,
                    items: new object[] { "Rectangular", "Circular" }),
                ImageCropper(
                    source: image,
                    cropShape: shape == 0
                        ? CommunityToolkit.WinUI.Controls.CropShape.Rectangular
                        : CommunityToolkit.WinUI.Controls.CropShape.Circular)
                    .Size(460, 320)));
    }

    private static Microsoft.UI.Xaml.Media.Imaging.WriteableBitmap SampleBitmap()
    {
        const int w = 480, h = 360;
        var wb = new Microsoft.UI.Xaml.Media.Imaging.WriteableBitmap(w, h);
        var px = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                px[i + 0] = (byte)(220 - y * 180 / h);
                px[i + 1] = (byte)(x * 200 / w);
                px[i + 2] = (byte)(60 + y * 160 / h);
                px[i + 3] = 255;
            }
        using (var s = wb.PixelBuffer.AsStream())
            s.Write(px, 0, px.Length);
        return wb;
    }
}
