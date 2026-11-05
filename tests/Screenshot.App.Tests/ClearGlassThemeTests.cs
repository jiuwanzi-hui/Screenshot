using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Markup;
using System.Xml.Linq;
using Screenshot.App.Core;
using Screenshot.App.Infrastructure;
using Screenshot.App.Presentation;
using Screenshot.App.Text;

namespace Screenshot.App.Tests;

public sealed class ClearGlassThemeTests
{
    private static readonly string[] GlassForegroundKeys =
        ["AppTextPrimaryBrush", "AppTextSecondaryBrush", "AppControlForegroundBrush"];

    [Fact]
    public void GlassPreservesDesktopColorWhileControlsAndSelectionStayReadable()
    {
        WpfTestHost.Invoke(() =>
        {
            var resources = new ResourceDictionary();
            AppThemeManager.ApplySettingsPalette(resources, AppTheme.GlassMist);
            var glass = Assert.IsType<LinearGradientBrush>(resources["AppGlassBackgroundBrush"]);
            Assert.All(glass.GradientStops, stop => Assert.InRange(stop.Color.A, 34, 52));
            Assert.True(LiquidGlassChrome.IsGlassTheme(AppTheme.GlassMist));
            var border = (SolidColorBrush)resources["SelectionBorderBrush"];
            Assert.Equal(255, border.Color.A);
            Assert.True(Contrast(border.Color, Colors.White) >= 4.5);
            Assert.True(Contrast(border.Color, Colors.Black) >= 3);
            var choice = new SettingsViewModel(AppSettings.CreateDefault()).ThemeOptions.Single(option => option.Value == nameof(AppTheme.GlassMist));
            Assert.Equal("清透琉璃", choice.Label);

            // Most of the actual desktop signal survives the glass tint.
            byte[]? previous = null;
            foreach (var desktop in new[] { Brushes.Black, Brushes.White, Brushes.DarkBlue })
            {
                var visual = new DrawingVisual();
                using (var dc = visual.RenderOpen())
                {
                    dc.DrawRectangle(desktop, null, new Rect(0, 0, 64, 64));
                    dc.DrawRectangle(glass, null, new Rect(0, 0, 64, 64));
                }
                var composite = new RenderTargetBitmap(64, 64, 96, 96, PixelFormats.Pbgra32);
                composite.Render(visual);
                var colors = new byte[64 * 64 * 4];
                composite.CopyPixels(colors, 64 * 4, 0);
                if (previous is not null) Assert.NotEqual(previous, colors);
                if (desktop == Brushes.Black) Assert.InRange(colors[0], 34, 60);
                if (desktop == Brushes.White) Assert.InRange(colors[0], 245, 255);
                previous = (byte[])colors.Clone();
                // Floating controls carry their own stronger, local scrim.
                using (var dc = visual.RenderOpen())
                {
                    dc.DrawRectangle(desktop, null, new Rect(0, 0, 64, 64));
                    dc.DrawRectangle((Brush)resources["AppFloatingSurfaceBrush"], null, new Rect(0, 0, 64, 64));
                }
                composite.Clear();
                composite.Render(visual);
                composite.CopyPixels(colors, 64 * 4, 0);
                foreach (var key in GlassForegroundKeys)
                {
                    var ink = ((SolidColorBrush)resources[key]).Color;
                    for (var i = 0; i < colors.Length; i += 4)
                        Assert.True(Contrast(ink, Color.FromRgb(colors[i + 2], colors[i + 1], colors[i])) >= 4.5,
                            $"{key} is unreadable over {desktop}.");
                }
            }
        });
    }

    [Fact]
    public void ClearAreaInkStaysBrightAndRestoresWhenAReadingSurfaceAppears()
    {
        WpfTestHost.Invoke(() =>
        {
            var root = new Border { Background = Brushes.Transparent };
            AppThemeManager.ApplySettingsPalette(root.Resources, AppTheme.GlassMist);
            var label = new TextBlock { Text = "保存与输出" };
            label.SetResourceReference(TextBlock.ForegroundProperty, "AppTextPrimaryBrush");
            root.Child = label;
            var original = label.Foreground;
            ClearGlassText.Apply(label, true);
            Assert.Same(Brushes.White, label.Foreground);
            Assert.NotNull(label.Effect);
            Assert.Equal(1, label.Opacity);
            // Selected navigation and controls have their own reading surface.
            root.Background = Brushes.White;
            ClearGlassText.Apply(label, true);
            Assert.Same(original, label.Foreground);
            Assert.NotNull(label.Effect);
            root.Background = Brushes.Transparent;
            ClearGlassText.Apply(label, true);
            ClearGlassText.Apply(label, false);
            Assert.Same(original, label.Foreground);
            Assert.Null(label.Effect);
            root.Child = null;
            var check = new CheckBox { Content = label, Background = Brushes.White };
            root.Child = check;
            ClearGlassText.Apply(label, true);
            Assert.Same(Brushes.White, label.Foreground);
        });
    }

