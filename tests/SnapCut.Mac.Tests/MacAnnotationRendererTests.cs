using Avalonia;
using Avalonia.Media;
using SnapCut.Core;
using SnapCut.Mac.Editor;

namespace SnapCut.Mac.Tests;

public sealed class MacAnnotationRendererTests
{
    [Fact]
    public void RendersShapeIntoFinalPixelImageWithoutMutatingSource()
    {
        var source = new PixelImage(80, 60);
        source.Fill(255, 255, 255);
        var annotation = new MacShapeAnnotation(
            MacEditorTool.Rectangle,
            new Point(10, 10),
            new Point(50, 40),
            Colors.Red,
            4);

        var result = MacAnnotationRenderer.Apply(
            source,
            new Rect(0, 0, 80, 60),
            [annotation]);

        Assert.NotSame(source, result);
        Assert.All(source.Pixels, value => Assert.Equal(255, value));
        Assert.True(CountRedPixels(result) > 150);
    }

    [Fact]
    public void MosaicChangesPixelsAlongStroke()
    {
        var source = CreateCheckerboard(96, 64);
        var annotation = new MacStrokeAnnotation(
            MacEditorTool.Mosaic,
            [new Point(12, 32), new Point(84, 32)],
            Colors.Transparent,
            16);

        var result = MacAnnotationRenderer.Apply(
            source,
            new Rect(0, 0, 96, 64),
            [annotation]);

        Assert.True(result.Pixels
            .Zip(source.Pixels, (actual, original) => actual != original)
            .Count(changed => changed) > 500);
    }

    [Fact]
    public void FilledAndHollowArrowsRenderDifferently()
    {
        var source = new PixelImage(120, 70);
        source.Fill(255, 255, 255);
        var filled = new MacShapeAnnotation(
            MacEditorTool.Arrow,
            new Point(12, 35),
            new Point(108, 35),
            Colors.Red,
            5,
            MacArrowStyle.Filled);
        var hollow = filled with { ArrowStyle = MacArrowStyle.Hollow };

        var filledImage = MacAnnotationRenderer.Apply(
            source,
            new Rect(0, 0, 120, 70),
            [filled]);
        var hollowImage = MacAnnotationRenderer.Apply(
            source,
            new Rect(0, 0, 120, 70),
            [hollow]);

        Assert.NotEqual(filledImage.Pixels, hollowImage.Pixels);
        Assert.True(CountRedPixels(filledImage) > CountRedPixels(hollowImage));
    }

    private static int CountRedPixels(PixelImage image)
    {
        var count = 0;
        for (var offset = 0; offset < image.Pixels.Length; offset += 4)
        {
            if (image.Pixels[offset + 2] > 200 &&
                image.Pixels[offset + 1] < 100 &&
                image.Pixels[offset] < 100)
            {
                count++;
            }
        }

        return count;
    }

    private static PixelImage CreateCheckerboard(int width, int height)
    {
        var image = new PixelImage(width, height);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var value = (byte)(((x + y) & 1) == 0 ? 0 : 255);
                image.FillRect(x, y, 1, 1, value, value, value);
            }
        }

        return image;
    }
}
