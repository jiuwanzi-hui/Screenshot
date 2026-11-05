using System.Drawing;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Screenshot.App.Capture;
using Screenshot.App.Infrastructure;
using WpfPoint = System.Windows.Point;
using Point = System.Drawing.Point;

namespace Screenshot.App.Tests;

public sealed class ColorPickerFollowingTests
{
    private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;

    [Fact]
    public void PickerPositionDoesNotReadNativeBitmapWhileSamplingHoldsIt()
    {
        WpfTestHost.Invoke(() =>
        {
            using var image = new CapturedImage(new Bitmap(80, 60));
            using var overlay = new CaptureOverlayWindow(null, isStartupPrewarm: true,
                initialScreenSnapshot: image);
            var root = (FrameworkElement)overlay.Content;
            overlay.Content = null;
            using var source = new HwndSource(new HwndSourceParameters("Offscreen locked sample")
            { WindowStyle = unchecked((int)0x80000000), Width = 80, Height = 60 });
            source.RootVisual = root;
            root.Measure(new System.Windows.Size(80, 60));
            root.Arrange(new Rect(0, 0, 80, 60));
            using var locked = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var reader = Task.Run(() =>
            {
                var data = image.Bitmap.LockBits(new Rectangle(0, 0, 80, 60),
                    System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                try { locked.Set(); release.Wait(TimeSpan.FromSeconds(5)); }
                finally { image.Bitmap.UnlockBits(data); }
            });
            try
            {
                Assert.True(locked.Wait(TimeSpan.FromSeconds(5)));
                typeof(CaptureOverlayWindow).GetField("_isColorPickerActive", Private)!.SetValue(overlay, true);
                // Avoid launching another reader; this call must still calculate
                // the pixel bounds and reposition the magnifier without GDI+.
                typeof(CaptureOverlayWindow).GetField("_lastColorPickerUpdateTimestamp", Private)!.SetValue(overlay, System.Diagnostics.Stopwatch.GetTimestamp());
                Assert.Null(Record.Exception(() => typeof(CaptureOverlayWindow)
                    .GetMethod("UpdateColorPicker", Private)!.Invoke(overlay, [new WpfPoint(20, 20)])));
            }
            finally { release.Set(); reader.GetAwaiter().GetResult(); overlay.Close(); }
        });
    }

    [Theory]
    [InlineData(System.Drawing.Imaging.PixelFormat.Format32bppArgb)]
    [InlineData(System.Drawing.Imaging.PixelFormat.Format32bppRgb)]
    public void MagnifierFollowsPhysicalPositionsWithoutAnyMouseMoveOrAltEvent(System.Drawing.Imaging.PixelFormat format)
    {
        WpfTestHost.Invoke(() =>
        {
            using var overlay = new CaptureOverlayWindow(null, isStartupPrewarm: true,
                initialScreenSnapshot: new CapturedImage(new Bitmap(800, 600, format)));
            try
            {
                var root = (FrameworkElement)overlay.Content;
                overlay.Content = null;
                using var source = new HwndSource(new HwndSourceParameters("Offscreen magnifier")
                {
                    WindowStyle = unchecked((int)0x80000000), Width = 800, Height = 600,
                });
                source.RootVisual = root;
                root.Measure(new System.Windows.Size(800, 600));
                root.Arrange(new Rect(0, 0, 800, 600));
                root.UpdateLayout();
                typeof(CaptureOverlayWindow).GetField("_isColorPickerActive", Private)!.SetValue(overlay, true);
                var surface = (FrameworkElement)overlay.FindName("CaptureSurface");
                var panel = (Border)overlay.FindName("ColorPickerPanel");
                var transform = Assert.IsType<TranslateTransform>(panel.RenderTransform);
                var first = surface.PointToScreen(new WpfPoint(40, 60));
                var second = surface.PointToScreen(new WpfPoint(180, 160));
                Assert.True(overlay.PresentColorPickerCursor(new Point((int)first.X, (int)first.Y)));
                var initial = new WpfPoint(transform.X, transform.Y);
                Assert.False(overlay.PresentColorPickerCursor(new Point((int)first.X, (int)first.Y)));
                Assert.True(overlay.PresentColorPickerCursor(new Point((int)second.X, (int)second.Y)));
                Assert.NotEqual(initial, new WpfPoint(transform.X, transform.Y));
                var timestamp = typeof(CaptureOverlayWindow).GetField("_lastColorPickerUpdateTimestamp", Private)!;
                timestamp.SetValue(overlay, System.Diagnostics.Stopwatch.GetTimestamp());
                Assert.True(overlay.PresentColorPickerCursor(new Point((int)first.X, (int)first.Y)));
                Assert.True((bool)typeof(CaptureOverlayWindow).GetField("_colorPickerSamplePending", Private)!.GetValue(overlay)!);
                timestamp.SetValue(overlay, 0L);
                // The last pixel sample catches up without another mouse move.
                Assert.True(overlay.PresentColorPickerCursor(new Point((int)first.X, (int)first.Y)));
                Assert.False(overlay.PresentColorPickerCursor(new Point((int)first.X, (int)first.Y)));
                // Rendering ticks can arrive after the user begins selecting.
                typeof(CaptureOverlayWindow).GetMethod("ExitColorPicker", Private)!.Invoke(overlay, null);
                Assert.False(overlay.PresentColorPickerCursor(new Point((int)first.X, (int)first.Y)));
                Assert.False(overlay.IsVisible);
                Assert.False(surface.IsMouseCaptured);
            }
            finally { overlay.Close(); }
        });
    }
}
