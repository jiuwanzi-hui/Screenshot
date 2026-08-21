using System.IO;
using Screenshot.App.Core;
using Screenshot.App.Infrastructure;
using Screenshot.App.Presentation;
using Screenshot.App.Text;
using Screenshot.App.Update;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Screenshot.App.Tests;

public sealed class MainWindowLifecycleTests
{
    [Fact]
    public void GlobalCheckBoxesUseTheRoundedThemeTemplate()
    {
        WpfTestHost.Invoke(() =>
        {
            var controls = new ResourceDictionary
            {
                Source = new Uri(
                    "/SnapCut;component/Themes/ThemedControls.xaml",
                    UriKind.Relative),
            };
            Application.Current.Resources.MergedDictionaries.Add(controls);
            var checkBox = new CheckBox
            {
                Content = "主题复选框",
                IsChecked = true,
                Style = Assert.IsType<Style>(
                    controls[typeof(CheckBox)]),
            };
            var host = new Window
            {
                Width = 240,
                Height = 120,
                Content = checkBox,
                ShowInTaskbar = false,
            };
            try
            {
                host.Show();
                host.UpdateLayout();

                var border = Assert.IsType<Border>(
                    checkBox.Template.FindName("CheckBoxBorder", checkBox));
                var checkMark = Assert.IsType<System.Windows.Shapes.Path>(
                    checkBox.Template.FindName("CheckMark", checkBox));
                var content = Assert.IsType<ContentPresenter>(
                    checkBox.Template.FindName("CheckBoxContent", checkBox));

                Assert.Equal(new CornerRadius(5), border.CornerRadius);
                Assert.Equal(Visibility.Visible, checkMark.Visibility);
                Assert.Equal(VerticalAlignment.Center, checkBox.VerticalContentAlignment);
                var borderTop = border.TranslatePoint(new Point(), checkBox).Y;
                var contentTop = content.TranslatePoint(new Point(), checkBox).Y;
                Assert.InRange(
                    Math.Abs(
                        borderTop + (border.ActualHeight / 2) -
                        contentTop - (content.ActualHeight / 2)),
                    0,
                    1);
            }
            finally
            {
                host.Close();
                Application.Current.Resources.MergedDictionaries.Remove(controls);
            }
        });
    }

    [Fact]
    public void TaskbarVisibilityChangeIsDeferredUntilTheWindowIsHidden()
    {
        WpfTestHost.Invoke(() =>
        {
            using var hotKeyManager = new GlobalHotKeyManager();
            var window = new MainWindow(
                AppSettings.CreateDefault() with { ShowTaskbarIcon = false },
                new SettingsStore(Path.Combine(
                    Path.GetTempPath(),
                    "Screenshot.App.Tests",
                    "deferred-taskbar-settings.json")),
                new FakeStartupRegistrationService(),
                hotKeyManager,
                new FakeTranslationCredentialStore());

            window.ConfigureTaskbarVisibility(showInTaskbar: false);
            window.ShowFromTray();
            Assert.False(window.ShowInTaskbar);

            window.ConfigureTaskbarVisibility(showInTaskbar: true);
            Assert.True(window.IsVisible);
            Assert.False(window.ShowInTaskbar);

            window.Close();
            Assert.False(window.IsVisible);
            Assert.True(window.ShowInTaskbar);

            window.ShowFromTray();
            Assert.True(window.ShowInTaskbar);
            window.RequestExit();
        });
    }

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
            captureBox.RequestCapture();
            Assert.True(captureBox.Focus());
            Keyboard.Focus(captureBox);
            Assert.True(window.IsCapturingHotKey);
            Assert.True(hotKeyManager.IsKeyboardCaptureActive);

            window.Close();

