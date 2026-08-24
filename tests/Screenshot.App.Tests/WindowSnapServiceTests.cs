using System.Windows;
using Screenshot.App.Capture;

namespace Screenshot.App.Tests;

public sealed class WindowSnapServiceTests
{
    [Theory]
    [InlineData(0x000800A8u, true, 255, 0x00000002u, true)]
    [InlineData(0x08080080u, true, 0, 0x00000002u, true)]
    [InlineData(0x00000100u, false, 255, 0u, false)]
    public void TransparentSystemOverlaysAreNotSnapCandidates(
        uint extendedStyle,
        bool hasLayeredAttributes,
        byte alpha,
        uint layeredFlags,
        bool expected)
    {
        Assert.Equal(
            expected,
            WindowSnapService.IsTransparentOverlayStyle(
                extendedStyle,
                hasLayeredAttributes,
                alpha,
                layeredFlags));
    }

    [Fact]
    public void FindsTheTopmostWindowUnderAPhysicalScreenPoint()
    {
        WpfTestHost.Invoke(() =>
        {
            var window = new Window
            {
                Width = 240,
                Height = 150,
                Left = 260,
                Top = 180,
                Topmost = true,
                WindowStyle = WindowStyle.None,
            };

            try
            {
                window.Show();
                window.Activate();
                window.UpdateLayout();
                var center = window.PointToScreen(new Point(120, 75));

                var found = WindowSnapService.TryGetWindowRegionAt(
                    (int)Math.Round(center.X),
                    (int)Math.Round(center.Y),
                    excludedWindow: IntPtr.Zero,
                    VirtualScreen.GetBounds(),
                    out var region);

                Assert.True(found);
                Assert.True(region.Contains(
                    (int)Math.Round(center.X),
                    (int)Math.Round(center.Y)));
                Assert.True(region.Width >= 200);
                Assert.True(region.Height >= 100);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void PrefersTheFrontWindowWhenWindowsOverlap()
    {
        WpfTestHost.Invoke(() =>
        {
            var backWindow = new Window
            {
                Width = 620,
                Height = 420,
                Left = 180,
                Top = 120,
                Topmost = true,
                WindowStyle = WindowStyle.None,
            };
            var frontWindow = new Window
            {
                Width = 260,
                Height = 180,
                Left = 300,
                Top = 210,
                Topmost = true,
                WindowStyle = WindowStyle.None,
            };

            try
            {
                backWindow.Show();
                frontWindow.Show();
                frontWindow.Activate();
                frontWindow.UpdateLayout();
                var center = frontWindow.PointToScreen(new Point(130, 90));

                var found = WindowSnapService.TryGetWindowRegionAt(
                    (int)Math.Round(center.X),
                    (int)Math.Round(center.Y),
                    excludedWindow: IntPtr.Zero,
                    VirtualScreen.GetBounds(),
                    out var region);

                Assert.True(found);
                Assert.InRange(region.Width, 240, 360);
                Assert.InRange(region.Height, 160, 280);
            }
            finally
            {
                frontWindow.Close();
                backWindow.Close();
            }
        });
    }
}
