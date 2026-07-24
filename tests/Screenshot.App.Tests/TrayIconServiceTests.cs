using Screenshot.App.Core;
using Screenshot.App.Infrastructure;

namespace Screenshot.App.Tests;

public sealed class TrayIconServiceTests
{
    [Fact]
    public void ContextMenuUsesApplicationThemeColorsAndComfortableSpacing()
    {
        using var tray = new TrayIconService(AppTheme.Dark);
        var menu = tray.ContextMenuForTesting;
        var darkBackground = menu.BackColor;

        Assert.False(menu.ShowImageMargin);
        Assert.IsAssignableFrom<System.Windows.Forms.ToolStripProfessionalRenderer>(
            menu.Renderer);
        Assert.Contains("TrayMenuRenderer", menu.Renderer.GetType().Name);
        Assert.Equal(
            tray.HoverForegroundForTesting,
            menu.Items.OfType<System.Windows.Forms.ToolStripMenuItem>().First().ForeColor);
        Assert.NotEqual(
            System.Drawing.SystemColors.Highlight,
            tray.HoverBackgroundForTesting);
        Assert.True(
            GetContrastRatio(
                tray.HoverForegroundForTesting,
                tray.HoverBackgroundForTesting) >= 4.5);
        Assert.All(
            menu.Items
                .OfType<System.Windows.Forms.ToolStripMenuItem>(),
            item => Assert.True(item.Padding.Top >= 6));

        tray.ApplyTheme(AppTheme.Light);

        Assert.NotEqual(darkBackground, menu.BackColor);
        Assert.True(menu.BackColor.GetBrightness() > darkBackground.GetBrightness());
    }

    private static double GetContrastRatio(
        System.Drawing.Color foreground,
        System.Drawing.Color background)
    {
        var lighter = Math.Max(GetRelativeLuminance(foreground), GetRelativeLuminance(background));
        var darker = Math.Min(GetRelativeLuminance(foreground), GetRelativeLuminance(background));
        return (lighter + 0.05) / (darker + 0.05);
    }

    private static double GetRelativeLuminance(System.Drawing.Color color)
    {
        static double Linearize(byte component)
        {
            var value = component / 255d;
            return value <= 0.04045
                ? value / 12.92
                : Math.Pow((value + 0.055) / 1.055, 2.4);
        }

        return
            (0.2126 * Linearize(color.R)) +
            (0.7152 * Linearize(color.G)) +
            (0.0722 * Linearize(color.B));
    }
}