            Assert.False(window.IsCapturingHotKey);
            Assert.False(hotKeyManager.IsKeyboardCaptureActive);
            window.ShowFromTray();
            window.RequestExit();
        });
    }

    [Fact]
    public void FocusingShortcutEditorAloneDoesNotStartCaptureMode()
    {
        WpfTestHost.Invoke(() =>
        {
            using var hotKeyManager = new GlobalHotKeyManager();
            var window = new MainWindow(
                AppSettings.CreateDefault(),
                new SettingsStore(Path.Combine(
                    Path.GetTempPath(),
                    "Screenshot.App.Tests",
                    "hotkey-focus-settings.json")),
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

            Assert.False(window.IsCapturingHotKey);
            Assert.False(hotKeyManager.IsKeyboardCaptureActive);
            window.RequestExit();
        });
    }

    [Fact]
    public void HookedKeyboardInputUpdatesTheFocusedShortcutAndEndsCaptureMode()
    {
        var settingsPath = Path.Combine(
            Path.GetTempPath(),
            "Screenshot.App.Tests",
            $"hooked-hotkey-{Guid.NewGuid():N}.json");
        var settingsStore = new SettingsStore(settingsPath);

        WpfTestHost.Invoke(() =>
        {
            using var hotKeyManager = new GlobalHotKeyManager();
            var settings = AppSettings.CreateDefault() with
            {
                SaveDirectory = Path.Combine(
                    Path.GetTempPath(),
                    "Screenshot.App.Tests",
                    "hooked-hotkey-captures"),
                RegionCaptureHotKey = "Ctrl+Alt+Shift+F13",
                OcrHotKey = "Ctrl+Alt+Shift+F14",
                PinHotKey = "Ctrl+Alt+Shift+F15",
                OpenSettingsHotKey = "Ctrl+Alt+Shift+F16",
            };
            var window = new MainWindow(
                settings,
                settingsStore,
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
            captureBox.RequestCapture();
            Assert.True(captureBox.Focus());
            Keyboard.Focus(captureBox);

            var consumedControl = hotKeyManager.ProcessKeyboardInputForCapture(
                virtualKey: 0x11,
                isKeyDown: true);
            var consumedAlt = hotKeyManager.ProcessKeyboardInputForCapture(
                virtualKey: 0x12,
                isKeyDown: true);
            var consumedShift = hotKeyManager.ProcessKeyboardInputForCapture(
                virtualKey: 0x10,
                isKeyDown: true);
            var consumedKey = hotKeyManager.ProcessKeyboardInputForCapture(
                virtualKey: 0x81,
                isKeyDown: true);

            Assert.True(consumedControl);
            Assert.True(consumedAlt);
            Assert.True(consumedShift);
            Assert.True(consumedKey);
            Assert.Equal("Ctrl+Alt+Shift+F18", captureBox.Text);
            Assert.False(window.IsCapturingHotKey);
            Assert.False(hotKeyManager.IsKeyboardCaptureActive);
            window.RequestExit();
        });

        var loaded = settingsStore.Load();
        Assert.Null(loaded.Warning);
        Assert.Equal("Ctrl+Alt+Shift+F18", loaded.Settings.RegionCaptureHotKey);
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
            Assert.IsType<ComboBox>(window.FindName("ReleaseHistorySelector"));
            Assert.IsType<Border>(window.FindName("SelectedReleaseDetailsPanel"));
            Assert.IsType<Button>(window.FindName("SelectedReleaseActionButton"));
            var navigation = Assert.IsType<ListBox>(window.FindName("SettingsNavigation"));
            Assert.Null(navigation.FocusVisualStyle);
            Assert.All(
                navigation.Items.Cast<ListBoxItem>(),
                item => Assert.Null(item.FocusVisualStyle));
            Assert.Equal(6, navigation.Items.Count);
            navigation.SelectedIndex = 4;
            window.UpdateLayout();
            var updatePanel = Assert.IsType<ScrollViewer>(
                window.FindName("UpdateSettingsPanel"));
            Assert.Equal(Visibility.Visible, updatePanel.Visibility);
            navigation.SelectedIndex = 5;
            window.UpdateLayout();
            var donatePanel = Assert.IsType<ScrollViewer>(
                window.FindName("DonateSettingsPanel"));
            Assert.Equal(Visibility.Visible, donatePanel.Visibility);
            var donateQrImage = Assert.IsType<Image>(
                window.FindName("DonateQrImage"));
            Assert.NotNull(donateQrImage.Source);
            window.RequestExit();
        });
    }

    [Fact]
    public void ReleaseHistorySelectsCurrentVersionAndOffersVerifiedRollback()
    {
        WpfTestHost.Invoke(() =>
        {
            using var hotKeyManager = new GlobalHotKeyManager();
            var window = new MainWindow(
                AppSettings.CreateDefault(),
                new SettingsStore(Path.Combine(
                    Path.GetTempPath(),
                    "Screenshot.App.Tests",
                    "release-history-settings.json")),
                new FakeStartupRegistrationService(),
                hotKeyManager,
                new FakeTranslationCredentialStore());
            var rollbackVersion = new Version(2, 2, 1);
            var rollbackUpdate = new ApplicationUpdateInfo(
                rollbackVersion,
                new Uri("https://github.com/jiuwanzi-hui/Screenshot/releases/tag/v2.2.1"),
                new ApplicationUpdateAsset(
                    "Screenshot-Setup-2.2.1-win-x64.exe",
                    new Uri("https://github.com/Screenshot-Setup-2.2.1-win-x64.exe"),
                    1,
                    new string('A', 64)),
                new ApplicationUpdateAsset(
                    "Screenshot-Portable-2.2.1-win-x64.zip",
                    new Uri("https://github.com/Screenshot-Portable-2.2.1-win-x64.zip"),
                    1,
                    new string('B', 64)),
                ApplicationUpdateMirror.GitHub);
            var history = new ApplicationReleaseHistoryResult(
                true,
                [
                    new ApplicationReleaseInfo(
                        AppMetadata.CurrentVersion,
                        $"SnapCut {AppMetadata.DisplayVersion}",
                        new DateTimeOffset(2026, 7, 30, 9, 0, 0, TimeSpan.FromHours(8)),
                        "当前版本说明",
                        new Uri("https://github.com/jiuwanzi-hui/Screenshot/releases/latest"),
                        null,
                        null),
                    new ApplicationReleaseInfo(
                        rollbackVersion,
                        "SnapCut 2.2.1",
                        new DateTimeOffset(2026, 7, 29, 20, 39, 5, TimeSpan.FromHours(8)),
                        "修复长截图接缝。",
                        rollbackUpdate.ReleasePage,
                        rollbackUpdate,
                        null),
                ],
                "已加载 2 个正式版本。");

            window.UpdateReleaseHistory(history);

            var selector = Assert.IsType<ComboBox>(
                window.FindName("ReleaseHistorySelector"));
            var details = Assert.IsType<Border>(
                window.FindName("SelectedReleaseDetailsPanel"));
            var action = Assert.IsType<Button>(
                window.FindName("SelectedReleaseActionButton"));
            Assert.Equal(2, selector.Items.Count);
            Assert.Equal(Visibility.Visible, details.Visibility);
            Assert.Equal(Visibility.Collapsed, action.Visibility);

            selector.SelectedIndex = 1;

            Assert.Equal(Visibility.Visible, action.Visibility);
            Assert.Equal("回退到 2.2.1", action.Content);
            Assert.Contains(
                "修复长截图接缝",
                Assert.IsType<TextBlock>(window.FindName("SelectedReleaseNotesText")).Text);

            window.UpdateReleaseHistory(new ApplicationReleaseHistoryResult(
                false,
                [],
                "两个在线源暂时不可用。"));

            Assert.Equal(2, selector.Items.Count);
            Assert.Contains(
                "已保留上次成功读取的版本列表",
                Assert.IsType<TextBlock>(window.FindName("ReleaseHistoryStatusText")).Text);
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

            window.ApplySettingsPalette(AppTheme.AuroraMist);
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
