using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Screenshot.App.Core;
using Screenshot.App.Infrastructure;
using Screenshot.App.Presentation;
using Screenshot.App.Text;

namespace Screenshot.App.Tests;

public sealed class ThemePresentationTests
{
    private static readonly string[] ToolbarCharacters = ["文", "取", "译", "隐"];
    private static readonly string[] FloatingForegroundKeys = ["AppTextPrimaryBrush", "AppTextSecondaryBrush", "EditorToolbarIconBrush"];

    [Fact]
    public void BrandingUsesTheHighResolutionFrameInsteadOfUpscalingTheTrayIcon()
    {
        WpfTestHost.Invoke(() =>
        {
            var source = Assert.IsAssignableFrom<System.Windows.Media.Imaging.BitmapSource>(AppBrandIcon.Source);
            Assert.True(source.PixelWidth >= 256);
            Assert.True(source.IsFrozen);
        });
    }
    [Theory]
    [InlineData(false, .8)]
    [InlineData(false, 1)]
    [InlineData(false, 1.25)]
    [InlineData(false, 1.5)]
    [InlineData(true, 1)]
    [InlineData(true, 1.5)]
    public void ToolbarGlyphsHaveRoomForTheCompleteFontLine(bool pinned, double scale)
    {
        WpfTestHost.Invoke(() =>
        {
            var owner = new Window();
            _ = new System.Windows.Interop.WindowInteropHelper(owner).EnsureHandle();
            Window window = pinned
                ? new Screenshot.App.Pin.PinnedImageEditorToolbarWindow(owner, AppSettings.CreateDefault())
                : new Screenshot.App.Capture.CaptureOverlayWindow(null, isStartupPrewarm: true, deferInitialScreenCapture: true);
            try
            {
                var style = (Style)window.Resources[pinned ? "ToolbarGlyphIcon" : "InlineGlyphIcon"];
                var buttonStyle = (Style)window.Resources[pinned ? "ToolbarButton" : "InlineActionButton"];
                foreach (var character in ToolbarCharacters)
                {
                    var text = new TextBlock { Text = character, Style = style };
                    var reference = new TextBlock { Text = character, FontFamily = text.FontFamily,
                        FontSize = text.FontSize, FontWeight = text.FontWeight };
                    reference.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                    var button = new Button { Style = buttonStyle, Content = text };
                    var root = new Border { Child = button, LayoutTransform = new ScaleTransform(scale, scale) };
                    root.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                    root.Arrange(new Rect(new Point(), root.DesiredSize));
                    root.UpdateLayout();
                    Assert.True(text.ActualHeight >= reference.DesiredSize.Height - .1,
                        $"{character}: line {reference.DesiredSize.Height} clipped to {text.ActualHeight}");
                    var origin = text.TranslatePoint(new Point(), button);
                    Assert.True(origin.Y >= 0);
                    Assert.True(origin.Y + text.ActualHeight <= button.ActualHeight + .1);
                    Assert.InRange(Math.Abs(origin.X + text.ActualWidth / 2 - button.ActualWidth / 2), 0, 1);
                }
            }
            finally { window.Close(); owner.Close(); }
        });
    }

