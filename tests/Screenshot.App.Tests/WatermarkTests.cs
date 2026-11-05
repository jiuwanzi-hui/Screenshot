using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Screenshot.App.Capture;
using Screenshot.App.Core;
using Screenshot.App.Editor;
using Screenshot.App.Presentation;
using Screenshot.App.Watermarks;

namespace Screenshot.App.Tests;

public sealed class WatermarkTests
{
    private static readonly DateTimeOffset Timestamp = new(2026, 9, 16, 14, 35, 42, TimeSpan.FromHours(8));

    [Fact]
    public void MigrationEnablesButtonOnceWithoutEnablingAutomaticStamping()
    {
        var upgraded = (AppSettings.CreateDefault() with
        {
            SettingsVersion = 17, VisibleCaptureToolbarFeatures = [CaptureToolbarFeature.Save],
            CaptureToolbarFeatureOrder = [CaptureToolbarFeature.Save, CaptureToolbarFeature.PinImage],
        }).Normalize();
        Assert.Contains(CaptureToolbarFeature.Watermark, upgraded.VisibleCaptureToolbarFeatures);
        Assert.False(upgraded.Watermark.AutoApply);
        Assert.Equal(CaptureToolbarFeature.Watermark, upgraded.CaptureToolbarFeatureOrder[1]);
        var hidden = (upgraded with { VisibleCaptureToolbarFeatures = [CaptureToolbarFeature.Save] }).Normalize();
        Assert.DoesNotContain(CaptureToolbarFeature.Watermark, hidden.VisibleCaptureToolbarFeatures);
        Assert.Equal(19, (int)CaptureToolbarFeature.Watermark);
    }

