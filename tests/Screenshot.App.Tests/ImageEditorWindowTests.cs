using System.IO;
using System.Windows;
using System.Windows.Media;
using Screenshot.App.Capture;
using Screenshot.App.Core;
using Screenshot.App.Editor;
using Screenshot.App.Infrastructure;

namespace Screenshot.App.Tests;

[Collection(GlobalInputTestGroup.Name)]
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

    [Fact]
    public void EditorChromeFollowsLightAndDarkApplicationThemes()
    {
        WpfTestHost.Invoke(() =>
        {
            using var themeManager = new AppThemeManager();
            themeManager.Apply(AppTheme.Light);
            using var image = new CapturedImage(new System.Drawing.Bitmap(4, 4));
            var window = new ImageEditorWindow(
                image.Clone(),
                Path.GetTempPath());

            try
            {
                window.Show();
                window.UpdateLayout();
                AssertBrushColor(window.Background, "#EEF7F6");
                AssertBrushColor(
                    Assert.IsType<System.Windows.Controls.Border>(
                        window.FindName("EditorShell")).Background,
                    "#FCFEFE");
                AssertBrushColor(
                    Assert.IsType<System.Windows.Controls.Border>(
                        window.FindName("EditorToolbarPanel")).Background,
                    "#F4FAF9");

                themeManager.Apply(AppTheme.Dark);
                window.UpdateLayout();
                AssertBrushColor(window.Background, "#102027");
                AssertBrushColor(
                    Assert.IsType<System.Windows.Controls.Border>(
                        window.FindName("EditorShell")).Background,
                    "#F212252C");
                AssertBrushColor(
                    Assert.IsType<System.Windows.Controls.Border>(
                        window.FindName("EditorToolbarPanel")).Background,
                    "#D9183037");
            }
            finally
            {
                window.Close();
                themeManager.Apply(AppTheme.Light);
            }
        });
    }

    private static void AssertBrushColor(Brush brush, string expected)
    {
        var solidBrush = Assert.IsType<SolidColorBrush>(brush);
        Assert.Equal(
            (Color)ColorConverter.ConvertFromString(expected),
            solidBrush.Color);
    }
}
