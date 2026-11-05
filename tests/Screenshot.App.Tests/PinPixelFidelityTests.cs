using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Screenshot.App.Capture;
using Screenshot.App.Core;
using Screenshot.App.Editor;
using Screenshot.App.Pin;

namespace Screenshot.App.Tests;

public sealed class PinPixelFidelityTests
{
    [Theory]
    [InlineData(1, 83, 47)]
    [InlineData(1.25, 83, 47)]
    [InlineData(1.5, 83, 47)]
    [InlineData(1.75, 83, 47)]
    [InlineData(2, 83, 47)]
    [InlineData(1.25, 676, 162)]
    [InlineData(1.5, 676, 162)]
    public void RepeatedPinRenderingPreservesPixelsThroughTheWholeWindowLayout(double dpi, int width, int height)
    {
        WpfTestHost.Invoke(() =>
        {
            using var bitmap = new System.Drawing.Bitmap(width, height);
            for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                bitmap.SetPixel(x, y, System.Drawing.Color.FromArgb(x % 2 * 255, y % 2 * 255, (x + y) % 2 * 255));
            VerifyRepeatedPinRendering(CapturedImage.ToBitmapSource(bitmap), dpi);
        });
    }

    [Theory]
    [InlineData(1)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(1.75)]
    [InlineData(2)]
    public void ReportedChatImageStaysUnchangedAfterTenScreenshotAndPinCycles(double dpi)
    {
        WpfTestHost.Invoke(() =>
        {
            using var stream = typeof(PinPixelFidelityTests).Assembly.GetManifestResourceStream(
                "Screenshot.App.Tests.Fixtures.pin-recapture-chat.jpg")!;
            using var bitmap = new System.Drawing.Bitmap(stream);
            VerifyRepeatedPinRendering(CapturedImage.ToBitmapSource(bitmap), dpi);
        });
    }

    [Theory]
    [InlineData(73, 80)]
    [InlineData(188, 202)]
    [InlineData(411, 418)]
    public void NativeWindowCeilingAt175PercentDoesNotResamplePins(int width, int height)
    {
        WpfTestHost.Invoke(() =>
        {
            using var bitmap = new System.Drawing.Bitmap(width, height);
            for (var y = 0; y < height; y++)
                for (var x = 0; x < width; x++)
                    bitmap.SetPixel(x, y, (x + y) % 2 == 0 ? System.Drawing.Color.Black : System.Drawing.Color.White);
            VerifyRepeatedPinRendering(CapturedImage.ToBitmapSource(bitmap), 1.75, nativeCeiling: true);
        });
    }

    private static void VerifyRepeatedPinRendering(BitmapSource source, double dpi, bool nativeCeiling = false)
    {
        var width = source.PixelWidth;
        var height = source.PixelHeight;
        var expected = new byte[width * height * 4];
        source.CopyPixels(expected, width * 4, 0);
        using var overlay = new CaptureOverlayWindow(null, deferInitialScreenCapture: true);
        try
        {
            for (var iteration = 0; iteration < 10; iteration++)
            {
                var frozenDesktop = CapturePixelFidelityTests.RenderFrozenDesktop(overlay, source, dpi);
                var displayed = new byte[expected.Length];
                frozenDesktop.CopyPixels(displayed, width * 4, 0);
                Assert.True(expected.SequenceEqual(displayed), $"DPI {dpi}, pass {iteration + 1}: frozen desktop pixels changed.");
                // Ordinary selection initializes the editor even without annotations.
                // Include that export before showing the next pin.
                using var selected = CapturedImage.FromBitmapSource(source);
                var editor = new ImageEditorCanvas();
                editor.Initialize(selected, width / dpi, height / dpi);
                CapturedImage exported;
                try { exported = CapturedImage.FromBitmapSource(editor.RenderEditedImage()); }
                finally { editor.Reset(); }
                var window = new PinnedImageWindow(exported);
                try
                {
                    // Exercise the real nested pin layout off screen, including its
                    // centered Image arrangement and independent border/shadow.
                    var shell = (Border)window.Content;
                    shell.DataContext = window.DataContext;
                    window.Content = null;
                    shell.Opacity = 1;
                    var root = new Grid { UseLayoutRounding = true, SnapsToDevicePixels = true };
                    root.Children.Add(shell);
                    VisualTreeHelper.SetRootDpi(root, new DpiScale(dpi, dpi));
                    var margin = (int)Math.Round(PinnedImageWindow.ShadowMarginDip * dpi);
                    var frameWidth = nativeCeiling ? (int)Math.Ceiling((width / dpi + PinnedImageWindow.ShadowMarginDip * 2) * dpi) : width + margin * 2;
                    var frameHeight = nativeCeiling ? (int)Math.Ceiling((height / dpi + PinnedImageWindow.ShadowMarginDip * 2) * dpi) : height + margin * 2;
                    var size = new Size(frameWidth / dpi, frameHeight / dpi);
                    root.Measure(size);
                    root.Arrange(new Rect(size));
                    root.UpdateLayout();
                    var frame = new RenderTargetBitmap(frameWidth, frameHeight,
                        96 * dpi, 96 * dpi, PixelFormats.Pbgra32);
                    frame.Render(root);
                    source = new CroppedBitmap(frame, new Int32Rect(margin, margin, width, height));
                    source.Freeze();
                    var actual = new byte[expected.Length];
                    source.CopyPixels(actual, width * 4, 0);
                    var mismatch = Enumerable.Range(0, actual.Length).FirstOrDefault(i => actual[i] != expected[i], -1);
                    Assert.True(mismatch < 0, $"DPI {dpi}, pass {iteration + 1}: pin pixels changed at byte {mismatch}; expected {expected[Math.Max(0, mismatch)]}, actual {actual[Math.Max(0, mismatch)]}.");
                }
                finally { window.Close(); }
            }
        }
        finally { overlay.Close(); }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(1.75)]
    [InlineData(2)]
    public void NativePixelDrawingSurvivesFractionalDpiWithoutResampling(double scale)
    {
        WpfTestHost.Invoke(() =>
        {
            const int width = 83, height = 47;
            var pixels = new byte[width * height * 4];
            for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var index = (y * width + x) * 4;
                pixels[index] = pixels[index + 1] = pixels[index + 2] = (byte)((x + y) % 2 == 0 ? 255 : 0);
                pixels[index + 3] = 255;
            }
            var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
            source.Freeze();
            var image = new PixelAlignedImage { Source = source, Stretch = Stretch.Uniform, UseLayoutRounding = true };
            VisualTreeHelper.SetRootDpi(image, new DpiScale(scale, scale));
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            image.Measure(new Size(width / scale, height / scale));
            image.Arrange(new Rect(0, 0, width / scale, height / scale));
            image.UpdateLayout();
            var rendered = new RenderTargetBitmap(width, height, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            rendered.Render(image);
            var actual = new byte[pixels.Length];
            rendered.CopyPixels(actual, width * 4, 0);
            Assert.Equal(pixels, actual);
        });
    }

