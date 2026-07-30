using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Screenshot.App.Core;
using Screenshot.App.Infrastructure;
using Screenshot.App.Presentation;
using Screenshot.App.Text;

namespace Screenshot.App.Tests;

public sealed class ImmediateSettingsTests : IDisposable
{
    private readonly string _testDirectory = Path.Combine(
        Path.GetTempPath(),
        "Screenshot.App.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void AppliesNotificationIconChangesWithoutASeparateSaveCommand()
    {
        Directory.CreateDirectory(_testDirectory);
        var settingsPath = Path.Combine(_testDirectory, "settings.json");
        var settingsStore = new SettingsStore(settingsPath);
        var initialSettings = CreateSettings();

        WpfTestHost.Invoke(() =>
        {
            using var hotKeyManager = new GlobalHotKeyManager();
            var window = new MainWindow(
                initialSettings,
                settingsStore,
                new FakeStartupRegistrationService(),
                hotKeyManager,
                new FakeTranslationCredentialStore());

            window.Show();
            var checkBox = Assert.IsType<CheckBox>(
                window.FindName("ShowNotificationIconCheckBox"));
            checkBox.IsChecked = false;
            window.RequestExit();
        });

        var loaded = settingsStore.Load();

        Assert.Null(loaded.Warning);
        Assert.False(loaded.Settings.ShowNotificationIcon);
    }

    [Fact]
    public void SelectionSettingsUseDropDownsAndSaveImmediately()
    {
        Directory.CreateDirectory(_testDirectory);
        var settingsPath = Path.Combine(_testDirectory, "settings.json");
        var settingsStore = new SettingsStore(settingsPath);
        var initialSettings = CreateSettings() with
        {
            TranslationEndpoint = "https://api.deepseek.com",
            TranslationModel = "DeepSeek",
            TranslationTargetLanguage = "zh-Hans",
        };

        WpfTestHost.Invoke(() =>
        {
            using var hotKeyManager = new GlobalHotKeyManager();
            var window = new MainWindow(
                initialSettings,
                settingsStore,
                new FakeStartupRegistrationService(),
                hotKeyManager,
                new FakeTranslationCredentialStore());

            window.Show();
            var ocrLanguage = Assert.IsType<ComboBox>(
                window.FindName("OcrLanguageComboBox"));
            var provider = Assert.IsType<ComboBox>(
                window.FindName("TranslationProviderComboBox"));
            var translationMode = Assert.IsType<ComboBox>(
                window.FindName("TranslationModeComboBox"));
            var targetLanguage = Assert.IsType<ComboBox>(
                window.FindName("TranslationTargetLanguageComboBox"));
            var model = Assert.IsType<ComboBox>(
                window.FindName("TranslationModelComboBox"));
            Assert.IsType<Button>(
                window.FindName("FetchTranslationModelsButton"));

            Assert.NotEmpty(ocrLanguage.Items);
            Assert.Equal(3, translationMode.Items.Count);
            Assert.Single(provider.Items);
            Assert.True(targetLanguage.Items.Count >= 5);
            Assert.True(model.IsEditable);
            Assert.Equal("deepseek-v4-flash", model.Text);

            targetLanguage.SelectedValue = "en";
            window.RequestExit();
        });

        var loaded = settingsStore.Load();
        Assert.Null(loaded.Warning);
        Assert.Equal("en", loaded.Settings.TranslationTargetLanguage);
        Assert.Equal("deepseek-v4-flash", loaded.Settings.TranslationModel);
    }

    [Fact]
    public void TranslationModeSelectionSavesOfflineWithoutOnlineConsent()
    {
        Directory.CreateDirectory(_testDirectory);
        var settingsPath = Path.Combine(_testDirectory, "settings.json");
        var settingsStore = new SettingsStore(settingsPath);
        var initialSettings = CreateSettings() with
        {
            TranslationMode = TranslationMode.Online,
            SendTextToOnlineTranslation = true,
        };

        WpfTestHost.Invoke(() =>
        {
            using var hotKeyManager = new GlobalHotKeyManager();
            var window = new MainWindow(
                initialSettings,
                settingsStore,
                new FakeStartupRegistrationService(),
                hotKeyManager,
                new FakeTranslationCredentialStore());

            window.Show();
            var mode = Assert.IsType<ComboBox>(
                window.FindName("TranslationModeComboBox"));
            var onlinePanel = Assert.IsType<StackPanel>(
                window.FindName("OnlineTranslationSettingsPanel"));
            var offlinePanel = Assert.IsType<StackPanel>(
                window.FindName("OfflineTranslationSettingsPanel"));
            Assert.Equal(Visibility.Visible, onlinePanel.Visibility);
            Assert.Equal(Visibility.Collapsed, offlinePanel.Visibility);

            mode.SelectedValue = nameof(TranslationMode.Offline);

            Assert.Equal(Visibility.Collapsed, onlinePanel.Visibility);
            Assert.Equal(Visibility.Visible, offlinePanel.Visibility);
            window.RequestExit();
        });

        var loaded = settingsStore.Load();
        Assert.Equal(TranslationMode.Offline, loaded.Settings.TranslationMode);
        Assert.False(loaded.Settings.SendTextToOnlineTranslation);
    }

    [Fact]
    public void AppliesThemeAndAvailableHotKeyChangesWhenOneBindingIsOccupied()
    {
        Directory.CreateDirectory(_testDirectory);
        var settingsPath = Path.Combine(_testDirectory, "settings.json");
        var settingsStore = new SettingsStore(settingsPath);
        var initialSettings = CreateSettings() with
        {
            Theme = AppTheme.Dark,
        };
        var settingsSavedCount = 0;

        WpfTestHost.Invoke(() =>
        {
            using var blocker = new GlobalHotKeyManager();
            using var hotKeyManager = new GlobalHotKeyManager();
            var initialBindings = HotKeyConfiguration.CreateBindings(initialSettings);
            var blockedPinBinding = Assert.Single(
                initialBindings,
                binding => binding.Action == HotKeyAction.PinImage);

            Assert.True(blocker.Apply([blockedPinBinding]).IsSuccess);
            Assert.False(hotKeyManager.ApplyAvailable(initialBindings).IsSuccess);

            var window = new MainWindow(
                initialSettings,
                settingsStore,
                new FakeStartupRegistrationService(),
                hotKeyManager,
                new FakeTranslationCredentialStore());
            window.SettingsSaved += (_, eventArgs) =>
            {
                settingsSavedCount++;
                window.ApplySettingsPalette(eventArgs.Settings.Theme);
            };

            window.Show();
            var lightTheme = Assert.IsType<RadioButton>(
                window.FindName("LightThemeOption"));
            lightTheme.IsChecked = true;
            var background = Assert.IsType<LinearGradientBrush>(
                window.Resources["AppWindowBackgroundBrush"]);
            Assert.Equal(
                Color.FromRgb(0xF7, 0xF7, 0xFC),
                background.GradientStops[0].Color);

            var regionCapture = Assert.IsType<HotKeyCaptureBox>(
                window.FindName("RegionCaptureHotKeyBox"));
            regionCapture.ProcessCapturedVirtualKey(
                virtualKey: 0x81,
                HotKeyModifiers.Control |
                HotKeyModifiers.Alt |
                HotKeyModifiers.Shift);

            Assert.Contains(
                hotKeyManager.RegisteredBindings,
                binding =>
                    binding.Action == HotKeyAction.RegionCapture &&
                    binding.Gesture.VirtualKey == 0x81);
            Assert.DoesNotContain(
                hotKeyManager.RegisteredBindings,
                binding => binding.Action == HotKeyAction.PinImage);
            window.RequestExit();
        });

        var loaded = settingsStore.Load();
        Assert.Null(loaded.Warning);
        Assert.Equal(AppTheme.Light, loaded.Settings.Theme);
        Assert.Equal("Ctrl+Alt+Shift+F18", loaded.Settings.RegionCaptureHotKey);
        Assert.Equal(initialSettings.PinHotKey, loaded.Settings.PinHotKey);
        Assert.True(settingsSavedCount >= 2);
    }

    [Fact]
    public void RejectsOnlyTheNewShortcutWhenThatCombinationIsOccupied()
    {
        Directory.CreateDirectory(_testDirectory);
        var settingsPath = Path.Combine(_testDirectory, "settings.json");
        var settingsStore = new SettingsStore(settingsPath);
        var initialSettings = CreateSettings();
        settingsStore.Save(initialSettings);

        WpfTestHost.Invoke(() =>
        {
            using var blocker = new GlobalHotKeyManager();
            using var hotKeyManager = new GlobalHotKeyManager();
            var blockedGesture = new HotKeyGesture(
                HotKeyModifiers.Control |
                HotKeyModifiers.Alt |
                HotKeyModifiers.Shift,
                VirtualKey: 0x81);

            Assert.True(blocker.Apply([
                new HotKeyBinding(HotKeyAction.RegionCapture, blockedGesture),
            ]).IsSuccess);
            Assert.True(hotKeyManager.ApplyAvailable(
                HotKeyConfiguration.CreateBindings(initialSettings)).IsSuccess);

            var window = new MainWindow(
                initialSettings,
                settingsStore,
                new FakeStartupRegistrationService(),
                hotKeyManager,
                new FakeTranslationCredentialStore());
            window.Show();
            var regionCapture = Assert.IsType<HotKeyCaptureBox>(
                window.FindName("RegionCaptureHotKeyBox"));

            regionCapture.ProcessCapturedVirtualKey(
                blockedGesture.VirtualKey,
                blockedGesture.Modifiers);

            Assert.Equal(initialSettings.RegionCaptureHotKey, regionCapture.Text);
            Assert.Contains(
                hotKeyManager.RegisteredBindings,
                binding =>
                    binding.Action == HotKeyAction.RegionCapture &&
                    binding.Gesture.ToString() == initialSettings.RegionCaptureHotKey);
            window.RequestExit();
        });

        var loaded = settingsStore.Load();
        Assert.Null(loaded.Warning);
        Assert.Equal(
            initialSettings.RegionCaptureHotKey,
            loaded.Settings.RegionCaptureHotKey);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, recursive: true);
        }
    }

    private AppSettings CreateSettings()
    {
        return AppSettings.CreateDefault() with
        {
            SaveDirectory = Path.Combine(_testDirectory, "captures"),
            RegionCaptureHotKey = "Ctrl+Alt+Shift+F13",
            ScrollCaptureHotKey = "Ctrl+Alt+Shift+F14",
            OcrHotKey = "Ctrl+Alt+Shift+F15",
            PinHotKey = "Ctrl+Alt+Shift+F16",
            OpenSettingsHotKey = "Ctrl+Alt+Shift+F17",
        };
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