    [Fact]
    public void SettingsLimitImagesRejectUnsafePathsAndRoundTripThroughViewModel()
    {
        var images = Enumerable.Range(0, 4).Select(index => new WatermarkImage { Id = Guid.NewGuid().ToString("N"), Name = $"图{index}" }).ToArray();
        var settings = (new WatermarkSettings
        {
            Text = "版权所有", IncludeTime = true, IncludeLocation = true, ManualLocation = "上海", AutoApply = true,
            Images = [new() { Id = "../../user-file" }, .. images], SelectedImageId = images[3].Id,
            X = double.NaN, Y = 99, WidthRatio = -1, Opacity = double.PositiveInfinity, TimeFormat = "%broken",
        }).Normalize();
        Assert.Equal(3, settings.Images.Length);
        Assert.Empty(settings.SelectedImageId);
        Assert.Equal(.96, settings.X);
        Assert.Equal(1, settings.Y);
        Assert.Equal(.05, settings.WidthRatio);
        Assert.Equal(.75, settings.Opacity);
        Assert.Equal(WatermarkSettings.TimeFormats[0], settings.TimeFormat);
        settings = settings with { SelectedImageId = images[1].Id };
        var model = new SettingsViewModel(AppSettings.CreateDefault() with { Watermark = settings });
        var saved = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(model.CreateSettings()))!.Normalize();
        Assert.Equal(settings.SelectedImageId, saved.Watermark.SelectedImageId);
        Assert.Equal(settings.Text, saved.Watermark.Text);
        Assert.True(saved.Watermark.AutoApply);
        Assert.Equal(3, saved.Watermark.Images.Length);
    }

    [Fact]
    public async Task LocationIsOptInAndFailureUsesManualLocationWithFixedCaptureTime()
    {
        var locationCalls = 0;
        var imageCalls = 0;
        var loader = new WatermarkLoader((_, _) => { imageCalls++; return Task.FromResult<BitmapSource?>(null); }, (_, _) =>
        {
            locationCalls++;
            return Task.FromResult(new WatermarkLocationResult(null, "定位失败，使用手填地点"));
        });
        var settings = new WatermarkSettings { IncludeTime = true, ManualLocation = "上海·办公室" };
        var result = await loader.LoadAsync(settings, Timestamp, CancellationToken.None);
        Assert.Equal(0, locationCalls);
        Assert.Equal(0, imageCalls);
        Assert.Equal("SnapCut\n2026-09-16 14:35:42", result.Composition.Text);
        result = await loader.LoadAsync(settings with { IncludeLocation = true }, Timestamp, CancellationToken.None);
        Assert.Equal(1, locationCalls);
        Assert.EndsWith("上海·办公室", result.Composition.Text);
        Assert.Contains("定位失败", result.Warning);
        result = await loader.LoadAsync(settings with { IncludeLocation = true, UseSystemLocation = false }, Timestamp, CancellationToken.None);
        Assert.Equal(1, locationCalls);
        Assert.Null(result.Warning);
        Assert.EndsWith("上海·办公室", result.Composition.Text);
    }

    [Fact]
    public async Task CancelledLateImageNeverPublishesOrStartsLocation()
    {
        var completion = new TaskCompletionSource<BitmapSource?>();
        using var cancellation = new CancellationTokenSource();
        var id = Guid.NewGuid().ToString("N");
        var published = false;
        var loader = new WatermarkLoader((_, _) => completion.Task, (_, _) => throw new InvalidOperationException("不应请求定位"));
        var task = loader.LoadAsync(new WatermarkSettings { Images = [new() { Id = id }], SelectedImageId = id, IncludeLocation = true },
            Timestamp, cancellation.Token, _ => published = true);
        cancellation.Cancel();
        completion.SetResult(null);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.False(published);
    }

    [Theory]
    [InlineData(240, 180, 0, 0)]
    [InlineData(240, 180, 1, 1)]
    [InlineData(1920, 1080, .96, .96)]
    [InlineData(200, 1200, .5, .5)]
    public void ImageAndTextStayInsideCaptureAndMatchCanvasExport(int width, int height, double x, double y)
    {
        WpfTestHost.Invoke(() =>
        {
            using var source = CapturedImage.FromBitmapSource(SolidBitmap(width, height, Colors.DarkSlateBlue));
            var original = Bytes(source.Preview);
            var settings = new WatermarkSettings { Text = "SnapCut 测试", IncludeTime = true, Opacity = .6 };
            foreach (var element in Enum.GetValues<WatermarkElement>())
                settings = settings.WithPlacement(element, settings.GetPlacement(element) with { X = x, Y = y });
            var watermark = new WatermarkComposition(settings,
                Timestamp, null, SolidBitmap(120, 60, Color.FromArgb(160, 255, 140, 30)));
            var bounds = watermark.GetBounds(new Size(width, height));
            Assert.True(bounds.Left >= 0 && bounds.Top >= 0 && bounds.Right <= width && bounds.Bottom <= height);
            var canvas = new ImageEditorCanvas();
            canvas.Initialize(source, width / 1.5, height / 1.5);
            canvas.SetWatermark(watermark);
            var exported = canvas.RenderEditedImage();
            Assert.Equal(width, exported.PixelWidth);
            Assert.Equal(height, exported.PixelHeight);
            Assert.Equal(Bytes(watermark.RenderOn(source.Preview)), Bytes(exported));
            Assert.NotEqual(original, Bytes(exported));
            Assert.Equal(original, Bytes(source.Preview));
            canvas.SetWatermark(null);
            Assert.Equal(Bytes(new WatermarkComposition(new WatermarkSettings { Text = "" }, Timestamp, null, null).RenderOn(source.Preview)), Bytes(canvas.RenderEditedImage()));
            canvas.Reset();
            canvas.Initialize(source, width, height);
            Assert.Equal(Bytes(watermark.RenderOn(source.Preview)).Length, Bytes(canvas.RenderEditedImage()).Length);
            Assert.Equal(original, Bytes(source.Preview));
            canvas.Reset();
        });
    }

    [Fact]
    public void LateWatermarkUpdateDoesNotDetachAnActiveTextAnnotation()
    {
        WpfTestHost.Invoke(() =>
        {
            using var source = CapturedImage.FromBitmapSource(SolidBitmap(400, 300, Colors.White));
            var canvas = new ImageEditorCanvas();
            canvas.Initialize(source, 400, 300);
            typeof(ImageEditorCanvas).GetMethod("StartTextInput", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .Invoke(canvas, [new Point(20, 20)]);
            var input = Assert.Single(canvas.Children.OfType<System.Windows.Controls.TextBox>());
            input.Text = "正在编辑的标注";
            var watermark = new WatermarkComposition(new WatermarkSettings(), Timestamp, null, null);
            canvas.SetWatermark(watermark);
            Assert.Same(input, Assert.Single(canvas.Children.OfType<System.Windows.Controls.TextBox>()));
            canvas.SetWatermark(null);
            Assert.Same(input, Assert.Single(canvas.Children.OfType<System.Windows.Controls.TextBox>()));
            Assert.Equal("正在编辑的标注", input.Text);
            canvas.CommitPendingText();
            Assert.True(canvas.CanUndo);
            canvas.Reset();
        });
    }

    [Fact]
    public void ReframingRepositionsWatermarkAndOtherCanvasesRemainUnmarked()
    {
        WpfTestHost.Invoke(() =>
        {
            using var source = CapturedImage.FromBitmapSource(SolidBitmap(400, 300, Colors.White));
            using var resized = CapturedImage.FromBitmapSource(SolidBitmap(600, 500, Colors.White));
            var watermark = new WatermarkComposition(new WatermarkSettings { Text = "Test", X = 1, Y = 1 }, Timestamp, null, null);
            var canvas = new ImageEditorCanvas();
            canvas.Initialize(source, 400, 300);
            canvas.SetWatermark(watermark);
            canvas.Reframe(resized, 600, 500, new Vector(0, 0));
            Assert.Equal(Bytes(watermark.RenderOn(resized.Preview)), Bytes(canvas.RenderEditedImage()));
            var other = new ImageEditorCanvas();
            other.Initialize(resized, 600, 500);
            Assert.NotEqual(Bytes(canvas.RenderEditedImage()), Bytes(other.RenderEditedImage()));
            canvas.InitializeAnnotationOverlay(600, 500, 600, 500);
            canvas.Measure(new Size(600, 500)); canvas.Arrange(new Rect(0, 0, 600, 500));
            var overlay = new RenderTargetBitmap(600, 500, 96, 96, PixelFormats.Pbgra32);
            overlay.Render(canvas);
            Assert.All(Bytes(overlay), value => Assert.Equal(0, value));
            canvas.Reset(); other.Reset();
        });
    }

    [Fact]
    public async Task ImportOwnsItsCopyPreservesAlphaAndDoesNotLockOriginalOrCopy()
    {
        var directory = Path.Combine(Path.GetTempPath(), "SnapCut-WatermarkTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var source = Path.Combine(directory, "original.png");
            WpfTestHost.Invoke(() =>
            {
                using var file = File.Create(source);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(SolidBitmap(32, 1800, Color.FromArgb(128, 240, 60, 30))));
                encoder.Save(file);
            });
            var store = new WatermarkImageStore(Path.Combine(directory, "owned"));
            var asset = await store.ImportAsync(source, CancellationToken.None);
            Assert.True(WatermarkSettings.IsValidImageId(asset.Id));
            Assert.True(File.Exists(source));
            File.Delete(source);
            var loaded = await store.LoadAsync(asset.Id, false, CancellationToken.None);
            Assert.NotNull(loaded);
            Assert.True(loaded.IsFrozen);
            Assert.Equal(32, loaded.PixelWidth);
            var thumbnail = await store.LoadAsync(asset.Id, true, CancellationToken.None);
            Assert.InRange(thumbnail!.PixelWidth, 1, 32);
            Assert.Equal(600, thumbnail.PixelHeight);
            var bytes = Bytes(loaded);
            Assert.Equal(128, bytes[3]);
            store.Delete(asset.Id);
            Assert.False(File.Exists(store.GetPath(asset.Id)));
            Assert.Throws<ArgumentException>(() => store.GetPath("../original"));
            await Assert.ThrowsAsync<FileNotFoundException>(() => store.LoadAsync(asset.Id, false, CancellationToken.None));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static BitmapSource SolidBitmap(int width, int height, Color color)
    {
        var bytes = new byte[width * height * 4];
        for (var i = 0; i < bytes.Length; i += 4) { bytes[i] = color.B; bytes[i + 1] = color.G; bytes[i + 2] = color.R; bytes[i + 3] = color.A; }
        var bitmap = BitmapSource.Create(width, height, 144, 144, PixelFormats.Bgra32, null, bytes, width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    private static byte[] Bytes(BitmapSource bitmap)
    {
        var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        var bytes = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        converted.CopyPixels(bytes, bitmap.PixelWidth * 4, 0);
        return bytes;
    }
}
