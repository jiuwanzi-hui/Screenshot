using System.Drawing;
using System.Drawing.Imaging;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using Screenshot.App.Capture;
using Forms = System.Windows.Forms;

namespace Screenshot.App.Tests;

public sealed class ScrollCaptureDisplayTests
{
    [DesktopFact]
    public void BorderOverTheTitleBarIsReplacedWithTheOriginalWindowPixels()
    {
        WpfTestHost.Invoke(() =>
        {
            var work = Forms.Screen.PrimaryScreen!.WorkingArea;
            using var source = new Forms.Form
            {
                StartPosition = Forms.FormStartPosition.Manual,
                Bounds = new Rectangle(work.X + 120, work.Y + 120, 400, 300),
                Text = "Scroll capture border test",
                BackColor = Color.White,
                TopMost = true,
                ShowInTaskbar = false,
            };
            var disableTransition = 1;
            _ = DwmSetWindowAttribute(source.Handle, 3, ref disableTransition, sizeof(int));
            source.Show();
            source.Refresh();
            // The top strip crosses the caption, outside the client DC.
            var bounds = new Rectangle(source.Left + 50, source.Top + 12, 200, 100);
            var region = new ScreenRegion(bounds.X, bounds.Y, bounds.Width, bounds.Height);
            using var backdrop = ForegroundWindowCaptureService.CaptureRegion(region);
            source.BackColor = Color.Tomato;
            source.Refresh();
            using var expected = ForegroundWindowCaptureService.CaptureRegion(region);
            using var border = new NativeSelectionFrameWindow();
            border.SetBorderColor(Color.Lime);
            border.Update(bounds);
            using var actual = ForegroundWindowCaptureService.CaptureScrollTargetRegion(
                new ScrollCaptureTarget(source.Handle, source.Handle, region, false)
                {
                    BorderRegions = NativeSelectionBorder.GetStrips(bounds)
                        .Select(strip => new ScreenRegion(strip.X, strip.Y, strip.Width, strip.Height)).ToArray(),
                    BorderBackdrop = (backdrop, region),
                });
            AssertSamePixels(expected, actual);
        });
    }

