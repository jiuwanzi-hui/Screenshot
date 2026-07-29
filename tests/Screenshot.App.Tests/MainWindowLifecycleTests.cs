using System.IO;
using Screenshot.App.Core;
using Screenshot.App.Infrastructure;
using Screenshot.App.Presentation;
using Screenshot.App.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Screenshot.App.Tests;

public sealed class MainWindowLifecycleTests
{
    [Fact]
    public void ShowsHidesAndExplicitlyClosesTheSettingsWindow()
    {
        var wasVisibleAfterShow = false;
        var wasVisibleAfterCloseRequest = true;
        var wasClosedAfterExitRequest = false;

        WpfTestHost.Invoke(() =>
        {
            using var hotKeyManager = new GlobalHotKeyManager();
            var window = new MainWindow(
                AppSettings.CreateDefault(),
                new SettingsStore(Path.Combine(Path.GetTempPath(), "Screenshot.App.Tests", "settings.json")),
                new FakeStartupRegistrationService(),
                hotKeyManager,
                new FakeTranslationCredentialStore());

            window.ShowFromTray();
            wasVisibleAfterShow = window.IsVisible;
            Assert.Equal(ResizeMode.CanResize, window.ResizeMode);

            window.Close();
            wasVisibleAfterCloseRequest = window.IsVisible;

            window.ShowFromTray();
            window.RequestExit();
            wasClosedAfterExitRequest = !window.IsVisible;
        });

        Assert.True(wasVisibleAfterShow);
        Assert.False(wasVisibleAfterCloseRequest);
        Assert.True(wasClosedAfterExitRequest);
    }

    [Fact]
    public void HidingTheSettingsWindowEndsHotKeyCaptureMode()
    {
        WpfTestHost.Invoke(() =>
        {
            using var hotKeyManager = new GlobalHotKeyManager();
            var window = new MainWindow(
                AppSettings.CreateDefault(),
                new SettingsStore(Path.Combine(
                    Path.GetTempPath(),
                    "Screenshot.App.Tests",
                    "hotkey-lifecycle-settings.json")),
                new FakeStartupRegistrationService(),
                hotKeyManager,
                new FakeTranslationCredentialStore());

            window.ShowFromTray();
            var navigation = Assert.IsType<ListBox>(
                window.FindName("SettingsNavigation"));
            navigation.SelectedIndex = 1;
            window.UpdateLayout();
            var captureBox = Assert.IsType<HotKeyCaptureBox>(
                window.FindName("RegionCaptureHotKeyBox"));
            Assert.True(captureBox.Focus());
            Keyboard.Focus(captureBox);
            Assert.True(window.IsCapturingHotKey);

            window.Close();

            Assert.False(window.IsCapturingHotKey);
            window.ShowFromTray();
            window.RequestExit();
        });
    }

    [Fact]
    public void ClosingTheWindowRequestsExitWhenConfiguredToExit()
    {
        var exitRequested = false;
        var remainedVisibleUntilApplicationExit = false;

        WpfTestHost.Invoke(() =>
        {
            using var hotKeyManager = new GlobalHotKeyManager();
            var window = new MainWindow(
                AppSettings.CreateDefault() with
                {
                    CloseBehavior = WindowCloseBehavior.ExitApplication,
                },
                new SettingsStore(Path.Combine(
                    Path.GetTempPath(),
                    "Screenshot.App.Tests",
                    "exit-on-close-settings.json")),
                new FakeStartupRegistrationService(),
                hotKeyManager,
                new FakeTranslationCredentialStore());
            window.ExitRequested += (_, _) => exitRequested = true;

            window.ShowFromTray();
            window.Close();
            remainedVisibleUntilApplicationExit = window.IsVisible;
            window.RequestExit();
        });

        Assert.True(exitRequested);
        Assert.True(remainedVisibleUntilApplicationExit);
    }

