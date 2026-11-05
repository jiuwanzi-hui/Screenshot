using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Screenshot.App.Capture;
using Screenshot.App.Core;
using Screenshot.App.Infrastructure;

namespace Screenshot.App.Tests;

public sealed class SelectionSizeBadgeAppearanceTests
{
    [Theory]
    [InlineData(AppTheme.AuroraMist)]
    [InlineData(AppTheme.CoralSky)]
    [InlineData(AppTheme.GinkgoPaper)]
    [InlineData(AppTheme.ForestNight)]
    [InlineData(AppTheme.ObsidianGold)]
    [InlineData(AppTheme.NeonDeep)]
    [InlineData(AppTheme.GlassMist)]
    [InlineData(AppTheme.GlassInk)]
    public void BadgeStaysCompactAndUsesTheSameThemeForLiveAndEditorViews(AppTheme theme)
    {
        WpfTestHost.Invoke(() =>
        {
            using var overlay = new CaptureOverlayWindow(null, isStartupPrewarm: true, deferInitialScreenCapture: true);
            try
            {
                AppThemeManager.ApplySettingsPalette(overlay.Resources, theme);
                // Clone the actual control for off-screen layout, keeping
                // the capture owner's selection callbacks out of the render.
                var original = Assert.IsType<Border>(overlay.FindName("SelectionSizeBadge"));
                var badge = (Border)System.Windows.Markup.XamlReader.Parse(System.Windows.Markup.XamlWriter.Save(original));
                var text = Assert.IsType<TextBlock>(badge.Child);
                badge.Resources.MergedDictionaries.Add(overlay.Resources);
                badge.Visibility = Visibility.Visible;
                text.Text = "92 × 24";
                badge.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                Assert.InRange(badge.DesiredSize.Width, 52, 75);
                // Layout rounding at 125% can add a fraction of a DIP.
                Assert.InRange(badge.DesiredSize.Height, 21, 23);
                var smallWidth = badge.DesiredSize.Width;
                text.Text = "16384 × 10000";
                badge.InvalidateMeasure();
                badge.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                badge.Arrange(new Rect(new Point(), badge.DesiredSize));
                badge.UpdateLayout();
                Assert.True(badge.DesiredSize.Width > smallWidth,
                    $"Width did not grow: {smallWidth} -> {badge.DesiredSize.Width}");
                Assert.True(text.ActualHeight <= badge.ActualHeight - 2);
                Assert.True(text.ActualWidth <= badge.ActualWidth - 15);

                var backgroundColors = badge.Background is SolidColorBrush solid
                    ? new[] { solid.Color }
                    : Assert.IsType<LinearGradientBrush>(badge.Background).GradientStops.Select(stop => stop.Color).ToArray();
                var foreground = Assert.IsType<SolidColorBrush>(text.Foreground);
                Assert.All(backgroundColors, color =>
                {
                    Assert.Equal(255, color.A);
                    var light = Math.Max(Luminance(color), Luminance(foreground.Color));
                    var dark = Math.Min(Luminance(color), Luminance(foreground.Color));
                    Assert.True((light + .05) / (dark + .05) >= 4.5);
                });
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
                typeof(CaptureOverlayWindow).GetMethod("SyncNativeSizeBadgeTheme", flags)!.Invoke(overlay, null);
                var native = (NativeSelectionSizeWindow)typeof(CaptureOverlayWindow).GetField("_nativeSelectionSize", flags)!.GetValue(overlay)!;
                var nativeBackground = (System.Drawing.Color)typeof(NativeSelectionSizeWindow).GetField("_backgroundColor", flags)!.GetValue(native)!;
                Assert.Equal(backgroundColors[0].R, nativeBackground.R);
                Assert.Equal(backgroundColors[0].G, nativeBackground.G);
                Assert.Equal(backgroundColors[0].B, nativeBackground.B);

                // Optional off-screen visual review; no desktop windows or input.
                if (Environment.GetEnvironmentVariable("SNAPCUT_BADGE_PREVIEW") is { Length: > 0 } directory)
                {
                    Directory.CreateDirectory(directory);
                    text.Text = "92 × 24";
                    badge.InvalidateMeasure();
                    badge.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                    badge.Arrange(new Rect(new Point(), badge.DesiredSize));
                    badge.UpdateLayout();
                    var bitmap = new RenderTargetBitmap((int)Math.Ceiling(badge.ActualWidth * 3),
                        (int)Math.Ceiling(badge.ActualHeight * 3), 288, 288, PixelFormats.Pbgra32);
                    bitmap.Render(badge);
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var stream = File.Create(Path.Combine(directory, theme + ".png"));
                    encoder.Save(stream);
                }
            }
            finally { overlay.Close(); }
        });
    }

    private static double Luminance(Color color)
    {
        static double Linear(byte value) => value / 255d <= .04045
            ? value / 255d / 12.92 : Math.Pow((value / 255d + .055) / 1.055, 2.4);
        return .2126 * Linear(color.R) + .7152 * Linear(color.G) + .0722 * Linear(color.B);
    }
}