    [DesktopTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BorderIsVisibleInDesktopPixelsAndAbsentFromScrollFrames(bool touchesScreenEdges)
    {
        Forms.Form? source = null;
        CaptureOverlayWindow? overlay = null;
        ScrollCaptureSelection? selection = null;
        Task<ScrollCaptureSelection?>? selectionTask = null;
        var bounds = Forms.Screen.PrimaryScreen!.Bounds;
        using var page = CreatePage(bounds.Width, bounds.Height + 256);
        var offset = 0;
        var region = touchesScreenEdges
            ? new ScreenRegion(bounds.X, bounds.Y, bounds.Width, bounds.Height)
            : new ScreenRegion(bounds.X + 100, bounds.Y + 100, 220, 140);
        try
        {
            WpfTestHost.Invoke(() =>
            {
                source = new Forms.Form
                {
                    FormBorderStyle = Forms.FormBorderStyle.None,
                    StartPosition = Forms.FormStartPosition.Manual,
                    Bounds = bounds,
                    TopMost = true,
                    ShowInTaskbar = false,
                };
                source.Paint += (_, e) => e.Graphics.DrawImageUnscaled(page, 0, -offset);
                source.Show();
                source.Refresh();
            });
            WpfTestHost.Invoke(() =>
            {
                selectionTask = CaptureOverlayWindow.SelectForScrollCaptureAsync();
                overlay = Application.Current.Windows.OfType<CaptureOverlayWindow>().Last();
                overlay.UpdateLayout();
                var surface = (FrameworkElement)overlay.FindName("CaptureSurface");
                var first = surface.PointFromScreen(new System.Windows.Point(region.X, region.Y));
                var last = surface.PointFromScreen(new System.Windows.Point(region.X + region.Width, region.Y + region.Height));
                InvokeOverlay("UpdateSelectionBounds", new Rect(first, last));
                InvokeOverlay("PublishScrollCaptureSelection");
            });
            selection = await selectionTask!.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.NotNull(selection);
            Assert.Equal(region, selection.CaptureRegion);
            await selection.LockForScrollingAsync();

            var handle = IntPtr.Zero;
            WpfTestHost.Invoke(() => handle = source!.Handle);
            var target = new ScrollCaptureTarget(handle, handle, region, false)
            {
                BorderRegions = selection.BorderRegions,
                AllowContextCapture = selection.SupportsContextCapture,
            };
            using var context = ScrollCaptureContext.Create(target);
            if (!touchesScreenEdges)
            {
                Assert.NotNull(context);
                using var initial = ExpectedFrame();
                Assert.True(context.Seed(initial, target));
            }
            target = target with { Context = context };
            foreach (var nextOffset in new[] { 0, 16, 32, 16, 0 })
            {
                WpfTestHost.Invoke(() => { offset = nextOffset; source!.Refresh(); });
                using var frame = await Task.Run(() => ForegroundWindowCaptureService.CaptureScrollTargetRegion(target));
                using var expected = ExpectedFrame();
                AssertSamePixels(expected, frame);
                if (context is not null)
                    Assert.Equal(nextOffset, ScrollCapturePosition.Read(frame)?.Rows);

                // Read actual desktop pixels, not just IsWindowVisible. This
                // also catches capture exclusion hiding the border in a remote
                // viewer even though all native HWNDs still report visible.
                foreach (var strip in selection.BorderRegions)
                {
                    using var visible = ForegroundWindowCaptureService.CaptureRegion(strip);
                    var x = strip.Width / 2;
                    var y = strip.Height / 2;
                    var underneath = page.GetPixel(strip.X - bounds.X + x, strip.Y - bounds.Y + y + offset);
                    Assert.NotEqual(underneath.ToArgb(), visible.GetPixel(x, y).ToArgb());
                }
                Assert.All(selection.CaptureWindowHandles.Skip(2), borderHandle =>
                {
                    Assert.True(GetWindowDisplayAffinity(borderHandle, out var affinity));
                    Assert.Equal(0u, affinity);
                });
            }
        }
        finally
        {
            selection?.Dispose();
            WpfTestHost.Invoke(() => { overlay?.Close(); source?.Dispose(); });
        }

        Bitmap ExpectedFrame() => page.Clone(new Rectangle(region.X - bounds.X,
            region.Y - bounds.Y + offset, region.Width, region.Height), PixelFormat.Format32bppPArgb);

        void InvokeOverlay(string method, params object[] arguments) =>
            typeof(CaptureOverlayWindow).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(overlay, arguments);
    }

    private static Bitmap CreatePage(int width, int height)
    {
        var bitmap = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
        var random = new Random(71943);
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x += 8)
        {
            var b = (byte)random.Next(256);
            var g = (byte)random.Next(256);
            var r = (byte)random.Next(256);
            for (var column = x; column < Math.Min(width, x + 8); column++)
            {
                var index = (y * width + column) * 4;
                pixels[index] = b;
                pixels[index + 1] = g;
                pixels[index + 2] = r;
                pixels[index + 3] = 255;
            }
        }
        var data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
        try { Marshal.Copy(pixels, 0, data.Scan0, pixels.Length); }
        finally { bitmap.UnlockBits(data); }
        return bitmap;
    }

    private static void AssertSamePixels(Bitmap expected, Bitmap actual)
    {
        Assert.Equal(expected.Size, actual.Size);
        var bounds = new Rectangle(0, 0, expected.Width, expected.Height);
        var first = expected.LockBits(bounds, ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
        var second = actual.LockBits(bounds, ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
        try
        {
            var a = new byte[expected.Width * 4];
            var b = new byte[a.Length];
            for (var y = 0; y < expected.Height; y++)
            {
                Marshal.Copy(first.Scan0 + y * first.Stride, a, 0, a.Length);
                Marshal.Copy(second.Scan0 + y * second.Stride, b, 0, b.Length);
                for (var x = 0; x < a.Length; x++)
                    if (a[x] != b[x]) Assert.Fail($"Source pixel changed at ({x / 4}, {y}), channel {x % 4}: {a[x]} -> {b[x]}.");
            }
        }
        finally { expected.UnlockBits(first); actual.UnlockBits(second); }
    }

    [DllImport("user32.dll")]
    private static extern bool GetWindowDisplayAffinity(IntPtr handle, out uint affinity);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr handle, uint attribute, ref int value, int size);
}
