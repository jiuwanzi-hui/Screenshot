using System.Reflection;
using System.Windows;
using System.Windows.Input;
using Screenshot.App.Capture;
using Screenshot.App.Pin;

namespace Screenshot.App.Tests;

public sealed class PinnedZoomInteractionTests
{
    private static readonly double[] RenderWidths = [824d, 426.4, 275.2, 154.8];
    [Fact]
    public void ShrinkingPinKeepsImageInteriorUnchanged()
    {
        WpfTestHost.Invoke(() =>
        {
            using var bitmap = new System.Drawing.Bitmap(800, 480);
            using (var graphics = System.Drawing.Graphics.FromImage(bitmap)) graphics.Clear(System.Drawing.Color.FromArgb(24, 24, 24));
            var window = new PinnedImageWindow(new CapturedImage((System.Drawing.Bitmap)bitmap.Clone()));
            try
            {
                var shell = (System.Windows.Controls.Border)window.Content;
                shell.DataContext = window.DataContext;
                window.Content = null;
                shell.Opacity = 1;
                shell.Resources["SelectionBorderBrush"] = System.Windows.Media.Brushes.SlateGray;
                var host = new System.Windows.Controls.Border { Background = System.Windows.Media.Brushes.Black, Child = shell };
                foreach (var width in RenderWidths)
                {
                    var height = (width - 24) * .6 + 24;
                    host.Measure(new Size(width, height));
                    host.Arrange(new Rect(0, 0, width, height));
                    host.UpdateLayout();
                    var render = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(width), (int)Math.Ceiling(height),
                        96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                    render.Render(host);
                    if (Environment.GetEnvironmentVariable("SNAPCUT_PIN_RENDER_DIRECTORY") is { Length: > 0 } directory)
                    {
                        System.IO.Directory.CreateDirectory(directory);
                        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(render));
                        using var stream = System.IO.File.Create(System.IO.Path.Combine(directory, $"pin-{(int)width}.png"));
                        encoder.Save(stream);
                    }
                    var pixels = new byte[render.PixelWidth * render.PixelHeight * 4];
                    render.CopyPixels(pixels, render.PixelWidth * 4, 0);
                    for (var y = 18; y < render.PixelHeight - 18; y++)
                        for (var x = 16; x < render.PixelWidth - 16; x++)
                            Assert.InRange(pixels[(y * render.PixelWidth + x) * 4], 20, 28);
                }
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void WheelPacketsAreCoalescedAndHighResolutionDeltasRetainTheirMagnitude()
    {
        WpfTestHost.Invoke(() =>
        {
            var updates = new List<(double Steps, Point Pointer)>();
            using var pump = new WheelZoomFramePump((steps, pointer) => updates.Add((steps, pointer)));
            for (var i = 0; i < 120; i++) pump.Queue(1, new Point(i, 20));
            Assert.Empty(updates);
            pump.Flush();
            Assert.Single(updates);
            Assert.Equal(1, updates[0].Steps, 8);
            Assert.Equal(new Point(119, 20), updates[0].Pointer);
            pump.Queue(120, new Point()); pump.Queue(-120, new Point()); pump.Flush();
            Assert.Single(updates);
            pump.Queue(120, new Point()); pump.Cancel(); pump.Flush();
            Assert.Single(updates);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DoubleClickClosesBrowsingPinButLeavesEditingInputAlone(bool editing)
    {
        WpfTestHost.Invoke(() =>
        {
            var window = new PinnedImageWindow(new CapturedImage(new System.Drawing.Bitmap(160, 120)));
            var closed = false;
            window.Closed += (_, _) => closed = true;
            try
            {
                typeof(PinnedImageWindow).GetField("_isEditorMode", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, editing);
                var args = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                    { RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent };
                typeof(MouseButtonEventArgs).GetProperty(nameof(MouseButtonEventArgs.ClickCount))!.SetValue(args, 2);
                typeof(PinnedImageWindow).GetMethod("OnImageMouseLeftButtonDown", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [window, args]);
                Assert.Equal(!editing, closed);
                Assert.Equal(!editing, args.Handled);
            }
            finally
            {
                if (!closed)
                {
                    typeof(PinnedImageWindow).GetField("_isEditorMode", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, false);
                    window.Close();
                }
            }
        });
    }
}
