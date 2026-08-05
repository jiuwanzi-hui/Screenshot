using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Screenshot.App.Capture;
using ZXing;
using ZXing.Common;

namespace Screenshot.App.Text;

public static class QrCodeRecognitionService
{
    public static ContentRecognitionResult Recognize(CapturedImage image)
    {
        ArgumentNullException.ThrowIfNull(image);

        try
        {
            using var bitmap = image.Bitmap.Clone(
                new Rectangle(0, 0, image.Bitmap.Width, image.Bitmap.Height),
                PixelFormat.Format32bppArgb);
            var data = bitmap.LockBits(
                new Rectangle(0, 0, bitmap.Width, bitmap.Height),
                ImageLockMode.ReadOnly,
                PixelFormat.Format32bppArgb);
            try
            {
                var pixels = new byte[Math.Abs(data.Stride) * data.Height];
                Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
                var source = new RGBLuminanceSource(
                    pixels,
                    bitmap.Width,
                    bitmap.Height,
                    RGBLuminanceSource.BitmapFormat.BGRA32);
                var reader = new BarcodeReaderGeneric
                {
                    AutoRotate = true,
                    Options = new DecodingOptions
                    {
                        TryHarder = true,
                        PossibleFormats = [BarcodeFormat.QR_CODE],
                    },
                };
                var result = reader.Decode(source);
                if (result is null || string.IsNullOrWhiteSpace(result.Text))
                {
                    return ContentRecognitionResult.Failure(
                        "二维码",
                        "选区内没有识别到二维码，请尽量完整框住二维码后重试。");
                }

                var points = result.ResultPoints ?? [];
                var region = points.Length == 0
                    ? new RecognizedContentRegion(
                        0,
                        0,
                        bitmap.Width,
                        bitmap.Height)
                    : new RecognizedContentRegion(
                        points.Min(point => point.X),
                        points.Min(point => point.Y),
                        Math.Max(1, points.Max(point => point.X) -
                            points.Min(point => point.X)),
                        Math.Max(1, points.Max(point => point.Y) -
                            points.Min(point => point.Y)));
                return new ContentRecognitionResult(
                    true,
                    "二维码",
                    result.Text.Trim())
                {
                    Region = region,
                };
            }
            finally
            {
                bitmap.UnlockBits(data);
            }
        }
        catch (Exception)
        {
            return ContentRecognitionResult.Failure(
                "二维码",
                "二维码识别失败，请确认图片清晰且二维码没有被遮挡。");
        }
    }
}
