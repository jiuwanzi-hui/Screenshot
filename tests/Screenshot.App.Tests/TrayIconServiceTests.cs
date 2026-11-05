using Screenshot.App.Core;
using Screenshot.App.Infrastructure;

namespace Screenshot.App.Tests;

public sealed class TrayIconServiceTests
{
    [Theory]
    [InlineData(AppTheme.AuroraMist)]
    [InlineData(AppTheme.CoralSky)]
    [InlineData(AppTheme.GinkgoPaper)]
    [InlineData(AppTheme.ForestNight)]
    [InlineData(AppTheme.ObsidianGold)]
    [InlineData(AppTheme.NeonDeep)]
    public void ContextMenuUsesApplicationThemeColorsAndComfortableSpacing(AppTheme theme)
    {
        using var tray = new TrayIconService(theme);
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
        Assert.Contains(
            menu.Items.OfType<System.Windows.Forms.ToolStripMenuItem>(),
            item => item.Text == "历史查看");
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

        tray.ApplyTheme(AppThemeManager.IsDarkTheme(theme) ? AppTheme.CoralSky : AppTheme.ForestNight);

        Assert.NotEqual(darkBackground, menu.BackColor);
        Assert.Equal(AppThemeManager.IsDarkTheme(theme), menu.BackColor.GetBrightness() > darkBackground.GetBrightness());
    }

    [Fact]
    public void VideoRecordingMenuItemRaisesRequest()
    {
        using var tray = new TrayIconService(AppTheme.AuroraMist);
        var requested = false;
        tray.VideoRecordingRequested += (_, _) => requested = true;
        var item = Assert.Single(
            tray.ContextMenuForTesting.Items
                .OfType<System.Windows.Forms.ToolStripMenuItem>()
                .Where(candidate => candidate.Text == "录制视频"));

        item.PerformClick();

        Assert.True(requested);
    }

    [Theory]
    [InlineData(AppTheme.GlassMist)]
    [InlineData(AppTheme.GlassInk)]
    public void ThemeMenuKeepsItsBackgroundAndHoverReadable(AppTheme theme)
    {
        WpfTestHost.Invoke(() =>
        {
            using var tray = new TrayIconService(theme);
            var menu = tray.ContextMenuForTesting;
            Assert.Equal(255, menu.BackColor.A);
            Assert.True(GetContrastRatio(menu.ForeColor, menu.BackColor) >= 4.5);
            Assert.True(GetContrastRatio(tray.HoverForegroundForTesting, tray.HoverBackgroundForTesting) >= 4.5);
            menu.Size = new System.Drawing.Size(180, 240);
            using var bitmap = new System.Drawing.Bitmap(menu.Width, menu.Height);
            using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
                menu.Renderer.DrawToolStripBackground(new System.Windows.Forms.ToolStripRenderEventArgs(graphics, menu));
            var first = bitmap.GetPixel(20, 20);
            if (theme == AppTheme.GlassInk)
                Assert.NotEqual(first, bitmap.GetPixel(menu.Width - 20, menu.Height - 20));
            for (var y = 10; y < menu.Height - 10; y += 10)
                for (var x = 10; x < menu.Width - 10; x += 10)
                {
                    var pixel = bitmap.GetPixel(x, y);
                    if (theme == AppTheme.GlassMist)
                        Assert.Equal(first, pixel);
                    Assert.Equal(255, pixel.A);
                    Assert.True(GetContrastRatio(menu.ForeColor, pixel) >= 4.5);
                }
            tray.ApplyTheme(AppTheme.ForestNight);
            Assert.Equal(255, menu.BackColor.A);
            tray.ApplyTheme(theme);
            Assert.Equal(255, menu.BackColor.A);
        });
    }

    [Fact]
    public void PinnedImageCommandsMatchCurrentVisibilityState()
    {
        using var tray = new TrayIconService(AppTheme.AuroraMist);
        var items = tray.ContextMenuForTesting.Items
            .OfType<System.Windows.Forms.ToolStripMenuItem>()
            .ToArray();
        var hide = Assert.Single(items.Where(item => item.Text == "最小化全部钉图"));

        tray.UpdatePinnedImageCommands(
            hasPinnedImages: false,
            hasHiddenPinnedImages: false);
        Assert.False(hide.Available);

        tray.UpdatePinnedImageCommands(
            hasPinnedImages: true,
            hasHiddenPinnedImages: false);
        Assert.True(hide.Available);

        tray.UpdatePinnedImageCommands(
            hasPinnedImages: true,
            hasHiddenPinnedImages: true);
        Assert.True(hide.Available);
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
