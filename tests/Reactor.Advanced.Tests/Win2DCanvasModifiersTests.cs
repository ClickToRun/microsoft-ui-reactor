using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Reactor.Advanced.Win2D;
using Windows.Foundation;
using Windows.UI;
using Xunit;
using static Microsoft.UI.Reactor.Advanced.Factories;

namespace Microsoft.UI.Reactor.Advanced.Tests;

public sealed class Win2DCanvasModifiersTests
{
    [Fact]
    public void CanvasClearColor_ReturnsNewRecordAndLeavesOriginalUnchanged()
    {
        var original = Win2DCanvas(static (CanvasDrawingSession _, CanvasDrawEventArgs _) => { });
        var updated = original.ClearColor(new Color { A = 255, R = 255, G = 0, B = 0 });

        Assert.NotSame(original, updated);
        Assert.Equal(new Color { A = 0, R = 0, G = 0, B = 0 }, original.ClearColor);
        Assert.Equal(new Color { A = 255, R = 255, G = 0, B = 0 }, updated.ClearColor);
    }

    [Fact]
    public void AnimatedModifiers_ReturnNewRecordsAndAreChainable()
    {
        var original = Win2DAnimatedCanvas(
            static (CanvasAnimatedUpdateEventArgs _, object? _) => { },
            static (CanvasDrawingSession _, CanvasAnimatedDrawEventArgs _, object? _) => { });

        var updated = original
            .ClearColor(new Color { A = 255, R = 0, G = 0, B = 255 })
            .Paused()
            .TargetFps(30);

        Assert.NotSame(original, updated);
        Assert.Equal(new Color { A = 0, R = 0, G = 0, B = 0 }, original.ClearColor);
        Assert.False(original.IsPaused);
        Assert.Equal(TimeSpan.FromTicks(166_667), original.TargetElapsedTime);
        Assert.Equal(new Color { A = 255, R = 0, G = 0, B = 255 }, updated.ClearColor);
        Assert.True(updated.IsPaused);
        Assert.Equal(TimeSpan.FromSeconds(1.0 / 30), updated.TargetElapsedTime);
    }

    [Fact]
    public void SetModifiers_AppendSettersWithoutMutatingOriginal()
    {
        var canvas = Win2DCanvas(static (CanvasDrawingSession _, CanvasDrawEventArgs _) => { });
        var animated = Win2DAnimatedCanvas(
            static (CanvasAnimatedUpdateEventArgs _, object? _) => { },
            static (CanvasDrawingSession _, CanvasAnimatedDrawEventArgs _, object? _) => { });
        var virtualCanvas = Win2DVirtualCanvas(static (CanvasDrawingSession _, Rect _) => { }, new Size(10, 10));

        var canvasUpdated = canvas.Set(static _ => { }).Set(static _ => { });
        var animatedUpdated = animated.Set(static _ => { });
        var virtualUpdated = virtualCanvas.Set(static _ => { });

        Assert.Empty(canvas.Setters);
        Assert.Empty(animated.Setters);
        Assert.Empty(virtualCanvas.Setters);
        Assert.Equal(2, canvasUpdated.Setters.Length);
        Assert.Single(animatedUpdated.Setters);
        Assert.Single(virtualUpdated.Setters);
    }

    [Fact]
    public void TargetFps_RejectsNonPositiveValues()
    {
        var original = Win2DAnimatedCanvas(
            static (CanvasAnimatedUpdateEventArgs _, object? _) => { },
            static (CanvasDrawingSession _, CanvasAnimatedDrawEventArgs _, object? _) => { });

        Assert.Throws<ArgumentOutOfRangeException>(() => original.TargetFps(0));
    }

    [Fact]
    public void UseSharedDevice_OptsInForAllCanvasKindsWithoutMutatingOriginal()
    {
        var canvas = Win2DCanvas(static (CanvasDrawingSession _, CanvasDrawEventArgs _) => { });
        var animated = Win2DAnimatedCanvas(
            static (CanvasAnimatedUpdateEventArgs _, object? _) => { },
            static (CanvasDrawingSession _, CanvasAnimatedDrawEventArgs _, object? _) => { });
        var virtualCanvas = Win2DVirtualCanvas(static (CanvasDrawingSession _, Rect _) => { }, new Size(10, 10));

        Assert.False(canvas.UseSharedDevice);
        Assert.False(animated.UseSharedDevice);
        Assert.False(virtualCanvas.UseSharedDevice);

        var canvasUpdated = canvas.UseSharedDevice();
        var animatedUpdated = animated.UseSharedDevice();
        var virtualUpdated = virtualCanvas.UseSharedDevice();

        Assert.NotSame(canvas, canvasUpdated);
        Assert.False(canvas.UseSharedDevice);
        Assert.False(animated.UseSharedDevice);
        Assert.False(virtualCanvas.UseSharedDevice);
        Assert.True(canvasUpdated.UseSharedDevice);
        Assert.True(animatedUpdated.UseSharedDevice);
        Assert.True(virtualUpdated.UseSharedDevice);

        Assert.False(animatedUpdated.UseSharedDevice(false).UseSharedDevice);
    }
}

