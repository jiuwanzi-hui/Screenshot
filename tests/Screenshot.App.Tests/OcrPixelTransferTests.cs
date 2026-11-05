using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Screenshot.App.Text;

namespace Screenshot.App.Tests;

public sealed class OcrPixelTransferTests
{
    [Theory]
    [InlineData(PixelFormat.Format24bppRgb)]
    [InlineData(PixelFormat.Format32bppArgb)]
    [InlineData(PixelFormat.Format32bppPArgb)]
    [InlineData(PixelFormat.Format32bppRgb)]
    public void CopiesOddWidthRowsAndChannelsWithoutCompression(PixelFormat format)
    {
        using var bitmap = new Bitmap(3, 2, format);
        bitmap.SetPixel(0, 0, Color.FromArgb(255, 230, 50, 10));
        bitmap.SetPixel(2, 1, Color.FromArgb(127, 40, 100, 180));
        using var result = HighQualityOcrService.CopyBitmapPixels(bitmap);
        for (var y = 0; y < 2; y++)
        for (var x = 0; x < 3; x++)
        {
            var expected = bitmap.GetPixel(x, y);
            var actual = result.GetPixel(x, y);
            Assert.InRange(Math.Abs(expected.R - actual.Red), 0, 1);
            Assert.InRange(Math.Abs(expected.G - actual.Green), 0, 1);
            Assert.InRange(Math.Abs(expected.B - actual.Blue), 0, 1);
            Assert.Equal(expected.A, actual.Alpha);
        }
    }

    [Fact]
    public void CopiesNegativeStrideWithoutFlippingRows()
    {
        var memory = Marshal.AllocHGlobal(16);
        try
        {
            using var bitmap = new Bitmap(2, 2, -8, PixelFormat.Format32bppArgb, IntPtr.Add(memory, 8));
            bitmap.SetPixel(0, 0, Color.Red);
            bitmap.SetPixel(0, 1, Color.Blue);
            using var result = HighQualityOcrService.CopyBitmapPixels(bitmap);
            Assert.Equal(255, result.GetPixel(0, 0).Red);
            Assert.Equal(255, result.GetPixel(0, 1).Blue);
        }
        finally { Marshal.FreeHGlobal(memory); }
    }
}
