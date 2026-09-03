using SnapCut.Core;
using static SnapCut.Core.Tests.TestImages;

namespace SnapCut.Core.Tests;

public sealed class PixelImageTests
{
    [Fact]
    public void FromBgraRejectsAMismatchedBuffer()
    {
        Assert.Throws<ArgumentException>(
            () => PixelImage.FromBgra(4, 4, new byte[4 * 4 * 4 - 1]));
    }

    [Fact]
    public void CropRowsCopiesTheRequestedBand()
    {
        var source = new PixelImage(8, 6);
        for (var y = 0; y < source.Height; y++)
        {
            FillRect(source, 0, y, source.Width, 1, Rgb((byte)(y * 10), 0, 0));
        }

        var crop = source.CropRows(2, 3);

        Assert.Equal(8, crop.Width);
        Assert.Equal(3, crop.Height);
        for (var y = 0; y < crop.Height; y++)
        {
            Assert.Equal(Rgb((byte)((y + 2) * 10), 0, 0), GetPixel(crop, 3, y));
        }
    }

    [Fact]
    public void BlitRowsToClampsHorizontalShiftAtTheEdges()
    {
        var source = new PixelImage(4, 1);
        SetPixel(source, 0, 0, Rgb(10, 0, 0));
        SetPixel(source, 1, 0, Rgb(20, 0, 0));
        SetPixel(source, 2, 0, Rgb(30, 0, 0));
        SetPixel(source, 3, 0, Rgb(40, 0, 0));
        var destination = new PixelImage(4, 1);

        // destination[x] = source[x - offset]; offset +1 shifts content right
        // and edge-fills the exposed left column with the nearest source pixel.
        source.BlitRowsTo(destination, 0, 1, 0, horizontalOffset: 1);

        Assert.Equal(Rgb(10, 0, 0), GetPixel(destination, 0, 0));
        Assert.Equal(Rgb(10, 0, 0), GetPixel(destination, 1, 0));
        Assert.Equal(Rgb(20, 0, 0), GetPixel(destination, 2, 0));
        Assert.Equal(Rgb(30, 0, 0), GetPixel(destination, 3, 0));
    }

    [Fact]
    public void DownscaleToAveragesSourceBlocks()
    {
        var source = new PixelImage(2, 2);
        SetPixel(source, 0, 0, Rgb(0, 0, 0));
        SetPixel(source, 1, 0, Rgb(0, 0, 0));
        SetPixel(source, 0, 1, Rgb(200, 200, 200));
        SetPixel(source, 1, 1, Rgb(200, 200, 200));

        var scaled = source.DownscaleTo(1, 1);

        Assert.Equal(Rgb(100, 100, 100), GetPixel(scaled, 0, 0));
    }

    [Fact]
    public void DownscaleToSameSizeIsAnExactCopy()
    {
        var source = new PixelImage(3, 3);
        for (var y = 0; y < 3; y++)
        {
            for (var x = 0; x < 3; x++)
            {
                SetPixel(source, x, y, Rgb((byte)(x * 40), (byte)(y * 40), 7));
            }
        }

        var scaled = source.DownscaleTo(3, 3);

        for (var y = 0; y < 3; y++)
        {
            for (var x = 0; x < 3; x++)
            {
                Assert.Equal(GetPixel(source, x, y), GetPixel(scaled, x, y));
            }
        }
    }
}
