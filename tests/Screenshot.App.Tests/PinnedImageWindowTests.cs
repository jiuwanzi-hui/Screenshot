using Screenshot.App.Capture;
using Screenshot.App.Pin;

namespace Screenshot.App.Tests;

public sealed class PinnedImageWindowTests
{
    [Fact]
    public void IsTopmostAndHiddenFromTheTaskbar()
    {
        var isVisible = false;
        var isTopmost = false;
        var isHiddenFromTaskbar = false;

        WpfTestHost.Invoke(() =>
        {
            var virtualDesktop = VirtualScreen.GetBounds();
            CapturedImage? image = ScreenCaptureService.Capture(
                new ScreenRegion(virtualDesktop.X, virtualDesktop.Y, 1, 1));
            PinnedImageWindow? window = null;

            try
            {
                window = new PinnedImageWindow(image);
                image = null;
                window.Show();
                isVisible = window.IsVisible;
                isTopmost = window.Topmost;
                isHiddenFromTaskbar = !window.ShowInTaskbar;
            }
            finally
            {
                window?.Close();
                image?.Dispose();
            }
        });

        Assert.True(isVisible);
        Assert.True(isTopmost);
        Assert.True(isHiddenFromTaskbar);
    }
}
