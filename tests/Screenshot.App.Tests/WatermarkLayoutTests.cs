using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Screenshot.App.Capture;
using Screenshot.App.Core;
using Screenshot.App.Editor;
using Screenshot.App.Presentation;
using Screenshot.App.Watermarks;

namespace Screenshot.App.Tests;

public sealed class WatermarkLayoutTests
{
    private static readonly DateTimeOffset Time = new(2026, 9, 16, 18, 30, 0, TimeSpan.FromHours(8));
    private static readonly Size PreviewSize = new(740, 290);
    private static WatermarkSettings AllElements => new()
    {
        Text = "独立文字", IncludeTime = true, IncludeLocation = true,
        ManualLocation = "上海 · 工作室", UseSystemLocation = false, Opacity = 1,
    };

    [Theory]
    [InlineData(WatermarkElement.Image)]
    [InlineData(WatermarkElement.Text)]
    [InlineData(WatermarkElement.Time)]
    [InlineData(WatermarkElement.Location)]
    public void DragResizeAndCancelOnlyChangeSelectedElement(WatermarkElement element)
    {
        WpfTestHost.Invoke(() =>
        {
            var composition = new WatermarkComposition(AllElements, Time, null, Solid(240, 120, Colors.Coral));
            var preview = new WatermarkPreview { Composition = composition, Placement = composition.Settings };
            preview.Measure(PreviewSize); preview.Arrange(new Rect(PreviewSize));
            preview.SelectElement(element);
            var original = composition.GetBounds(element, PreviewSize);
            Assert.True(preview.BeginManipulation(new Point(original.Left + original.Width / 2, original.Top + original.Height / 2)));
            preview.MoveManipulation(new Point(original.Left + original.Width / 2 - 60, original.Top + original.Height / 2 + 10));
            preview.EndManipulation();
            var movedSettings = preview.Placement;
            var moved = composition.GetBounds(element, PreviewSize, movedSettings);
            Assert.InRange(original.Left - moved.Left, 59.9, 60.1);
            foreach (var other in Enum.GetValues<WatermarkElement>().Where(value => value != element))
                Assert.Equal(composition.GetBounds(other, PreviewSize), composition.GetBounds(other, PreviewSize, movedSettings));
            Assert.True(preview.BeginManipulation(moved.BottomRight));
            preview.MoveManipulation(moved.BottomRight + new Vector(moved.Width * .2, moved.Height * .2));
            Assert.True(composition.GetBounds(element, PreviewSize, preview.Placement).Width > moved.Width);
            preview.EndManipulation(cancel: true);
            Assert.Equal(movedSettings, preview.Placement);
            var saved = JsonSerializer.Deserialize<WatermarkSettings>(JsonSerializer.Serialize(movedSettings))!.Normalize();
            Assert.Equal(movedSettings.GetPlacement(element), saved.GetPlacement(element));
        });
    }

    [Fact]
    public void ImageSizeAndTextLengthDoNotChangeOtherElementsFontSize()
    {
        WpfTestHost.Invoke(() =>
        {
            var settings = AllElements.Normalize();
            var one = new WatermarkComposition(settings, Time, null, Solid(900, 700, Colors.Coral));
            var twoSettings = settings.WithPlacement(WatermarkElement.Image, new() { X = .1, Y = .1, Size = .8 }) with { Text = "比较长的独立水印文字" };
            var two = new WatermarkComposition(twoSettings, Time, null, Solid(900, 700, Colors.Coral));
            foreach (var element in new[] { WatermarkElement.Time, WatermarkElement.Location })
                Assert.Equal(one.GetBounds(element, PreviewSize), two.GetBounds(element, PreviewSize));
            Assert.InRange(two.GetBounds(WatermarkElement.Text, PreviewSize).Height / one.GetBounds(WatermarkElement.Text, PreviewSize).Height, .9, 1.15);
        });
    }

