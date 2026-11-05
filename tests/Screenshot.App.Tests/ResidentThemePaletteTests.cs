using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Screenshot.App.Core;
using Screenshot.App.Infrastructure;

namespace Screenshot.App.Tests;

public sealed class ResidentThemePaletteTests
{
    [Fact]
    public void ResidentGlassKeepsThePatternWithoutFullSizeRasterTextures()
    {
        WpfTestHost.Invoke(() =>
        {
            var resident = new ResourceDictionary();
            var feature = new ResourceDictionary();
            AppThemeManager.ApplySettingsPalette(resident, AppTheme.GlassInk, residentShell: true);
            AppThemeManager.ApplySettingsPalette(feature, AppTheme.GlassInk);
            foreach (var key in new[] { "AppWindowBackgroundBrush", "AppGlassBackgroundBrush", "AppFloatingSurfaceBrush" })
            {
                var lightweight = Assert.IsType<DrawingBrush>(resident[key]);
                Assert.True(lightweight.IsFrozen);
                var expected = Render((System.Windows.Media.Brush)feature[key]);
                var actual = Render(lightweight);
                var meanError = expected.Zip(actual, (a, b) => Math.Abs(a - b)).Average();
                Assert.True(meanError < 5, $"{key} visual difference: {meanError:F2}");
            }
        });
    }

    private static byte[] Render(System.Windows.Media.Brush brush)
    {
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen()) drawing.DrawRectangle(brush, null, new Rect(0, 0, 64, 64));
        var bitmap = new RenderTargetBitmap(64, 64, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var pixels = new byte[64 * 64 * 4];
        bitmap.CopyPixels(pixels, 64 * 4, 0);
        return pixels;
    }
}
