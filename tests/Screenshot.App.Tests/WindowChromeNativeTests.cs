using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Screenshot.App.Core;
using Screenshot.App.Infrastructure;
using Screenshot.App.Presentation;
using Screenshot.App.Text;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace Screenshot.App.Tests;

public sealed class WindowChromeNativeTests
{
    [DesktopTheory]
    [InlineData(AppTheme.GlassMist)]
    [InlineData(AppTheme.GlassInk)]
    [InlineData(AppTheme.NeonDeep)]
    public void ResultClientFillsTheNativeWindowBeforeAndAfterResizing(AppTheme theme)
    {
        WpfTestHost.Invoke(() =>
        {
            var window = new RecognitionResultPopupWindow("文字识别", "缩放后标题栏应始终贴合窗口顶部。", null)
            { ShowActivated = false };
            AppThemeManager.ApplySettingsPalette(window.Resources, theme);
            try
            {
                window.Show();
                var shell = Assert.IsType<Border>(window.FindName("Shell"));
                foreach (var size in new[] { new Size(480, 350), new Size(640, 460), new Size(320, 280) })
                {
                    window.Width = size.Width;
                    window.Height = size.Height;
                    window.UpdateLayout();
                    window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                    UiTransitions.Finish(shell);
                    LiquidGlassChrome.Apply(window, theme);
                    if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621))
                    {
                        Assert.Equal(0, DwmGetWindowAttribute(new WindowInteropHelper(window).Handle,
                            38, out var backdrop, sizeof(int)));
                        Assert.Equal(1, backdrop);
                    }
                    Assert.True(MonitorGeometryService.TryGetWindowBounds(window, out var native));
                    var clientOrigin = shell.PointToScreen(new Point());
                    Assert.InRange(Math.Abs(clientOrigin.X - native.Left), 0, 1);
                    Assert.InRange(Math.Abs(clientOrigin.Y - native.Top), 0, 1);
                    Assert.InRange(Math.Abs(shell.ActualWidth - size.Width), 0, 1);
                    Assert.InRange(Math.Abs(shell.ActualHeight - size.Height), 0, 1);
                }
            }
            finally { window.Close(); }
        });
    }

    [DesktopFact]
    public void PinKeepsOnlyTheImageAndRetainsMenuCommandsAtSmallSizes()
    {
        WpfTestHost.Invoke(() =>
        {
            using var image = new Screenshot.App.Capture.CapturedImage(new System.Drawing.Bitmap(500, 320));
            var window = new Screenshot.App.Pin.PinnedImageWindow(image.Clone()) { ShowActivated = false };
            try
            {
                window.Show();
                var handle = new WindowInteropHelper(window).Handle;
                Assert.Equal(ResizeMode.NoResize, window.ResizeMode);
                Assert.Equal(0, GetWindowLong(handle, -16) & 0x40000); // WS_THICKFRAME
                window.Width = 160;
                window.Height = 100;
                window.UpdateLayout();
                Assert.Null(window.FindName("PinnedHeader"));
                Assert.IsType<MenuItem>(window.FindName("EditMenuItem"));
                Assert.IsType<Border>(window.FindName("ImageOutline"));
                Assert.True(MonitorGeometryService.TryGetWindowBounds(window, out var native));
                var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(window);
                Assert.InRange(Math.Abs(native.Width - 160 * dpi.DpiScaleX), 0, 1);
                Assert.InRange(Math.Abs(native.Height - 100 * dpi.DpiScaleY), 0, 1);
                window.Width = 500;
                window.UpdateLayout();
                var shell = Assert.IsType<Border>(window.FindName("PinnedShell"));
                Assert.Contains(shell.ContextMenu.Items.OfType<MenuItem>(), item => Equals(item.Header, "复制图片"));
            }
            finally { window.Close(); }
        });
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr handle, int attribute, out int value, int size);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr handle, int index);
}