    [Theory]
    [InlineData(AppTheme.GlassMist)]
    [InlineData(AppTheme.GlassInk)]
    public void GlassThemesPersistAndProvideReadableSharedResources(AppTheme theme)
    {
        var file = Path.Combine(Path.GetTempPath(), $"snapcut-theme-{Guid.NewGuid():N}.json");
        try
        {
            var store = new SettingsStore(file);
            store.Save(AppSettings.CreateDefault() with { Theme = theme });
            Assert.Equal(theme, store.Load().Settings.Theme);
            WpfTestHost.Invoke(() =>
            {
                var resources = new ResourceDictionary();
                AppThemeManager.ApplySettingsPalette(resources, theme);
                var panel = Assert.IsAssignableFrom<Brush>(resources["AppPanelBackgroundBrush"]);
                var background = Assert.IsAssignableFrom<Brush>(resources["AppWindowBackgroundBrush"]);
                var glass = Assert.IsAssignableFrom<Brush>(resources["AppGlassBackgroundBrush"]);
                Assert.True(background.IsFrozen);
                Assert.True(glass.IsFrozen);
                var anotherWindow = new ResourceDictionary();
                AppThemeManager.ApplySettingsPalette(anotherWindow, theme);
                Color panelColor;
                if (theme == AppTheme.GlassInk)
                {
                    Assert.True(Assert.IsType<ImageBrush>(background).ImageSource.IsFrozen);
                    Assert.IsType<ImageBrush>(glass);
                    Assert.Same(background, anotherWindow["AppWindowBackgroundBrush"]);
                    Assert.Same(glass, anotherWindow["AppGlassBackgroundBrush"]);
                    Assert.Same(background, resources["AppFloatingSurfaceBrush"]);
                    var menu = Assert.IsType<LinearGradientBrush>(resources["AppPopupBackgroundBrush"]);
                    Assert.All(menu.GradientStops, stop => Assert.Equal(255, stop.Color.A));
                    panelColor = Assert.IsType<LinearGradientBrush>(panel).GradientStops[0].Color;
                }
                else
                {
                    var fallback = Assert.IsType<LinearGradientBrush>(background);
                    Assert.All(fallback.GradientStops, stop => Assert.Equal(255, stop.Color.A));
                    Assert.Equal(fallback.GradientStops[0].Color, Assert.IsType<LinearGradientBrush>(anotherWindow["AppWindowBackgroundBrush"]).GradientStops[0].Color);
                    Assert.All(Assert.IsType<LinearGradientBrush>(glass).GradientStops, stop => Assert.InRange(stop.Color.A, 34, 52));
                    Assert.All(Assert.IsType<LinearGradientBrush>(resources["AppFloatingSurfaceBrush"]).GradientStops, stop => Assert.InRange(stop.Color.A, 155, 195));
                    Assert.All(Assert.IsType<LinearGradientBrush>(resources["AppPopupBackgroundBrush"]).GradientStops, stop => Assert.InRange(stop.Color.A, 190, 215));
                    panelColor = Assert.IsType<LinearGradientBrush>(panel).GradientStops[0].Color;
                }
                var text = Assert.IsType<SolidColorBrush>(resources["AppTextPrimaryBrush"]);
                Assert.True(panel.IsFrozen);
                Assert.True(text.IsFrozen);
                var brighter = Math.Max(Luminance(panelColor), Luminance(text.Color));
                var darker = Math.Min(Luminance(panelColor), Luminance(text.Color));
                Assert.True((brighter + .05) / (darker + .05) >= 4.5);
                // Include the actual layered material, not just nominal palette
                // colors: translucent glows must not wash out foreground text.
                var visual = new DrawingVisual();
                using (var dc = visual.RenderOpen())
                {
                    dc.DrawRectangle(background, null, new Rect(0, 0, 80, 80));
                    dc.DrawRectangle(glass, null, new Rect(0, 0, 80, 80));
                }
                var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(80, 80, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(visual);
                var pixels = new byte[80 * 80 * 4];
                bitmap.CopyPixels(pixels, 80 * 4, 0);
                for (var i = 0; i < pixels.Length; i += 4)
                {
                    var luminance = Luminance(Color.FromRgb(pixels[i + 2], pixels[i + 1], pixels[i]));
                    var contrast = (Math.Max(luminance, Luminance(text.Color)) + .05) /
                        (Math.Min(luminance, Luminance(text.Color)) + .05);
                    Assert.True(contrast >= 4.5, $"{theme}: material/text contrast {contrast:0.00}");
                }
                Assert.IsType<SolidColorBrush>(resources["SelectionBorderBrush"]);
                Assert.IsAssignableFrom<Brush>(resources["EditorToolbarButtonBackgroundBrush"]);
                Assert.IsAssignableFrom<Brush>(resources["ImageEditorToolbarBackgroundBrush"]);
                var choices = new SettingsViewModel(AppSettings.CreateDefault()).ThemeOptions;
                Assert.Equal(8, choices.Count);
                Assert.Contains(choices, choice => choice.Value == theme.ToString());
            });
        }
        finally
        {
            File.Delete(file);
            File.Delete(file + ".bak");
        }
    }

    [Fact]
    public void ResultReusesBusySurfaceAndStopsActivityWithoutLosingSourceNotice()
    {
        WpfTestHost.Invoke(() =>
        {
            var popup = new RecognitionResultPopupWindow("识别", "", null);
            try
            {
                var chrome = System.Windows.Shell.WindowChrome.GetWindowChrome(popup);
                Assert.NotNull(chrome);
                Assert.Equal(new Thickness(-1), chrome.GlassFrameThickness);
                Assert.Equal(new Thickness(6), chrome.ResizeBorderThickness);
                Assert.Equal(System.Windows.Shell.NonClientFrameEdges.None, chrome.NonClientFrameEdges);
                Assert.Equal(ResizeMode.CanResize, popup.ResizeMode);
                var rail = Assert.IsType<ActivityRail>(popup.FindName("BusyRail"));
                var result = Assert.IsType<TextBox>(popup.FindName("ResultTextBox"));
                var copy = Assert.IsType<Button>(popup.FindName("CopyAllButton"));
                popup.ShowBusy("正在翻译…", "本机离线");
                Assert.True(rail.IsActive);
                Assert.False(copy.IsEnabled);
                Assert.Equal(Visibility.Collapsed, result.Visibility);
                const string notice = "本机离线 · TranslateGemma · 3.2 秒";
                popup.ShowResult("已翻译", "source", "译文", notice);
                Assert.False(rail.IsActive);
                Assert.Equal(Visibility.Collapsed, rail.Visibility);
                Assert.True(copy.IsEnabled);
                Assert.Equal("译文", result.Text);
                Assert.Equal(Visibility.Visible, result.Visibility);
                Assert.Equal(notice, Assert.IsType<TextBlock>(popup.FindName("SourceText")).Text);
            }
            finally { popup.Close(); }
            popup.ShowResult("迟到的结果", "原文", "不能重新显示", null);
            Assert.True(popup.WasClosed);
            Assert.False(popup.IsVisible);
        });
    }

    [Theory]
    [InlineData(AppTheme.GlassMist)]
    [InlineData(AppTheme.GlassInk)]
    public void FloatingGlassRemainsReadableOverAnyDesktop(AppTheme theme)
    {
        WpfTestHost.Invoke(() =>
        {
            var overlay = new Screenshot.App.Capture.CaptureOverlayWindow(null,
                isStartupPrewarm: true, deferInitialScreenCapture: true);
            var progress = new Screenshot.App.Capture.ScrollCaptureProgressWindow();
            try
            {
                foreach (var (window, name) in new (Window, string)[]
                    { (overlay, "CaptureToolbar"), (progress, "ProgressShell") })
                {
                    AppThemeManager.ApplySettingsPalette(window.Resources, theme);
                    var surface = Assert.IsType<Border>(window.FindName(name));
                    var material = Assert.IsAssignableFrom<Brush>(surface.Background);
                    Assert.Same(window.Resources["AppFloatingSurfaceBrush"], material);
                    byte[]? previous = null;
                    foreach (var desktop in new[] { Brushes.Black, Brushes.White, Brushes.Red })
                    {
                        var visual = new DrawingVisual();
                        using (var dc = visual.RenderOpen())
                        {
                            dc.DrawRectangle(desktop, null, new Rect(0, 0, 80, 80));
                            dc.DrawRectangle(material, null, new Rect(0, 0, 80, 80));
                        }
                        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(80, 80, 96, 96, PixelFormats.Pbgra32);
                        bitmap.Render(visual);
                        var pixels = new byte[80 * 80 * 4];
                        bitmap.CopyPixels(pixels, 80 * 4, 0);
                        if (previous is not null)
                        {
                            if (theme == AppTheme.GlassInk) Assert.Equal(previous, pixels);
                            else Assert.NotEqual(previous, pixels);
                        }
                        previous = pixels;
                        foreach (var key in FloatingForegroundKeys)
                        {
                            var foreground = Assert.IsType<SolidColorBrush>(window.Resources[key]).Color;
                            var minimumContrast = double.MaxValue;
                            for (var i = 0; i < pixels.Length; i += 4)
                            {
                                Assert.Equal(255, pixels[i + 3]);
                                var l = Luminance(Color.FromRgb(pixels[i + 2], pixels[i + 1], pixels[i]));
                                var contrast = (Math.Max(l, Luminance(foreground)) + .05) /
                                    (Math.Min(l, Luminance(foreground)) + .05);
                                minimumContrast = Math.Min(minimumContrast, contrast);
                            }
                            Assert.True(minimumContrast >= 4.5, $"{theme}/{name}/{key}: minimum {minimumContrast:0.00}");
                        }
                    }
                    // Switching back also removes the glass-specific material.
                    AppThemeManager.ApplySettingsPalette(window.Resources, AppTheme.ForestNight);
                    Assert.Same(window.Resources["AppPanelBackgroundBrush"], surface.Background);
                }
            }
            finally { overlay.Close(); progress.CloseFromCoordinator(); }
        });
    }

    private static double Luminance(Color color)
    {
        static double Channel(byte value)
        {
            var s = value / 255.0;
            return s <= .04045 ? s / 12.92 : Math.Pow((s + .055) / 1.055, 2.4);
        }
        return .2126 * Channel(color.R) + .7152 * Channel(color.G) + .0722 * Channel(color.B);
    }
}