    [Fact]
    public void HotKeySettingsExposePinImageButNotScrollingCaptureShortcut()
    {
        WpfTestHost.Invoke(() =>
        {
            using var hotKeyManager = new GlobalHotKeyManager();
            var window = new MainWindow(
                AppSettings.CreateDefault(),
                new SettingsStore(Path.Combine(
                    Path.GetTempPath(),
                    "Screenshot.App.Tests",
                    "hotkey-without-pin-settings.json")),
                new FakeStartupRegistrationService(),
                hotKeyManager,
                new FakeTranslationCredentialStore());

            window.ShowFromTray();
            Assert.IsType<HotKeyCaptureBox>(window.FindName("PinHotKeyBox"));
            Assert.Null(window.FindName("ScrollCaptureHotKeyBox"));
            Assert.IsType<TextBlock>(window.FindName("CurrentVersionText"));
            Assert.IsType<Button>(window.FindName("CheckForUpdatesButton"));
            Assert.IsType<Button>(window.FindName("InstallUpdateButton"));
            var navigation = Assert.IsType<ListBox>(window.FindName("SettingsNavigation"));
            Assert.Equal(5, navigation.Items.Count);
            navigation.SelectedIndex = 4;
            window.UpdateLayout();
            var updatePanel = Assert.IsType<ScrollViewer>(
                window.FindName("UpdateSettingsPanel"));
            Assert.Equal(Visibility.Visible, updatePanel.Visibility);
            window.RequestExit();
        });
    }

    [Fact]
    public void UpdateNavigationTextFitsTheSidebarWithoutASeparateBadge()
    {
        WpfTestHost.Invoke(() =>
        {
            using var hotKeyManager = new GlobalHotKeyManager();
            var window = new MainWindow(
                AppSettings.CreateDefault(),
                new SettingsStore(Path.Combine(
                    Path.GetTempPath(),
                    "Screenshot.App.Tests",
                    "update-badge-layout-settings.json")),
                new FakeStartupRegistrationService(),
                hotKeyManager,
                new FakeTranslationCredentialStore());

            window.ApplySettingsPalette(AppTheme.Light);
            window.ShowFromTray();
            var navigation = Assert.IsType<ListBox>(
                window.FindName("SettingsNavigation"));
            var updateText = Assert.IsType<TextBlock>(
                window.FindName("UpdateNavigationText"));
            window.SetUpdateNavigationState(new Version(2, 2, 2));
            window.UpdateLayout();

            var textPosition = updateText.TranslatePoint(new Point(), navigation);
            Assert.Equal("有新版本", updateText.Text);
            Assert.Same(
                window.FindResource("AppWarmAccentBrush"),
                updateText.Foreground);
            Assert.Equal("发现 2.2.2，点击查看", updateText.ToolTip);
            Assert.True(textPosition.X >= 0);
            Assert.True(
                textPosition.X + updateText.ActualWidth <= navigation.ActualWidth,
                $"Update text exceeded navigation bounds: " +
                $"right={textPosition.X + updateText.ActualWidth}, " +
                $"width={navigation.ActualWidth}.");
            Assert.Null(window.FindName("UpdateBadge"));

            window.SetUpdateNavigationState(availableVersion: null);
            Assert.Equal("版本更新", updateText.Text);
            Assert.Equal(
                DependencyProperty.UnsetValue,
                updateText.ReadLocalValue(TextBlock.ForegroundProperty));
            Assert.Null(updateText.ToolTip);

            window.RequestExit();
        });
    }

    private sealed class FakeStartupRegistrationService : IStartupRegistrationService
    {
        public bool IsEnabled()
        {
            return false;
        }

        public void SetEnabled(bool enabled)
        {
        }
    }

    private sealed class FakeTranslationCredentialStore : ITranslationCredentialStore
    {
        public string? GetApiKey(string providerId)
        {
            return null;
        }

        public void SetApiKey(string providerId, string? apiKey)
        {
        }
    }
}
