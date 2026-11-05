using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Screenshot.App.Capture;
using Screenshot.App.Core;
using Screenshot.App.Editor;
using Screenshot.App.Pin;

namespace Screenshot.App.Tests;

public sealed class WatermarkOverlayTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OrdinaryToolbarToggleAddsAndRemovesPresetFromActualExport(bool automatic)
    {
        CaptureOverlayWindow? overlay = null;
        using var pins = new PinnedImageManager();
        Task<CapturedImage>? export = null;
        byte[]? original = null;
        try
        {
            WpfTestHost.Invoke(() =>
            {
                overlay = new CaptureOverlayWindow(Options(pins, new WatermarkSettings { Text = "SnapCut · 水印测试", AutoApply = automatic }),
                    isStartupPrewarm: true, deferInitialScreenCapture: true);
                var handle = new WindowInteropHelper(overlay).EnsureHandle();
                var root = (FrameworkElement)overlay.Content;
                overlay.Content = null;
                root.Resources = overlay.Resources;
                HwndSource.FromHwnd(handle)!.RootVisual = root;
                root.Measure(new Size(1024, 768)); root.Arrange(new Rect(0, 0, 1024, 768)); root.UpdateLayout();
                var canvas = (ImageEditorCanvas)overlay.FindName("InlineEditorCanvas");
                var bitmap = new System.Drawing.Bitmap(320, 240);
                using (var graphics = System.Drawing.Graphics.FromImage(bitmap)) graphics.Clear(System.Drawing.Color.White);
                var image = new CapturedImage(bitmap);
                typeof(CaptureOverlayWindow).GetField("_inlineEditorImage", Private)!.SetValue(overlay, image);
                canvas.Initialize(image, 320, 240);
                original = Bytes(canvas.RenderEditedImage());
                var selection = (System.Windows.Shapes.Rectangle)overlay.FindName("SelectionRectangle");
                Canvas.SetLeft(selection, 20); Canvas.SetTop(selection, 20); selection.Width = 320; selection.Height = 240;
                var button = (Button)overlay.FindName("WatermarkButton");
                Assert.Equal(Visibility.Visible, button.Visibility);
                var row = (Panel)button.Parent;
                Assert.True(row.Children.IndexOf(button) < row.Children.IndexOf((Button)overlay.FindName("PinButton")));
                typeof(CaptureOverlayWindow).GetMethod("InitializeWatermark", Private)!.Invoke(overlay, null);
                if (!automatic) button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                export = Export(overlay);
            });
            using (var image = await export!.WaitAsync(TimeSpan.FromSeconds(5)))
                WpfTestHost.Invoke(() => Assert.NotEqual(original, Bytes(image.Preview)));
            WpfTestHost.Invoke(() =>
            {
                var button = (Button)overlay!.FindName("WatermarkButton");
                Assert.Equal("取消水印", button.ToolTip);
                var canvas = (ImageEditorCanvas)overlay.FindName("InlineEditorCanvas");
                var suppress = typeof(CaptureOverlayWindow).GetMethod("SetResizeControlsSuppressed", Private)!;
                suppress.Invoke(overlay, [true]);
                var layer = canvas.Children.OfType<FrameworkElement>().Single(item => item.GetType().Name == "WatermarkVisual");
                Assert.Equal(Visibility.Hidden, layer.Visibility);
                suppress.Invoke(overlay, [false]);
                Assert.Equal(Visibility.Visible, layer.Visibility);
                suppress.Invoke(overlay, [true]);
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                suppress.Invoke(overlay, [false]);
                Assert.DoesNotContain(canvas.Children.OfType<FrameworkElement>(), item => item.GetType().Name == "WatermarkVisual");
                export = Export(overlay);
            });
            using (var image = await export!.WaitAsync(TimeSpan.FromSeconds(5)))
                WpfTestHost.Invoke(() => Assert.Equal(original, Bytes(image.Preview)));
        }
        finally { if (overlay is not null) WpfTestHost.Invoke(overlay.Close); }
    }

    [Fact]
    public void NonOrdinaryOverlayDoesNotExposeWatermarkEvenWhenFeatureIsConfigured()
    {
        WpfTestHost.Invoke(() =>
        {
            using var pins = new PinnedImageManager();
            var overlay = new CaptureOverlayWindow(Options(pins, null), isStartupPrewarm: true, deferInitialScreenCapture: true);
            try { Assert.Equal(Visibility.Collapsed, ((Button)overlay.FindName("WatermarkButton")).Visibility); }
            finally { overlay.Close(); }
        });
    }

    private static Task<CapturedImage> Export(CaptureOverlayWindow overlay) =>
        (Task<CapturedImage>)typeof(CaptureOverlayWindow).GetMethod("CaptureCurrentResultAsync", Private)!.Invoke(overlay, [true])!;

    private static CaptureOverlayOptions Options(PinnedImageManager pins, WatermarkSettings? watermark) => new()
    {
        SaveDirectory = Path.GetTempPath(), KeepHistory = false, HistoryLimit = 0,
        HistoryService = new CaptureHistoryService(), PinnedImageManager = pins, StartOcrAsync = _ => Task.CompletedTask,
        Watermark = watermark,
    };

    private static byte[] Bytes(BitmapSource bitmap)
    {
        var converted = new FormatConvertedBitmap(bitmap, System.Windows.Media.PixelFormats.Bgra32, null, 0);
        var bytes = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        converted.CopyPixels(bytes, bitmap.PixelWidth * 4, 0);
        return bytes;
    }
}
