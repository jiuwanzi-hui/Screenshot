using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Screenshot.App.Core;
using Screenshot.App.Infrastructure;
using Screenshot.App.Presentation;

namespace Screenshot.App.Tests;

public sealed class ReferenceThemePaletteTests
{
    [Fact]
    public void ReferenceThemesKeepThreeLightAndThreeDarkWithReadableActionsAndSurfaces()
    {
        WpfTestHost.Invoke(() =>
        {
            var themes = new SettingsViewModel(AppSettings.CreateDefault()).ThemeOptions.Take(6)
                .Select(option => Enum.Parse<AppTheme>(option.Value)).ToArray();
            Assert.Equal(3, themes.Count(AppThemeManager.IsDarkTheme));
            foreach (var theme in themes)
            {
                var resources = new ResourceDictionary();
                AppThemeManager.ApplySettingsPalette(resources, theme);
                foreach (var (background, foreground) in new[]
                {
                    ("AppWindowBackgroundBrush", "AppTextSecondaryBrush"),
                    ("AppSidebarBackgroundBrush", "AppNavigationForegroundBrush"),
                    ("AppPanelBackgroundBrush", "AppControlForegroundBrush"),
                    ("AppInputBackgroundBrush", "AppControlForegroundBrush"),
                    ("AppPopupBackgroundBrush", "AppControlForegroundBrush"),
                    ("AppAccentBrush", "AppOnAccentBrush"),
                    ("AppAccentMutedBrush", "AppAccentForegroundBrush"),
                    ("AppAccentMutedBrush", "AppControlForegroundBrush"),
                    ("EditorToolbarButtonSelectedBackgroundBrush", "EditorToolbarIconBrush"),
                })
                {
                    var brush = Assert.IsAssignableFrom<Brush>(resources[background]);
                    Assert.True(brush.IsFrozen);
                    var text = Assert.IsType<SolidColorBrush>(resources[foreground]).Color;
                    var visual = new DrawingVisual();
                    using (var dc = visual.RenderOpen())
                    {
                        dc.DrawRectangle((Brush)resources["AppPanelBackgroundBrush"], null, new Rect(0, 0, 64, 64));
                        dc.DrawRectangle(brush, null, new Rect(0, 0, 64, 64));
                    }
                    var bitmap = new RenderTargetBitmap(64, 64, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(visual);
                    var pixels = new byte[64 * 64 * 4];
                    bitmap.CopyPixels(pixels, 64 * 4, 0);
                    var minimum = double.MaxValue;
                    for (var pixel = 0; pixel < pixels.Length; pixel += 4)
                    {
                        Assert.Equal(255, pixels[pixel + 3]);
                        minimum = Math.Min(minimum, Contrast(text, Color.FromRgb(pixels[pixel + 2], pixels[pixel + 1], pixels[pixel])));
                    }
                    Assert.True(minimum >= 4.5, $"{theme}/{background}/{foreground}: {minimum:F2}");
                }
            }
        });
    }

    private static double Contrast(Color a, Color b)
    {
        static double Linear(byte x) => x / 255d <= .04045 ? x / 255d / 12.92 : Math.Pow((x / 255d + .055) / 1.055, 2.4);
        static double Luminance(Color x) => .2126 * Linear(x.R) + .7152 * Linear(x.G) + .0722 * Linear(x.B);
        return (Math.Max(Luminance(a), Luminance(b)) + .05) / (Math.Min(Luminance(a), Luminance(b)) + .05);
    }
}
