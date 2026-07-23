using System.Drawing;
using System.Windows;
using Screenshot.App.Editor;

namespace Screenshot.App.Tests;

public sealed class MosaicRendererTests
{
    [Fact]
    public void CreatesAMosaicBitmapForTheRequestedRegion()
    {
        using var source = new Bitmap(8, 8);
        using (var graphics = Graphics.FromImage(source))
        {
            graphics.Clear(Color.Red);
        }

        var mosaic = MosaicRenderer.Create(source, new Int32Rect(1, 1, 4, 4), blockSize: 2);

        Assert.Equal(4, mosaic.PixelWidth);
        Assert.Equal(4, mosaic.PixelHeight);
        Assert.True(mosaic.IsFrozen);
    }
}
