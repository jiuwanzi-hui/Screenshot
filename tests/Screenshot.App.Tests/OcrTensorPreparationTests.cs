using RapidOcrNet;
using SkiaSharp;

namespace Screenshot.App.Tests;

public sealed class OcrTensorPreparationTests
{
    [Theory]
    [InlineData(SKColorType.Bgra8888)]
    [InlineData(SKColorType.Gray8)]
    public void PlanarNormalizationPreservesChannelsAndIgnoresStridePadding(SKColorType color)
    {
        const int width = 7, height = 5;
        var info = new SKImageInfo(width, height, color, SKAlphaType.Opaque);
        using var bitmap = new SKBitmap(info, info.RowBytes + 16);
        var source = bitmap.GetPixelSpan();
        for (var i = 0; i < source.Length; i++) source[i] = (byte)(i * 31);
        float[] mean = [123.1f, 127.5f, 101.7f];
        float[] scale = [.012f, 1 / 127.5f, .019f];
        var tensor = OcrUtils.SubtractMeanNormalize(bitmap, mean, scale);
        Assert.Equal(new[] { 1, 3, height, width }, tensor.Dimensions.ToArray());
        for (var channel = 0; channel < 3; channel++)
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var value = source[y * bitmap.RowBytes + x * bitmap.BytesPerPixel +
                (color == SKColorType.Gray8 ? 0 : channel)];
            Assert.Equal((value - mean[channel]) * scale[channel], tensor[0, channel, y, x]);
        }
    }

    [Fact]
    public void PreparationAllocatesTensorWithoutPerPixelCoordinateArrays()
    {
        using var image = new SKBitmap(1000, 700, SKColorType.Bgra8888, SKAlphaType.Opaque);
        float[] mean = [127.5f, 127.5f, 127.5f], scale = [1 / 127.5f, 1 / 127.5f, 1 / 127.5f];
        _ = OcrUtils.SubtractMeanNormalize(image, mean, scale);
        var before = GC.GetAllocatedBytesForCurrentThread();
        var result = OcrUtils.SubtractMeanNormalize(image, mean, scale);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.InRange(allocated, 1000L * 700 * 3 * sizeof(float), 1000L * 700 * 3 * sizeof(float) + 4096);
        GC.KeepAlive(result);
    }
}
