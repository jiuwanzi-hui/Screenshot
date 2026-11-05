using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Screenshot.App.Capture;
using Screenshot.App.Text;
using ZXing;
using ZXing.Common;

namespace Screenshot.App.Tests;

public sealed class QrCodeCompatibilityTests
{
    private const string Link = "https://example.com/page?item=42";

    [Theory]
    [InlineData(false, 0, 90)]
    [InlineData(true, 0, 110)]
    [InlineData(false, 1, 200)]
    [InlineData(true, 3, 200)]
    public void ReadsTightSmallAndRotatedCodes(bool inverted, int rotation, int size)
    {
        using var image = new CapturedImage(CreateCode(Link, size, 0, inverted));
        for (var i = 0; i < rotation; i++) image.Bitmap.RotateFlip(RotateFlipType.Rotate90FlipNone);
        var scan = QrCodeRecognitionService.Scan(image);
        Assert.Equal(Link, Assert.Single(scan.Codes).Content);
        Assert.Null(scan.WeChatCodeRegion);
        Assert.InRange(scan.Codes[0].Region!.CenterX, size * .2, size * .8);
    }

    [Fact]
    public void ReportsEveryCodeIncludingInvertedAndRepeatedLinksAtDifferentLocations()
    {
        using var normal = CreateCode(Link, 160, 4, false);
        using var inverse = CreateCode("https://example.org/second", 160, 4, true);
        using var image = new CapturedImage(new Bitmap(570, 210));
        using (var graphics = Graphics.FromImage(image.Bitmap))
        {
            graphics.Clear(Color.White);
            graphics.DrawImageUnscaled(normal, 10, 15);
            graphics.DrawImageUnscaled(inverse, 205, 25);
            graphics.DrawImageUnscaled(normal, 400, 35);
        }
        var scan = QrCodeRecognitionService.Scan(image);
        Assert.Equal(3, scan.Codes.Count);
        Assert.Equal(2, scan.Codes.Count(code => code.Content == Link));
        Assert.Contains(scan.Codes, code => code.Content == "https://example.org/second");
        Assert.InRange(scan.Codes.Max(code => code.Region!.CenterX), 450, 530);
    }

    [Fact]
    public void KeepsNonLinkPayloadAndTransparentQuietZone()
    {
        const string payload = "WIFI:T:WPA;S:Test;P:example;;";
        using var image = new CapturedImage(CreateCode(payload, 160, 4, false, transparentPaper: true));
        Assert.Equal(payload, QrCodeRecognitionService.Recognize(image).Content);
    }

    [Fact]
    public void BlankAndOrdinaryShapesDoNotInventLinksOrMiniProgramResults()
    {
        using var image = new CapturedImage(new Bitmap(300, 300));
        using (var graphics = Graphics.FromImage(image.Bitmap))
        {
            graphics.Clear(Color.White);
            using var pen = new Pen(Color.Black, 3);
            graphics.DrawEllipse(pen, 20, 20, 110, 110);
            graphics.DrawRectangle(pen, 150, 150, 90, 90);
        }
        Assert.False(QrCodeRecognitionService.Scan(image).HasDetectedContent);
        Assert.False(QrCodeRecognitionService.Recognize(image).IsSuccess);
    }

    [Theory]
    [InlineData(1, 0, false, 0)]
    [InlineData(1, 0, false, 100)]
    [InlineData(.75, 1, false, 50)]
    [InlineData(1.5, 2, false, 50)]
    [InlineData(1, 3, false, 100)]
    [InlineData(1, 0, true, 100)]
    public void ReportedTextDoesNotBecomeACodeAtDifferentSizesAndOrientations(
        double scale, int rotation, bool inverted, int padding)
    {
        // Only the text is retained from the report, with all surrounding UI
        // and customer paths excluded. Padding also covers fully in-bounds
        // false candidates, independently of the clipped-circle safeguard.
        using var stream = typeof(QrCodeCompatibilityTests).Assembly.GetManifestResourceStream(
            "Screenshot.App.Tests.Fixtures.mini-code-false-positive-text.png")!;
        using var sample = new Bitmap(stream);
        using var image = new CapturedImage(new Bitmap((int)(sample.Width * scale) + padding * 2,
            (int)(sample.Height * scale) + padding * 2));
        using (var graphics = Graphics.FromImage(image.Bitmap))
        {
            graphics.Clear(Color.White);
            graphics.DrawImage(sample, padding, padding, (int)(sample.Width * scale), (int)(sample.Height * scale));
        }
        if (inverted)
            for (var y = 0; y < image.Bitmap.Height; y++)
                for (var x = 0; x < image.Bitmap.Width; x++)
                {
                    var color = image.Bitmap.GetPixel(x, y);
                    image.Bitmap.SetPixel(x, y, Color.FromArgb(255 - color.R, 255 - color.G, 255 - color.B));
                }
        for (var index = 0; index < rotation; index++) image.Bitmap.RotateFlip(RotateFlipType.Rotate90FlipNone);
        Assert.False(QrCodeRecognitionService.Scan(image).HasDetectedContent);
    }