    [Theory]
    [InlineData(1.25, 157.3, 91.7)]
    [InlineData(1.5, 149.7, 99.4)]
    [InlineData(2, 53.25, 35.75)]
    public void FractionalZoomAlignsImageEdgesToPhysicalPixels(double dpi, double width, double height)
    {
        var rect = PixelAlignedImage.GetPixelAlignedBounds(801, 479, new Size(width, height), new DpiScale(dpi, dpi));
        foreach (var coordinate in new[] { rect.X, rect.Y, rect.Width, rect.Height })
            Assert.Equal(Math.Round(coordinate * dpi), coordinate * dpi, 8);
    }

    [Theory]
    [InlineData(1.25)]
    [InlineData(1.75)]
    [InlineData(2)]
    public void GroupingPreservesNativePixelsOnScaledDisplays(double dpi)
    {
        WpfTestHost.Invoke(() =>
        {
            var window = new PinnedImageWindow(new CapturedImage(new System.Drawing.Bitmap(411, 418)));
            try
            {
                VisualTreeHelper.SetRootDpi(window, new DpiScale(dpi, dpi));
                window.Width = 411 / dpi + PinnedImageWindow.ShadowMarginDip * 2;
                window.Height = 418 / dpi + PinnedImageWindow.ShadowMarginDip * 2;
                Assert.Same(window.Preview, window.CreateGroupingPreview());
            }
            finally { window.Close(); }
        });
    }

    [Theory]
    [InlineData(50)]
    [InlineData(100)]
    [InlineData(150)]
    [InlineData(200)]
    public void ExportScalingDoesNotReduceOrCompoundPinnedSourcePixels(int percent)
    {
        WpfTestHost.Invoke(() =>
        {
            var bitmap = new System.Drawing.Bitmap(80, 48);
            for (var y = 0; y < bitmap.Height; y++)
            for (var x = 0; x < bitmap.Width; x++)
                bitmap.SetPixel(x, y, System.Drawing.Color.FromArgb(x * 3, y * 5, (x + y) % 256));
            var window = new PinnedImageWindow(new CapturedImage(bitmap), outputScalePercent: percent);
            try
            {
                var source = window.Preview;
                for (var iteration = 0; iteration < 3; iteration++)
                {
                    var output = window.CreateOutputPreview();
                    Assert.Equal(80 * percent / 100, output.PixelWidth);
                    Assert.Equal(48 * percent / 100, output.PixelHeight);
                    Assert.Same(source, window.Preview);
                    Assert.Equal(80, window.Preview.PixelWidth);
                    if (percent == 100) Assert.Same(source, output);
                }
                using var original = window.CloneImage();
                Assert.Equal(bitmap.GetPixel(39, 21), original.Bitmap.GetPixel(39, 21));
                Assert.True(window.UseLayoutRounding);
                Assert.Equal(BitmapScalingMode.HighQuality,
                    RenderOptions.GetBitmapScalingMode((PixelAlignedImage)window.FindName("PinnedImage")));
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void PinsWithoutAnOutputScaleKeepTheirOriginalExportSize()
    {
        WpfTestHost.Invoke(() =>
        {
            // Direct pins / result images (including long captures) never
            // inherited ordinary screenshot scaling and must keep that behavior.
            var window = new PinnedImageWindow(new CapturedImage(new System.Drawing.Bitmap(80, 48)),
                settingsProvider: () => new AppSettings { ScreenshotScalePercent = 200 });
            try { Assert.Same(window.Preview, window.CreateOutputPreview()); }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void PersistenceKeepsDeferredOutputScaleAndOlderImagesDefaultToOriginalSize()
    {
        var state = new PinnedImageState("sample", "sample.png", 0, 0, 80, 48) { OutputScalePercent = 150 };
        var json = System.Text.Json.JsonSerializer.Serialize(state);
        Assert.Equal(150, System.Text.Json.JsonSerializer.Deserialize<PinnedImageState>(json)!.OutputScalePercent);
        const string legacy = "{\"Id\":\"sample\",\"ImageFileName\":\"sample.png\",\"Left\":0,\"Top\":0,\"Width\":80,\"Height\":48}";
        Assert.Equal(100, System.Text.Json.JsonSerializer.Deserialize<PinnedImageState>(legacy)!.OutputScalePercent);
    }
}
