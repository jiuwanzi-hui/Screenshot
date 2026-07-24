using System.IO;
using System.Windows.Controls;
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
            var targetLanguage = Assert.IsType<ComboBox>(
                window.FindName("TranslationTargetLanguageComboBox"));
            var model = Assert.IsType<ComboBox>(
                window.FindName("TranslationModelComboBox"));
            Assert.IsType<Button>(
                window.FindName("FetchTranslationModelsButton"));

            Assert.NotEmpty(ocrLanguage.Items);
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
