using System.IO;
using Screenshot.App.Capture;
using Screenshot.App.Editor;

namespace Screenshot.App.Tests;

public sealed class ImageEditorWindowTests
{
    [Fact]
    public void ShowsAndClosesAnEditorWindow()
    {
        var wasVisible = false;
        var wasClosed = false;

        WpfTestHost.Invoke(() =>
        {
            var virtualDesktop = VirtualScreen.GetBounds();
            var image = ScreenCaptureService.Capture(
                new ScreenRegion(virtualDesktop.X, virtualDesktop.Y, 1, 1));
            var window = new ImageEditorWindow(image, Path.GetTempPath());

            window.Show();
            wasVisible = window.IsVisible;
            window.Close();
            wasClosed = !window.IsVisible;
        });

        Assert.True(wasVisible);
        Assert.True(wasClosed);
    }
}
