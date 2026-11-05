using RapidOcrNet;
using SkiaSharp;

namespace Screenshot.App.Tests;

public sealed class OcrTallCropTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TallCropPixelsAndCharacterCoordinatesUseTheSameDirection(bool counterClockwise)
    {
        using var source = new SKBitmap(8, 32);
        for (var y = 0; y < source.Height; y++)
        for (var x = 0; x < source.Width; x++)
            source.SetPixel(x, y, new SKColor((byte)(x * 20), (byte)(y * 7), 0));
        using var rotated = OcrUtils.GetRotateCropImage(source,
            [new(0, 0), new(8, 0), new(8, 32), new(0, 32)], out var context, counterClockwise);
        Assert.True(context.Rotated90);
        Assert.Equal(counterClockwise, context.CounterClockwise);
        Assert.Equal(32, rotated.Width);
        Assert.Equal(8, rotated.Height);
        var expected = counterClockwise ? source.GetPixel(7, 0) : source.GetPixel(0, 31);
        var actual = rotated.GetPixel(0, 0);
        // The existing cubic warp can round channel values by one at the edge.
        Assert.InRange(Math.Abs(expected.Red - actual.Red), 0, 2);
        Assert.InRange(Math.Abs(expected.Green - actual.Green), 0, 2);
        var line = new TextLine { Chars = ["春", "夏", "秋"], CharScores = [.99f, .99f, .99f],
            CharCols = [2, 5, 8], ColCount = 12, LineTxtLen = 12 };
        var boxes = CalRecBoxes.Build(line, context, false, true)!;
        Assert.Equal(3, boxes.Length);
        var centers = boxes.Select(b => b.BoxPoints.Average(p => p.Y)).ToArray();
        Assert.Equal(counterClockwise, centers[0] < centers[1]);
        Assert.Equal(counterClockwise, centers[1] < centers[2]);
        Assert.All(boxes.SelectMany(b => b.BoxPoints), point => {
            Assert.InRange(point.X, 0, 8); Assert.InRange(point.Y, 0, 32);
        });
    }

    [Fact]
    public void V6PresetCorrectsDirectionWithoutChangingLegacyPreset()
    {
        Assert.True(RapidOcrOptions.PPOCRv6.RotateTallCropsCounterClockwise);
        Assert.False(RapidOcrOptions.Default.RotateTallCropsCounterClockwise);
        Assert.True(RapidOcrOptions.PPOCRv6.PadRecognitionInput);
        Assert.False(RapidOcrOptions.Default.PadRecognitionInput);
    }
}