    [Theory]
    [InlineData(24, 18)]
    [InlineData(770, 545)]
    [InlineData(300, 1600)]
    [InlineData(3840, 2160)]
    public void AllPartsClipInsideSelectionAndCanvasMatchesDirectExport(int width, int height)
    {
        WpfTestHost.Invoke(() =>
        {
            var settings = AllElements;
            foreach (var part in Enum.GetValues<WatermarkElement>())
                settings = settings.WithPlacement(part, new() { X = 1, Y = 1, Size = .8 });
            var composition = new WatermarkComposition(settings, Time, null, Solid(80, 900, Colors.Coral));
            foreach (var part in composition.Parts)
            {
                var bounds = composition.GetBounds(part.Element, new Size(width, height));
                Assert.True(bounds.Left >= 0 && bounds.Top >= 0 && bounds.Right <= width && bounds.Bottom <= height);
            }
            using var source = CapturedImage.FromBitmapSource(Solid(width, height, Colors.DarkSlateBlue));
            var canvas = new ImageEditorCanvas();
            canvas.Initialize(source, width / 1.5, height / 1.5);
            canvas.SetWatermark(composition);
            Assert.Equal(Bytes(composition.RenderOn(source.Preview)), Bytes(canvas.RenderEditedImage()));
            canvas.Reset();
        });
    }

    [Fact]
    public void SuppressionSurvivesLateContentAndReframeWithoutRemovingExportWatermark()
    {
        WpfTestHost.Invoke(() =>
        {
            using var source = CapturedImage.FromBitmapSource(Solid(400, 300, Colors.White));
            using var resized = CapturedImage.FromBitmapSource(Solid(600, 480, Colors.White));
            var composition = new WatermarkComposition(AllElements, Time, null, Solid(120, 80, Colors.Coral));
            var canvas = new ImageEditorCanvas();
            canvas.Initialize(source, 400, 300);
            canvas.SetWatermark(composition);
            canvas.SetWatermarkSuppressed(true);
            canvas.SetWatermark(new WatermarkComposition(AllElements with { Text = "迟到的更新" }, Time, null, Solid(120, 80, Colors.Coral)));
            canvas.Reframe(resized, 600, 480, new Vector());
            var layer = canvas.Children.OfType<FrameworkElement>().Single(item => item.GetType().Name == "WatermarkVisual");
            Assert.Equal(Visibility.Hidden, layer.Visibility);
            Assert.Equal(Bytes(resized.Preview), Bytes(Render(canvas, 600, 480)));
            Assert.NotEqual(Bytes(resized.Preview), Bytes(canvas.RenderEditedImage()));
            canvas.SetWatermarkSuppressed(false);
            Assert.Equal(Visibility.Visible, layer.Visibility);
            Assert.Equal(Bytes(canvas.RenderEditedImage()), Bytes(Render(canvas, 600, 480)));
            canvas.Reset();
        });
    }

    [Fact]
    public void MovingPreviewErasesPreviousImagePixels()
    {
        WpfTestHost.Invoke(() =>
        {
            var settings = (AllElements with { Text = "", IncludeTime = false, IncludeLocation = false })
                .WithPlacement(WatermarkElement.Image, new() { X = .1, Y = .3, Size = .3 });
            var preview = new WatermarkPreview
            {
                Placement = settings,
                Composition = new WatermarkComposition(settings, Time, null, Solid(120, 80, Colors.Red)),
            };
            var original = preview.Composition.GetBounds(WatermarkElement.Image, PreviewSize);
            var before = Render(preview, 740, 290);
            preview.Placement = settings.WithPlacement(WatermarkElement.Image, new() { X = .8, Y = .3, Size = .3 });
            preview.InvalidateVisual();
            var after = Render(preview, 740, 290);
            var background = Render(new WatermarkPreview(), 740, 290);
            var x = (int)(original.Left + original.Width / 2);
            var y = (int)(original.Top + original.Height / 2);
            Assert.Equal(255, Bytes(before)[(y * 740 + x) * 4 + 2]);
            Assert.Equal(Bytes(background)[(y * 740 + x) * 4 + 2], Bytes(after)[(y * 740 + x) * 4 + 2]);
            preview.ClearContent();
            Assert.Null(((DrawingVisual)VisualTreeHelper.GetChild(preview, 0)).Drawing);
        });
    }

