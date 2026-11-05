using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Screenshot.App.Infrastructure;
using Screenshot.App.Presentation;
using Rectangle = System.Drawing.Rectangle;

namespace Screenshot.App.Tests;

public sealed class WindowWorkAreaTests
{
    [Theory]
    [InlineData(0, 0, 2560, 1440, 0, 0, 2560, 1392)]
    [InlineData(-1920, 0, 1920, 1080, -1880, 0, 1880, 1080)]
    [InlineData(0, -2160, 3840, 2160, 0, -2112, 3840, 2112)]
    [InlineData(2560, 0, 1920, 1080, 2560, 0, 1872, 1080)]
    public void MaximumBoundsExcludeTaskbarsOnAnyMonitorEdge(int x, int y, int width, int height,
        int workX, int workY, int workWidth, int workHeight)
    {
        var monitor = new Rectangle(x, y, width, height);
        var work = new Rectangle(workX, workY, workWidth, workHeight);
        var result = WindowWorkArea.GetMaximizedBounds(monitor, work);
        result.Offset(monitor.Location);
        Assert.Equal(work, result);
        Assert.True(monitor.Contains(result));
    }

    [Fact]
    public void CustomChromeAnswersNativeMaximizeRequestWithoutShowingAWindow()
    {
        WpfTestHost.Invoke(() =>
        {
            var window = new Window { Width = 980, Height = 820, MinWidth = 480, MinHeight = 360, Content = new Grid() };
            try
            {
                WindowShell.Attach(window);
                var handle = new WindowInteropHelper(window).EnsureHandle();
                var screen = System.Windows.Forms.Screen.FromHandle(handle);
                var result = new WindowWorkArea.MinMaxInfo();
                _ = SendMessage(handle, 0x0024, IntPtr.Zero, ref result);
                Assert.Equal(screen.WorkingArea.Width, result.MaxSize.X);
                Assert.Equal(screen.WorkingArea.Height, result.MaxSize.Y);
                Assert.Equal(screen.WorkingArea.Left - screen.Bounds.Left, result.MaxPosition.X);
                Assert.Equal(screen.WorkingArea.Top - screen.Bounds.Top, result.MaxPosition.Y);
                var dpi = HwndSource.FromHwnd(handle)!.CompositionTarget.TransformToDevice;
                Assert.Equal(Math.Min(screen.WorkingArea.Width, Math.Ceiling(480 * dpi.M11)), result.MinTrackSize.X);
                Assert.Equal(Math.Min(screen.WorkingArea.Height, Math.Ceiling(360 * dpi.M22)), result.MinTrackSize.Y);
                Assert.False(window.IsVisible);
            }
            finally { window.Close(); }
        });
    }

    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, ref WindowWorkArea.MinMaxInfo limits);
}
