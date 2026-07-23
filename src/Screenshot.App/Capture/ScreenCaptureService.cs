using System.Drawing;
using System.Drawing.Imaging;

namespace Screenshot.App.Capture;

public static class ScreenCaptureService
{
    public static CapturedImage Capture(ScreenRegion region)
    {
        if (region.IsEmpty)
        {
            throw new ArgumentException("截图区域不能为空。", nameof(region));
        }

        var bitmap = new Bitmap(region.Width, region.Height, PixelFormat.Format32bppPArgb);

        try
        {
            using var graphics = Graphics.FromImage(bitmap);
            graphics.CopyFromScreen(region.X, region.Y, 0, 0, bitmap.Size);
            return new CapturedImage(bitmap, region);
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }
}