    [Theory]
    [InlineData("{\"lookupSource\":\"coordinates\",\"principalSubdivision\":\"上海市\",\"city\":\"上海市\",\"locality\":\"浦东新区\"}", "上海市 · 浦东新区")]
    [InlineData("{\"city\":\"苏州市\",\"locality\":\"\"}", "苏州市")]
    [InlineData("{\"city\":null,\"principalSubdivision\":17}", null)]
    [InlineData("{\"lookupSource\":\"ipGeolocation\",\"city\":\"不应采用的代理位置\"}", null)]
    public void CityResponseUsesLocalityDeduplicatesAndRejectsIpFallback(string json, string? expected) =>
        Assert.Equal(expected, WatermarkLocationService.ParsePlaceName(json));

    [Fact]
    public async Task ManualTypingOverridesSuccessfulAndPendingLocationAndClearsOnHide()
    {
        HwndSource? host = null;
        WatermarkSettingsControl? control = null;
        WatermarkSettings? saved = null;
        var pending = new TaskCompletionSource<WatermarkLocationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        CancellationToken requestedToken = default;
        Task? lookup = null;
        try
        {
            WpfTestHost.Invoke(() =>
            {
                control = new WatermarkSettingsControl(AllElements with { UseSystemLocation = true, LocationDisplay = WatermarkLocationDisplay.PlaceName },
                    value => saved = value, (display, token) =>
                    {
                        Assert.Equal(WatermarkLocationDisplay.PlaceName, display);
                        requestedToken = token;
                        return ++calls == 1 ? Task.FromResult(new WatermarkLocationResult("上海市", "已获取")) : pending.Task;
                    });
                host = new HwndSource(new HwndSourceParameters("Watermark hidden test") { Width = 800, Height = 1500, WindowStyle = unchecked((int)0x80000000) });
                host.RootVisual = control;
                control.Measure(new Size(740, double.PositiveInfinity)); control.Arrange(new Rect(control.DesiredSize));
                Assert.True(control.IsVisible);
                lookup = control.LocateAsync();
            });
            await lookup!;
            WpfTestHost.Invoke(() =>
            {
                var preview = (WatermarkPreview)((ContentControl)control!.FindName("PreviewHost")).Content;
                Assert.EndsWith("上海市", preview.Composition!.Text);
                ((TextBox)control.FindName("ManualLocation")).Text = "上海通协小区66";
                Assert.EndsWith("上海通协小区66", preview.Composition.Text);
                Assert.False(saved!.UseSystemLocation);
                Assert.Equal(2, ((ComboBox)control.FindName("LocationMode")).SelectedIndex);
                ((ComboBox)control.FindName("LocationMode")).SelectedIndex = 0;
                lookup = control.LocateAsync();
                ((TextBox)control.FindName("ManualLocation")).Text = "手填的新地点";
                Assert.True(requestedToken.IsCancellationRequested);
            });
            pending.SetResult(new("迟到的城市", "已获取"));
            await lookup!;
            WpfTestHost.Invoke(() =>
            {
                var preview = (WatermarkPreview)((ContentControl)control!.FindName("PreviewHost")).Content;
                Assert.EndsWith("手填的新地点", preview.Composition!.Text);
                ((ComboBox)control.FindName("LocationMode")).SelectedIndex = 1;
                Assert.True(saved!.UseSystemLocation);
                Assert.Equal(WatermarkLocationDisplay.Coordinates, saved.LocationDisplay);
                control.Visibility = Visibility.Collapsed;
                Assert.Null(preview.Composition);
                Assert.Null(typeof(WatermarkSettingsControl).GetField("_location", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(control));
            });
        }
        finally { WpfTestHost.Invoke(() => { control?.Dispose(); host?.Dispose(); }); }
    }

    private static RenderTargetBitmap Solid(int width, int height, Color color)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen()) dc.DrawRectangle(new SolidColorBrush(color), null, new Rect(0, 0, width, height));
        var image = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        image.Render(visual); image.Freeze(); return image;
    }

    private static RenderTargetBitmap Render(FrameworkElement element, int width, int height)
    {
        element.Measure(new Size(width, height)); element.Arrange(new Rect(0, 0, width, height)); element.UpdateLayout();
        var image = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        image.Render(element); return image;
    }

    private static byte[] Bytes(BitmapSource image)
    {
        var bytes = new byte[image.PixelWidth * image.PixelHeight * 4];
        image.CopyPixels(bytes, image.PixelWidth * 4, 0); return bytes;
    }
}