    [Theory]
    [InlineData(16, false)]
    [InlineData(24, false)]
    [InlineData(32, true)]
    public void OrdinaryMultilingualTextDoesNotProduceAMiniProgramHint(int fontSize, bool dark)
    {
        using var image = new CapturedImage(new Bitmap(600, 340));
        using (var graphics = Graphics.FromImage(image.Bitmap))
        using (var font = new Font("Microsoft YaHei UI", fontSize, FontStyle.Bold, GraphicsUnit.Pixel))
        {
            graphics.Clear(dark ? Color.FromArgb(30, 30, 30) : Color.White);
            graphics.DrawString("Read every page, search more files.\n这里是普通文字，识别、翻译与设置。\n0123456789 abcdefghijklmnopqrstuvwxyz\n日本語の文字と English paragraphs.",
                font, dark ? Brushes.White : Brushes.Black, new RectangleF(20, 20, 560, 300));
        }
        Assert.False(QrCodeRecognitionService.Scan(image).HasDetectedContent);
    }

    [Fact]
    public void CancellationStopsBeforeDecoding()
    {
        using var image = new CapturedImage(new Bitmap(50, 50));
        Assert.Throws<OperationCanceledException>(() =>
            QrCodeRecognitionService.Scan(image, new CancellationToken(true)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RadialCodeProducesExternalScannerHintWithoutAFabricatedLink(bool rotate)
    {
        using var image = new CapturedImage(CreateRadialPattern());
        if (rotate) image.Bitmap.RotateFlip(RotateFlipType.Rotate90FlipNone);
        var scan = QrCodeRecognitionService.Scan(image);
        Assert.Empty(scan.Codes);
        Assert.NotNull(scan.WeChatCodeRegion);
        var result = QrCodeRecognitionService.Recognize(image);
        Assert.False(result.IsSuccess);
        Assert.Empty(result.Content);
        Assert.Contains("微信", result.ErrorMessage);
    }

    [Theory]
    [InlineData(.75)]
    [InlineData(1.5)]
    [InlineData(2)]
    public void ScaledRadialCodesRetainTheExternalScannerHint(double scale)
    {
        using var sample = CreateRadialPattern();
        using var image = new CapturedImage(new Bitmap((int)(320 * scale), (int)(320 * scale)));
        using (var graphics = Graphics.FromImage(image.Bitmap))
        {
            graphics.Clear(Color.White);
            graphics.DrawImage(sample, 0, 0, image.Bitmap.Width, image.Bitmap.Height);
        }
        var result = QrCodeRecognitionService.Scan(image);
        Assert.Empty(result.Codes);
        Assert.NotNull(result.WeChatCodeRegion);
    }

    private static Bitmap CreateCode(string content, int size, int margin, bool inverted, bool transparentPaper = false)
    {
        var writer = new BarcodeWriterPixelData { Format = BarcodeFormat.QR_CODE,
            Options = new EncodingOptions { Width = size, Height = size, Margin = margin } };
        var data = writer.Write(content);
        for (var index = 0; index < data.Pixels.Length; index += 4)
        {
            var white = data.Pixels[index] > 128;
            if (inverted)
                for (var channel = 0; channel < 3; channel++) data.Pixels[index + channel] = white ? (byte)0 : (byte)255;
            if (transparentPaper && white) data.Pixels[index + 3] = 0;
        }
        var bitmap = new Bitmap(data.Width, data.Height, PixelFormat.Format32bppArgb);
        var locked = bitmap.LockBits(new Rectangle(0, 0, data.Width, data.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try { Marshal.Copy(data.Pixels, 0, locked.Scan0, data.Pixels.Length); }
        finally { bitmap.UnlockBits(locked); }
        return bitmap;
    }

    private static Bitmap CreateRadialPattern()
    {
        var bitmap = new Bitmap(320, 320);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.White);
        for (var i = 0; i < 60; i++)
        {
            var angle = i * Math.Tau / 60;
            using var pen = new Pen(Color.Black, 3);
            graphics.DrawLine(pen, (float)(160 + Math.Cos(angle) * 68), (float)(160 + Math.Sin(angle) * 68),
                (float)(160 + Math.Cos(angle) * 95), (float)(160 + Math.Sin(angle) * 95));
        }
        foreach (var point in new[] { new Point(110, 110), new Point(210, 110), new Point(110, 210) })
        {
            graphics.FillEllipse(Brushes.White, point.X - 12, point.Y - 12, 24, 24);
            graphics.FillEllipse(Brushes.Black, point.X - 8, point.Y - 8, 16, 16);
            graphics.FillEllipse(Brushes.White, point.X - 5, point.Y - 5, 10, 10);
            graphics.FillEllipse(Brushes.Black, point.X - 2, point.Y - 2, 4, 4);
        }
        return bitmap;
    }
}
