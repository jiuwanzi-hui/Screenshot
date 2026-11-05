using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using Screenshot.App.Capture;
using Screenshot.App.Core;
using Screenshot.App.Infrastructure;
using Screenshot.App.Pin;
using Forms = System.Windows.Forms;

namespace Screenshot.App.Tests;

public sealed class MenuCaptureDesktopTests
{
    [DesktopTheory]
    [InlineData(9f, false)]
    [InlineData(13.5f, true)]
    [InlineData(18f, true)]
    public void TrayMenuKeepsItsMeasuredEdgeAtTheRequestedAnchor(float fontSize, bool hasPins)
    {
        WpfTestHost.Invoke(() =>
        {
            using var tray = new TrayIconService(AppTheme.AuroraMist);
            using var font = new Font("Microsoft YaHei UI", fontSize);
            tray.UpdatePinnedImageCommands(hasPins, false);
            var menu = tray.ContextMenuForTesting;
            menu.Font = font;
            var show = typeof(Forms.ContextMenuStrip).GetMethod("ShowInTaskbar",
                BindingFlags.Instance | BindingFlags.NonPublic)!;
            foreach (var screen in Forms.Screen.AllScreens)
            {
                var bounds = screen.Bounds;
                var point = new Point(bounds.Left + bounds.Width * 3 / 4,
                    bounds.Top + bounds.Height * 3 / 4);
                show.Invoke(menu, [point.X, point.Y]);
                try
                {
                    Assert.InRange(Math.Abs(menu.Right - point.X), 0, 1);
                    Assert.InRange(Math.Abs(menu.Bottom - point.Y), 0, 1);
                    Assert.True(menu.Width >= menu.GetPreferredSize(Size.Empty).Width);
                    Assert.True(bounds.Contains(menu.Bounds));
                }
                finally { menu.Close(); }
            }
        });
    }

    [DesktopTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LayeredMenuPixelsSurviveTheOverlayHandoffWithoutPrintingOrActivation(bool usePreCapture)
    {
        Forms.Form? backdrop = null;
        PopupProbe? popup = null;
        CaptureOverlayWindow? overlay = null;
        PinnedImageManager? pins = null;
        Rectangle bounds = default;
        try
        {
            WpfTestHost.Invoke(() =>
            {
                var work = Forms.Screen.PrimaryScreen!.WorkingArea;
                backdrop = new Forms.Form
                {
                    StartPosition = Forms.FormStartPosition.Manual,
                    Bounds = new Rectangle(work.Left + 100, work.Top + 100, 300, 240),
                    FormBorderStyle = Forms.FormBorderStyle.None,
                    BackColor = Color.Blue, TopMost = true, ShowInTaskbar = false,
                };
                backdrop.Show();
                popup = new PopupProbe
                {
                    StartPosition = Forms.FormStartPosition.Manual,
                    Bounds = new Rectangle(backdrop.Left + 30, backdrop.Top + 30, 180, 130),
                    FormBorderStyle = Forms.FormBorderStyle.None,
                    BackColor = Color.Red, Opacity = 0.75, TopMost = true, ShowInTaskbar = false,
                };
                popup.Show(backdrop);
                bounds = popup.Bounds;
            });
            await Task.Delay(150);
            _ = DwmFlush();
            var foreground = GetForegroundWindow();
            var region = new ScreenRegion(bounds.Left + 40, bounds.Top + 40, 20, 20);
            using var direct = ScreenCaptureService.CaptureIncludingLayeredWindows(region);
            AssertPurple(direct.Bitmap.GetPixel(5, 5));
            var capture = typeof(GlobalHotKeyManager).GetMethod("CaptureTransientUiScreen",
                BindingFlags.Static | BindingFlags.NonPublic)!;
            using var desktop = Assert.IsType<CapturedImage>(capture.Invoke(null, null));
            var origin = desktop.SourceRegion!.Value;
            AssertPurple(desktop.Bitmap.GetPixel(region.X - origin.X + 5, region.Y - origin.Y + 5));
            var previewPixel = new byte[4];
            desktop.Preview.CopyPixels(new System.Windows.Int32Rect(
                region.X - origin.X + 5, region.Y - origin.Y + 5, 1, 1), previewPixel, 4, 0);
            Assert.Equal(255, previewPixel[3]);
            Assert.Equal(foreground, GetForegroundWindow());
            WpfTestHost.Invoke(() =>
            {
                Assert.True(popup!.Visible);
                Assert.Equal(0, popup.PrintMessages);
                pins = new PinnedImageManager();
                overlay = CaptureOverlayWindow.ShowInteractive(new CaptureOverlayOptions
                {
                    SaveDirectory = System.IO.Path.GetTempPath(), KeepHistory = false, HistoryLimit = 0,
                    HistoryService = new CaptureHistoryService(), PinnedImageManager = pins,
                    StartOcrAsync = image => { image.Dispose(); return Task.CompletedTask; },
                    DeferInitialScreenCapture = true,
                }, initialScreenSnapshot: usePreCapture ? desktop.Clone() : null);
            });
            var deadline = DateTime.UtcNow.AddSeconds(5);
            var ready = false;
            while (!ready && DateTime.UtcNow < deadline)
            {
                await Task.Delay(20);
                WpfTestHost.Invoke(() => ready = overlay!.Opacity == 1 &&
                    typeof(CaptureOverlayWindow).GetField("_screenSnapshot", BindingFlags.Instance | BindingFlags.NonPublic)!
                        .GetValue(overlay) is CapturedImage);
            }
            Assert.True(ready, "The capture overlay did not publish its first frame.");
            WpfTestHost.Invoke(() =>
            {
                var displayed = (CapturedImage)typeof(CaptureOverlayWindow)
                    .GetField("_screenSnapshot", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(overlay)!;
                AssertPurple(displayed.Bitmap.GetPixel(region.X - origin.X + 5, region.Y - origin.Y + 5));
                Assert.True(popup!.Visible);
                Assert.Equal(0, popup.PrintMessages);
            });
            Assert.Equal(foreground, GetForegroundWindow());
        }
        finally
        {
            WpfTestHost.Invoke(() =>
            {
                overlay?.Close(); overlay?.Dispose(); pins?.Dispose(); popup?.Dispose(); backdrop?.Dispose();
            });
        }
    }

    private static void AssertPurple(Color pixel)
    {
        Assert.InRange((int)pixel.R, 180, 200);
        Assert.InRange((int)pixel.G, 0, 5);
        Assert.InRange((int)pixel.B, 55, 75);
    }

    private sealed class PopupProbe : Forms.Form
    {
        public int PrintMessages { get; private set; }
        protected override void WndProc(ref Forms.Message message)
        {
            if (message.Msg is 0x0317 or 0x0318) PrintMessages++;
            base.WndProc(ref message);
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmFlush();

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
}
