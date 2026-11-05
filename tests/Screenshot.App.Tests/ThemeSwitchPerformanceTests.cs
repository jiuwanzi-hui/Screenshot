using System.Diagnostics;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Interop;
using System.Windows.Threading;
using Screenshot.App.Core;
using Screenshot.App.Infrastructure;
using Screenshot.App.Presentation;
using Screenshot.App.Text;

namespace Screenshot.App.Tests;

public sealed partial class ImmediateSettingsTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ApplicationThemeSwitchKeepsTheExistingSettingsWindowTransparentAfterLayout(bool simulateOpaqueRender)
    {
        WpfTestHost.Invoke(() =>
        {
            using var hotkeys = new GlobalHotKeyManager(deferLowLevelInputHooks: true);
            var store = new SettingsStore(Path.Combine(_testDirectory, "native-theme-switch.json"));
            var window = new MainWindow(CreateSettings() with { Theme = AppTheme.AuroraMist }, store,
                new FakeStartupRegistrationService(), hotkeys, new FakeTranslationCredentialStore());
            using var themes = new AppThemeManager();
            window.SettingsSaved += (_, args) => themes.Apply(args.Settings.Theme);
            themes.ThemeChanged += (_, theme) =>
            {
                window.ApplySettingsPalette(theme);
                // WPF bindings and native chrome also run after the synchronous
                // selection callback. A final surface update must win over
                // an opaque target left by that pending frame.
                if (simulateOpaqueRender)
                    _ = window.Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
                        HwndSource.FromHwnd(new WindowInteropHelper(window).Handle)!.CompositionTarget.BackgroundColor = Colors.White));
            };
            try
            {
                var handle = new WindowInteropHelper(window).EnsureHandle();
                // Attach the real window tree to its hidden HWND so Loaded and
                // the actual theme-card handlers run without showing a window.
                HwndSource.FromHwnd(handle)!.RootVisual = window;
                var root = (Border)window.Content;
                var size = new Size(980, 740);
                root.Measure(size); root.Arrange(new Rect(size)); root.UpdateLayout();
                var ready = new DispatcherFrame();
                _ = window.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => ready.Continue = false));
                Dispatcher.PushFrame(ready);
                Assert.True(window.IsLoaded);
                var glassOptions = (Dictionary<AppTheme, RadioButton>)typeof(MainWindow)
                    .GetField("_glassThemeOptions", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                    .GetValue(window)!;
                foreach (var theme in new[] { AppTheme.GlassMist, AppTheme.ForestNight,
                    AppTheme.GlassMist, AppTheme.GlassInk, AppTheme.AuroraMist, AppTheme.GlassMist })
                {
                    var watch = Stopwatch.StartNew();
                    var option = glassOptions.TryGetValue(theme, out var glass) ? glass : (RadioButton)window.FindName(theme + "ThemeOption");
                    option.IsChecked = true;
                    root.Measure(size); root.Arrange(new Rect(size)); root.UpdateLayout();
                    var frame = new DispatcherFrame();
                    _ = window.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => frame.Continue = false));
                    Dispatcher.PushFrame(frame);
                    Console.WriteLine($"Theme card {theme}: {watch.Elapsed.TotalMilliseconds:F1} ms including layout/chrome; simulated opaque render={simulateOpaqueRender}");
                    Assert.Equal(theme, themes.ResolvedTheme);
                    Assert.Equal(theme, store.Load().Settings.Theme);
                    Assert.Equal(handle, new WindowInteropHelper(window).Handle);
                    Assert.False(window.IsVisible);
                    if (theme == AppTheme.GlassMist)
                    {
                        Assert.Same(Brushes.Transparent, window.Background);
                        Assert.Same(Brushes.Transparent, window.Resources["AppWindowBackgroundBrush"]);
                        Assert.Equal(Colors.Transparent, HwndSource.FromHwnd(handle)!.CompositionTarget.BackgroundColor);
                    }
                    else Assert.NotSame(Brushes.Transparent, window.Background);
                }
            }
            finally { window.RequestExit(); store.FlushPendingSave(); }
        });
    }

    [Fact]
    public void RapidThemeSelectionsKeepTheLatestChromeAndDisposalCancelsPendingWork()
    {
        WpfTestHost.Invoke(() =>
        {
            var window = new RecognitionResultPopupWindow("主题切换", "连续切换后使用最终主题", null);
            using var themes = new AppThemeManager();
            var handle = new WindowInteropHelper(window).EnsureHandle();
            try
            {
                themes.Apply(AppTheme.GlassMist);
                themes.Apply(AppTheme.ForestNight);
                themes.Apply(AppTheme.GlassInk);
                var frame = new DispatcherFrame();
                _ = window.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => frame.Continue = false));
                Dispatcher.PushFrame(frame);
                Assert.NotSame(Brushes.Transparent, window.Background);
                Assert.Equal(AppTheme.GlassInk, themes.ResolvedTheme);
                Assert.Equal(handle, new WindowInteropHelper(window).Handle);
                Assert.False(window.IsVisible);

                themes.Apply(AppTheme.GlassMist);
                var pending = (DispatcherOperation)typeof(AppThemeManager)
                    .GetField("_pendingChromeRefresh", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                    .GetValue(themes)!;
                window.Close();
                themes.Dispose();
                Assert.Equal(DispatcherOperationStatus.Aborted, pending.Status);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void ThemePalettePublishesOnlyFinalBrushAndPreservesUnrelatedResources()
    {
        WpfTestHost.Invoke(() =>
        {
            var border = new Border();
            var marker = new object();
            border.Resources["UnrelatedSetting"] = marker;
            AppThemeManager.ApplySettingsPalette(border.Resources, AppTheme.GlassInk);
            border.SetResourceReference(Border.BackgroundProperty, "AppWindowBackgroundBrush");
            var descriptor = DependencyPropertyDescriptor.FromProperty(Border.BackgroundProperty, typeof(Border));
            var notifications = 0;
            EventHandler changed = (_, _) => notifications++;
            descriptor.AddValueChanged(border, changed);
            try
            {
                foreach (var theme in new[] { AppTheme.GlassMist, AppTheme.ForestNight, AppTheme.GlassInk })
                {
                    notifications = 0;
                    AppThemeManager.ApplySettingsPalette(border.Resources, theme);
                    Assert.Same(border.Resources["AppWindowBackgroundBrush"], border.Background);
                    Assert.Equal(1, notifications);
                    Assert.Same(marker, border.Resources["UnrelatedSetting"]);
                }
            }
            finally { descriptor.RemoveValueChanged(border, changed); }
        });
    }

    [Fact]
    public void ThemeSwitchOffscreenKeepsLatestPaletteAndMeasuresLayout()
    {
        WpfTestHost.Invoke(() =>
        {
            using var hotkeys = new GlobalHotKeyManager();
            var window = new MainWindow(CreateSettings(),
                new SettingsStore(Path.Combine(_testDirectory, "theme-timing.json")),
                new FakeStartupRegistrationService(), hotkeys, new FakeTranslationCredentialStore());
            try
            {
                var root = (Border)window.Content;
                var size = new Size(1000, 740);
                root.Measure(size); root.Arrange(new Rect(size)); root.UpdateLayout();
                foreach (var theme in new[] { AppTheme.GlassMist, AppTheme.GlassInk,
                    AppTheme.AuroraMist, AppTheme.GlassMist, AppTheme.ForestNight, AppTheme.GlassMist })
                {
                    var timer = Stopwatch.StartNew();
                    window.ApplySettingsPalette(theme);
                    var paletteMs = timer.Elapsed.TotalMilliseconds;
                    root.Measure(size); root.Arrange(new Rect(size)); root.UpdateLayout();
                    Console.WriteLine($"Theme {theme}: palette {paletteMs:F1} ms; with layout {timer.Elapsed.TotalMilliseconds:F1} ms");
                    var expected = new ResourceDictionary();
                    AppThemeManager.ApplySettingsPalette(expected, theme);
                    Assert.Equal(((SolidColorBrush)expected["AppTextPrimaryBrush"]).Color,
                        ((SolidColorBrush)window.FindResource("AppTextPrimaryBrush")).Color);
                    Assert.Equal(size.Width, root.ActualWidth);
                }
            }
            finally { window.RequestExit(); }
        });
    }
}
