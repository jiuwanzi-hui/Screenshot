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
        Assert.Contains("RoundedContextMenuStrip", menu.GetType().Name);
        Assert.Equal(8, TrayIconService.CornerRadiusForTesting);
        Assert.NotEqual(IntPtr.Zero, menu.Handle);
        Assert.NotNull(menu.Region);
        Assert.Contains(
            menu.Items.OfType<System.Windows.Forms.ToolStripMenuItem>(),
            item => item.Text == "录制视频");
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

    [Fact]
    public void VideoRecordingMenuItemRaisesRequest()
    {
        using var tray = new TrayIconService(AppTheme.Light);
        var requested = false;
        tray.VideoRecordingRequested += (_, _) => requested = true;
        var item = Assert.Single(
            tray.ContextMenuForTesting.Items
                .OfType<System.Windows.Forms.ToolStripMenuItem>()
                .Where(candidate => candidate.Text == "录制视频"));

        item.PerformClick();

        Assert.True(requested);
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