    [Theory]
    [InlineData("AppInputBackgroundBrush", "AppControlForegroundBrush", true)]
    [InlineData("AppInputFocusBrush", "AppControlForegroundBrush", true)]
    [InlineData("AppPanelBackgroundBrush", "AppControlForegroundBrush", true)]
    [InlineData("AppFloatingSurfaceBrush", "EditorToolbarIconBrush", false)]
    [InlineData("AppPopupBackgroundBrush", "AppControlForegroundBrush", false)]
    [InlineData("EditorToolbarButtonSelectedBackgroundBrush", "EditorToolbarIconBrush", false)]
    [InlineData("AppAccentBrush", "AppSelectedControlForegroundBrush", false)]
    [InlineData("AppAccentMutedBrush", "AppSelectedControlForegroundBrush", false)]
    public void TransparentControlLayersProtectInkAcrossDesktopColors(string surfaceKey, string inkKey, bool windowLayer)
    {
        WpfTestHost.Invoke(() =>
        {
            var resources = new ResourceDictionary();
            AppThemeManager.ApplySettingsPalette(resources, AppTheme.GlassMist);
            var surface = Assert.IsAssignableFrom<Brush>(resources[surfaceKey]);
            var ink = Assert.IsType<SolidColorBrush>(resources[inkKey]).Color;
            byte[]? first = null;
            foreach (var desktop in new[] { Brushes.Black, Brushes.White, Brushes.DarkBlue, Brushes.DarkGreen, Brushes.OrangeRed })
            {
                var visual = new DrawingVisual();
                using (var dc = visual.RenderOpen())
                {
                    dc.DrawRectangle(desktop, null, new Rect(0, 0, 100, 40));
                    if (windowLayer) dc.DrawRectangle((Brush)resources["AppGlassBackgroundBrush"], null, new Rect(0, 0, 100, 40));
                    if (surfaceKey.StartsWith("EditorToolbarButton", StringComparison.Ordinal))
                        dc.DrawRectangle((Brush)resources["AppFloatingSurfaceBrush"], null, new Rect(0, 0, 100, 40));
                    dc.DrawRectangle(surface, null, new Rect(0, 0, 100, 40));
                }
                var bitmap = new RenderTargetBitmap(100, 40, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(visual);
                var pixels = new byte[100 * 40 * 4];
                bitmap.CopyPixels(pixels, 400, 0);
                if (first is null) first = pixels;
                else Assert.NotEqual(first, pixels); // Real underlying software still contributes colour.
                for (var i = 0; i < pixels.Length; i += 4)
                    Assert.True(Contrast(ink, Color.FromRgb(pixels[i + 2], pixels[i + 1], pixels[i])) >= 4.5,
                        $"{surfaceKey} / {inkKey} loses contrast over {desktop}.");
                if (desktop == Brushes.White && !surfaceKey.Contains("Accent") && !surfaceKey.StartsWith("EditorToolbarButton", StringComparison.Ordinal))
                    Assert.True(pixels[0] - first[0] >= 45, $"{surfaceKey} hides too much of the underlying window.");
            }
        });
    }

    [Fact]
    public void ClearTextUsesTheCombinedReadingSurfaceWithoutCountingTemplatesTwice()
    {
        WpfTestHost.Invoke(() =>
        {
            var root = new Border();
            AppThemeManager.ApplySettingsPalette(root.Resources, AppTheme.GlassMist);
            root.Background = (Brush)root.Resources["AppGlassBackgroundBrush"];
            var input = new Border { Background = (Brush)root.Resources["AppInputBackgroundBrush"] };
            var label = new TextBlock { Text = "D:\\Pictures\\截图.png" };
            label.SetResourceReference(TextBlock.ForegroundProperty, "AppControlForegroundBrush");
            input.Child = label;
            root.Child = input;
            var original = label.Foreground;
            ClearGlassText.Apply(label, true);
            Assert.Same(original, label.Foreground);
            Assert.NotNull(label.Effect);
            input.Background = Brushes.Transparent;
            ClearGlassText.Apply(label, true);
            Assert.Same(Brushes.White, label.Foreground);
            Assert.NotNull(label.Effect);
            input.Background = (Brush)root.Resources["AppInputBackgroundBrush"];
            ClearGlassText.Apply(label, true);
            Assert.Same(original, label.Foreground);
            Assert.NotNull(label.Effect);
        });
    }

    [Fact]
    public void ComboSelectionKeepsDarkInkOnItsSiblingGlassSurface()
    {
        WpfTestHost.Invoke(() =>
        {
            var root = new Border();
            AppThemeManager.ApplySettingsPalette(root.Resources, AppTheme.GlassMist);
            root.Background = (Brush)root.Resources["AppGlassBackgroundBrush"];
            var label = new TextBlock { Text = "每次保存时选择目录" };
            label.SetResourceReference(TextBlock.ForegroundProperty, "AppControlForegroundBrush");
            var combo = new ComboBox { Background = (Brush)root.Resources["AppInputBackgroundBrush"] };
            // SelectionBoxItem's presenter is a sibling of the painted toggle.
            combo.Items.Add(label);
            combo.SelectedIndex = 0;
            root.Child = combo;
            var original = label.Foreground;
            ClearGlassText.Apply(label, true);
            Assert.Same(original, label.Foreground);
            Assert.NotNull(label.Effect);
        });
    }

    [Fact]
    public void ThemeSwitchingEnablesRealAlphaCompositionAndRestoresTheOpaqueTheme()
    {
        WpfTestHost.Invoke(() =>
        {
            var window = new RecognitionResultPopupWindow("主题验证", "不会显示到桌面", null);
            try
            {
                var handle = new WindowInteropHelper(window).EnsureHandle();
                var originalOpacity = window.Opacity;
                foreach (var theme in new[] { AppTheme.GlassMist, AppTheme.GlassInk, AppTheme.GlassMist, AppTheme.AuroraMist })
                {
                    AppThemeManager.ApplySettingsPalette(window.Resources, theme);
                    LiquidGlassChrome.Apply(window, theme);
                    if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621))
                    {
                        Assert.Equal(0, DwmGetWindowAttribute(handle, 38, out var backdrop, sizeof(int)));
                        Assert.Equal(1, backdrop); // No preset solid system material beneath the glass.
                    }
                    if (theme == AppTheme.GlassMist)
                    {
                        Assert.Same(Brushes.Transparent, window.Background);
                        Assert.Equal(0, HwndSource.FromHwnd(handle)!.CompositionTarget.BackgroundColor.A);
                    }
                    else Assert.NotSame(Brushes.Transparent, window.Background);
                    Assert.Equal(originalOpacity, window.Opacity);
                    Assert.Equal(new Thickness(-1),
                        System.Windows.Shell.WindowChrome.GetWindowChrome(window).GlassFrameThickness);
                    Assert.False(window.IsVisible);
                }
            }
            finally { window.Close(); }
        });
    }

    [Theory]
    [InlineData(false, 980, 820, AppTheme.GlassMist)]
    [InlineData(true, 980, 820, AppTheme.GlassMist)]
    [InlineData(false, 2560, 1392, AppTheme.GlassMist)]
    [InlineData(true, 2560, 1392, AppTheme.GlassMist)]
    [InlineData(false, 480, 540, AppTheme.GlassMist)]
    [InlineData(false, 980, 820, AppTheme.AuroraMist)]
    [InlineData(false, 980, 820, AppTheme.CoralSky)]
    [InlineData(false, 980, 820, AppTheme.GinkgoPaper)]
    [InlineData(false, 980, 820, AppTheme.ForestNight)]
    [InlineData(false, 980, 820, AppTheme.ObsidianGold)]
    [InlineData(false, 980, 820, AppTheme.NeonDeep)]
    public void SettingsPageCanBeReviewedOffScreen(bool toolbar, int width, int height, AppTheme theme)
    {
        var path = Path.Combine(Path.GetTempPath(), $"snapcut-theme-review-{Guid.NewGuid():N}.json");
        try
        {
            WpfTestHost.Invoke(() =>
            {
                using var hotkeys = new GlobalHotKeyManager();
                var settings = AppSettings.CreateDefault() with { Theme = theme, CloseBehavior = WindowCloseBehavior.ExitApplication };
                var window = new MainWindow(settings, new SettingsStore(path), new NoStartup(), hotkeys, new NoCredentials());
                try
                {
                    window.Width = width;
                    window.Height = height;
                    AppThemeManager.ApplySettingsPalette(window.Resources, settings.Theme);
                    // Load the authored application styles without constructing
                    // a second Application or opening any desktop window.
                    var appPath = Path.GetFullPath("../../../../../src/Screenshot.App/App.xaml", AppContext.BaseDirectory);
                    var app = XDocument.Load(appPath);
                    var dictionary = app.Root!.Elements().Single(element => element.Name.LocalName == "Application.Resources").Elements().Single();
                    dictionary.SetAttributeValue(XNamespace.Xmlns + "x", "http://schemas.microsoft.com/winfx/2006/xaml");
                    var applicationResources = (ResourceDictionary)XamlReader.Parse(dictionary.ToString().Replace(
                        "clr-namespace:Screenshot.App.Presentation", "clr-namespace:Screenshot.App.Presentation;assembly=SnapCut", StringComparison.Ordinal), new ParserContext
                    { BaseUri = new Uri("pack://application:,,,/SnapCut;component/") });
                    window.Resources.MergedDictionaries.Add(applicationResources);
                    _ = new WindowInteropHelper(window).EnsureHandle();
                    if (toolbar)
                    {
                        var pageContent = (StackPanel)((ScrollViewer)window.FindName("GeneralSettingsPanel")).Content;
                        var tabs = pageContent.Children.OfType<WrapPanel>().First();
                        ((RadioButton)tabs.Children[1]).IsChecked = true;
                    }
                    ((FrameworkElement)window.Content).Measure(new Size(width, height));
                    ((FrameworkElement)window.Content).Arrange(new Rect(0, 0, width, height));
                    LiquidGlassChrome.Apply(window, settings.Theme);
                    var shell = (FrameworkElement)window.Content;
                    window.Content = null;
                    shell.DataContext = window.DataContext;
                    shell.Resources.MergedDictionaries.Add(window.Resources);
                    // A muted desktop substitute stays entirely off-screen.
                    // It makes real alpha holes visible in the review image.
                    var desk = new DrawingGroup();
                    using (var dc = desk.Open())
                    {
                        dc.DrawRectangle(Brushes.SlateGray, null, new Rect(0, 0, width, height));
                        dc.DrawRectangle(Brushes.LightSteelBlue, null, new Rect(width / 2d, 0, width / 2d, height));
                        for (var y = 70; y < height; y += 90)
                            dc.DrawRoundedRectangle(Brushes.CadetBlue, null, new Rect(40, y, width - 100, 18), 6, 6);
                    }
                    var host = new Border { Background = new DrawingBrush(desk), Child = shell };
                    host.Measure(new Size(width, height));
                    host.Arrange(new Rect(0, 0, width, height));
                    host.UpdateLayout();
                    var page = (ScrollViewer)window.FindName("GeneralSettingsPanel");
                    var navigation = (ListBox)window.FindName("SettingsNavigation");
                    Assert.InRange(page.ActualWidth, 250, 1000);
                    Assert.InRange(page.ActualHeight, 250, 1000);
                    var pagePosition = page.TranslatePoint(new Point(), host);
                    var navPosition = navigation.TranslatePoint(new Point(), host);
                    Assert.InRange(pagePosition.X - (navPosition.X + navigation.ActualWidth), 12, 60);
                    Assert.True(pagePosition.X + page.ActualWidth <= width);
                    var option = (RadioButton)window.FindName("AuroraMistThemeOption");
                    if (!toolbar)
                    {
                        Assert.InRange(option.ActualWidth, 80, 260);
                        var preview = ((StackPanel)option.Content).Children.OfType<System.Windows.Controls.Image>().Single();
                        Assert.Equal(Stretch.Uniform, preview.Stretch);
                    }
                    var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(host);
                    Assert.Equal(new Size(width, height), host.RenderSize);
                    Assert.False(window.IsVisible);
                    if (Environment.GetEnvironmentVariable("SNAPCUT_THEME_PREVIEW") is { Length: > 0 } output)
                    {
                        output = Path.Combine(Path.GetDirectoryName(output)!, Path.GetFileNameWithoutExtension(output) + $"-{theme}-{width}x{height}" + (toolbar ? "-toolbar" : "") + ".png");
                        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                        var encoder = new PngBitmapEncoder();
                        encoder.Frames.Add(BitmapFrame.Create(bitmap));
                        using var stream = File.Create(output);
                        encoder.Save(stream);
                    }
                }
                finally { window.Close(); }
            });
        }
        finally { File.Delete(path); File.Delete(path + ".bak"); }
    }

    private static double Contrast(Color first, Color second)
    {
        static double Luminance(Color color)
        {
            static double Linear(byte value) => value / 255d <= .04045 ? value / 255d / 12.92 : Math.Pow((value / 255d + .055) / 1.055, 2.4);
            return .2126 * Linear(color.R) + .7152 * Linear(color.G) + .0722 * Linear(color.B);
        }
        return (Math.Max(Luminance(first), Luminance(second)) + .05) / (Math.Min(Luminance(first), Luminance(second)) + .05);
    }

    [Fact]
    public void LeavingClearGlassRestoresTheOtherThemesControlTreatment()
    {
        WpfTestHost.Invoke(() =>
        {
            var resources = new ResourceDictionary();
            foreach (var theme in Enum.GetValues<AppTheme>().Where(theme => theme != AppTheme.GlassMist))
            {
                AppThemeManager.ApplySettingsPalette(resources, AppTheme.GlassMist);
                AppThemeManager.ApplySettingsPalette(resources, theme);
                Assert.Same(Brushes.Transparent, resources["AppControlReflectionBrush"]);
                Assert.Equal(Visibility.Collapsed, resources["AppLiquidLensVisibility"]);
                Assert.Same(Brushes.Transparent, resources["ImageEditorToolbarPanelBackgroundBrush"]);
                Assert.Equal(new Thickness(0, 0, 0, 1), resources["ImageEditorToolbarPanelBorderThickness"]);
                Assert.Equal(new CornerRadius(0), resources["ImageEditorToolbarPanelCornerRadius"]);
                Assert.Equal(.92, resources["AppButtonHoverOpacity"]);
                Assert.Equal(.78, resources["AppButtonPressedOpacity"]);
            }
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AdditionalDialogsUseGlassCaptionsAndRestoreTheirOriginalContentOnThemeChange(bool crop)
    {
        WpfTestHost.Invoke(() =>
        {
            Window window = crop
                ? new Screenshot.App.Editor.ImageCropWindow(BitmapSource.Create(8, 8, 96, 96, PixelFormats.Bgra32, null, new byte[256], 32))
                : new TranslationProfileEditorWindow(new AiTranslationProfileItem(new AiTranslationProfile { Name = "主题验证" }), new NoCredentials());
            try
            {
                var content = window.Content;
                var style = window.WindowStyle;
                var chrome = System.Windows.Shell.WindowChrome.GetWindowChrome(window);
                _ = new WindowInteropHelper(window).EnsureHandle();
                foreach (var theme in new[] { AppTheme.GlassMist, AppTheme.AuroraMist, AppTheme.GlassMist, AppTheme.GlassInk })
                {
                    AppThemeManager.ApplySettingsPalette(window.Resources, theme);
                    LiquidGlassChrome.Apply(window, theme);
                    Assert.False(window.IsVisible);
                    if (theme == AppTheme.GlassMist)
                    {
                        Assert.IsType<Border>(window.Content);
                        Assert.Equal(WindowStyle.None, window.WindowStyle);
                        Assert.False(System.Windows.Shell.WindowChrome.GetWindowChrome(window).UseAeroCaptionButtons);
                        Assert.Same(window, Window.GetWindow((DependencyObject)content));
                        Assert.Same(Brushes.Transparent, window.Background);
                        Assert.Same(Brushes.Transparent, window.Resources["AppWindowBackgroundBrush"]);
                    }
                    else
                    {
                        Assert.Same(content, window.Content);
                        Assert.Equal(style, window.WindowStyle);
                        Assert.Same(chrome, System.Windows.Shell.WindowChrome.GetWindowChrome(window));
                        Assert.Same(window.Resources["AppWindowBackgroundBrush"], window.Background);
                    }
                }
            }
            finally { window.Close(); }
        });
    }

    private sealed class NoStartup : IStartupRegistrationService
    {
        public bool IsEnabled() => false;
        public void SetEnabled(bool enabled) { }
    }
    private sealed class NoCredentials : ITranslationCredentialStore
    {
        public string? GetApiKey(string providerId) => null;
        public void SetApiKey(string providerId, string? apiKey) { }
    }
    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr handle, int attribute, out int value, int size);
}
