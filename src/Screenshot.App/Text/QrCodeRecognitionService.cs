using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Screenshot.App.Capture;
using ZXing;
using ZXing.Common;

namespace Screenshot.App.Text;

public sealed record QrCodeScanResult(IReadOnlyList<ContentRecognitionResult> Codes,
    RecognizedContentRegion? WeChatCodeRegion = null)
{
    public bool HasDetectedContent => Codes.Count > 0 || WeChatCodeRegion is not null;
    public const string WeChatExplanation =
        "疑似微信小程序码，需使用微信识别；本机无法直接提取链接。可复制图片后交给微信识别。";
}

public static class QrCodeRecognitionService
{
    public static ContentRecognitionResult Recognize(CapturedImage image)
    {
        var scan = Scan(image);
        return scan.Codes.Count > 0 ? scan.Codes[0] : ContentRecognitionResult.Failure("二维码",
            scan.WeChatCodeRegion is not null ? QrCodeScanResult.WeChatExplanation :
            "未识别到可解码的二维码。请完整框住清晰的码图；微信小程序码等专有码需使用对应应用识别。");
    }

    public static QrCodeScanResult Scan(CapturedImage image, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        cancellationToken.ThrowIfCancellationRequested();
        var results = new List<ContentRecognitionResult>();
        try
        {
            // Bound work for large desktop captures; normal images retain native pixels.
            var ratio = Math.Min(1d, 2400d / Math.Max(image.Bitmap.Width, image.Bitmap.Height));
            using var bitmap = new Bitmap(Math.Max(1, (int)(image.Bitmap.Width * ratio)),
                Math.Max(1, (int)(image.Bitmap.Height * ratio)), PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.Clear(Color.White);
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.DrawImage(image.Bitmap, new Rectangle(0, 0, bitmap.Width, bitmap.Height));
            }
            var gray = ReadGray(bitmap);
            var watch = Stopwatch.StartNew();
            Decode(gray, bitmap.Width, bitmap.Height, 1, 0, invert: false);
            Decode(gray, bitmap.Width, bitmap.Height, 1, 0, invert: true);
            if (results.Count == 0 && watch.ElapsedMilliseconds < 800)
            {
                // Restore the quiet zone and enlarge small modules without smoothing.
                var scale = Math.Max(bitmap.Width, bitmap.Height) < 600 ? 2 : 1;
                const int padding = 20;
                var width = bitmap.Width * scale + padding * 2;
                var height = bitmap.Height * scale + padding * 2;
                var padded = new byte[width * height];
                Array.Fill(padded, (byte)255);
                var low = gray.Min();
                var high = gray.Max();
                for (var y = 0; y < bitmap.Height * scale; y++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    for (var x = 0; x < bitmap.Width * scale; x++)
                    {
                        var value = gray[y / scale * bitmap.Width + x / scale];
                        padded[(y + padding) * width + x + padding] = high > low
                            ? (byte)((value - low) * 255 / (high - low)) : value;
                    }
                }
                Decode(padded, width, height, scale, padding, invert: false);
                Decode(padded, width, height, scale, padding, invert: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            var miniCode = MiniProgramCodeDetector.Find(gray, bitmap.Width, bitmap.Height, cancellationToken);
            if (miniCode is not null)
                miniCode = new RecognizedContentRegion(miniCode.X / ratio, miniCode.Y / ratio,
                    miniCode.Width / ratio, miniCode.Height / ratio);
            return new QrCodeScanResult(results.OrderBy(result => result.Region!.Y)
                .ThenBy(result => result.Region!.X).ToArray(), miniCode);

            void Decode(byte[] input, int width, int height, int scale, int padding, bool invert)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (watch.ElapsedMilliseconds > 1200 || results.Count >= 12) return;
                LuminanceSource source = new RGBLuminanceSource(input, width, height,
                    RGBLuminanceSource.BitmapFormat.Gray8);
                if (invert) source = source.invert();
                var reader = new BarcodeReaderGeneric
                {
                    AutoRotate = true,
                    Options = new DecodingOptions { TryHarder = true, PossibleFormats = [BarcodeFormat.QR_CODE] },
                };
                var decoded = reader.DecodeMultiple(source) ?? [];
                if (decoded.Length == 0 && reader.Decode(source) is { } single) decoded = [single];
                foreach (var code in decoded)
                {
                    if (string.IsNullOrWhiteSpace(code.Text) || results.Count >= 12) continue;
                    var points = code.ResultPoints ?? [];
                    var x = points.Length > 0 ? points.Min(point => point.X) : padding;
                    var y = points.Length > 0 ? points.Min(point => point.Y) : padding;
                    var right = points.Length > 0 ? points.Max(point => point.X) : width - padding;
                    var bottom = points.Length > 0 ? points.Max(point => point.Y) : height - padding;
                    var region = new RecognizedContentRegion(
                        Math.Clamp((x - padding) / scale / ratio, 0, image.Bitmap.Width - 1),
                        Math.Clamp((y - padding) / scale / ratio, 0, image.Bitmap.Height - 1),
                        Math.Max(1, (right - x) / scale / ratio), Math.Max(1, (bottom - y) / scale / ratio));
                    if (results.Any(previous => previous.Content == code.Text &&
                        Math.Abs(previous.Region!.CenterX - region.CenterX) < region.Width * .35 &&
                        Math.Abs(previous.Region.CenterY - region.CenterY) < region.Height * .35)) continue;
                    // Preserve payloads: text and Wi-Fi settings are not webpage URLs.
                    results.Add(new ContentRecognitionResult(true, "二维码", code.Text) { Region = region });
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { return new QrCodeScanResult(results); }
    }

    private static byte[] ReadGray(Bitmap bitmap)
    {
        var bounds = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var data = bitmap.LockBits(bounds, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var row = new byte[bitmap.Width * 4];
            var gray = new byte[bitmap.Width * bitmap.Height];
            for (var y = 0; y < bitmap.Height; y++)
            {
                Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, row.Length);
                for (var x = 0; x < bitmap.Width; x++)
                    gray[y * bitmap.Width + x] = (byte)((row[x * 4 + 2] * 77 +
                        row[x * 4 + 1] * 150 + row[x * 4] * 29) >> 8);
            }
            return gray;
        }
        finally { bitmap.UnlockBits(data); }
    }
}
