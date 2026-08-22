using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Reflection;
using Screenshot.App.Core;
using Screenshot.App.Infrastructure;
using Screenshot.App.Presentation;
using Screenshot.App.Text;

namespace Screenshot.App.Tests;

public sealed class ImmediateSettingsTests : IDisposable
{
    private static readonly string[] RefreshedTranslationModels =
        ["model-a", "model-b"];
    private static readonly object?[] TranslationModelRefreshArguments =
        [RefreshedTranslationModels];
    private readonly string _testDirectory = Path.Combine(
        Path.GetTempPath(),
        "Screenshot.App.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void GeneralSettingsAreSeparatedIntoScannableGroups()
    {
        Directory.CreateDirectory(_testDirectory);
        var settingsStore = new SettingsStore(
            Path.Combine(_testDirectory, "grouped-settings.json"));

        WpfTestHost.Invoke(() =>
        {
            using var hotKeyManager = new GlobalHotKeyManager();
            var window = new MainWindow(
                CreateSettings(),
                settingsStore,
                new FakeStartupRegistrationService(),
                hotKeyManager,
                new FakeTranslationCredentialStore());
            try
            {
                Assert.Equal("保存与外观", GetGroupTitle(
                    window,
                    "GeneralAppearanceGroup"));
                Assert.Equal("窗口与后台", GetGroupTitle(
                    window,
                    "GeneralWindowGroup"));
                Assert.Equal("悬浮截图", GetGroupTitle(
                    window,
                    "GeneralFloatingCaptureGroup"));
                Assert.Equal("历史记录", GetGroupTitle(
                    window,
                    "GeneralHistoryGroup"));
            }
            finally
            {
                window.RequestExit();
            }
        });
    }

    [Fact]
    public void SavesScreenshotAndVideoRetentionPeriodsIndependently()
    {
        Directory.CreateDirectory(_testDirectory);
        var settingsPath = Path.Combine(_testDirectory, "retention-settings.json");
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
            try
            {
                var screenshotRetention = Assert.IsType<TextBox>(
                    window.FindName("ScreenshotHistoryRetentionDaysTextBox"));
                var screenshotLimit = Assert.IsType<TextBox>(
                    window.FindName("ScreenshotHistoryLimitTextBox"));
                var videoRetention = Assert.IsType<TextBox>(
                    window.FindName("VideoHistoryRetentionDaysTextBox"));
                var videoLimit = Assert.IsType<TextBox>(
                    window.FindName("VideoHistoryLimitTextBox"));

                screenshotRetention.Text = "15";
                screenshotLimit.Text = "40";
                videoRetention.Text = "30";
                videoLimit.Text = "60";
                var applyMethod = typeof(MainWindow).GetMethod(
                    "ApplySettingsImmediately",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.NotNull(applyMethod);
                applyMethod.Invoke(window, [null]);
            }
            finally
            {
                window.RequestExit();
            }
        });

        var loaded = settingsStore.Load();
        Assert.Null(loaded.Warning);
        Assert.Equal(15, loaded.Settings.ScreenshotHistoryRetentionDays);
        Assert.Equal(30, loaded.Settings.VideoHistoryRetentionDays);
        Assert.Equal(40, loaded.Settings.HistoryLimit);
        Assert.Equal(60, loaded.Settings.VideoHistoryLimit);
    }

    [Fact]
    public void RestoresRetentionDaysAfterTogglingKeepAll()
    {
        var viewModel = new SettingsViewModel(CreateSettings() with
        {
            ScreenshotHistoryRetentionDays = 15,
            VideoHistoryRetentionDays = 30,
        });

        viewModel.KeepAllScreenshotHistory = true;
        viewModel.KeepAllVideoHistory = true;
        Assert.Equal("15", viewModel.ScreenshotHistoryRetentionDaysText);
        Assert.Equal("30", viewModel.VideoHistoryRetentionDaysText);
        Assert.Equal(0, viewModel.CreateSettings().ScreenshotHistoryRetentionDays);
        Assert.Equal(0, viewModel.CreateSettings().VideoHistoryRetentionDays);

        viewModel.KeepAllScreenshotHistory = false;
        viewModel.KeepAllVideoHistory = false;
        Assert.Equal("15", viewModel.ScreenshotHistoryRetentionDaysText);
        Assert.Equal("30", viewModel.VideoHistoryRetentionDaysText);
        Assert.Equal(15, viewModel.CreateSettings().ScreenshotHistoryRetentionDays);
        Assert.Equal(30, viewModel.CreateSettings().VideoHistoryRetentionDays);
    }

    [Fact]
    public void KeepAllDisablesBothRetentionEditors()
    {
        Directory.CreateDirectory(_testDirectory);
        var settingsStore = new SettingsStore(
            Path.Combine(_testDirectory, "disabled-retention-settings.json"));

        WpfTestHost.Invoke(() =>
        {
            using var hotKeyManager = new GlobalHotKeyManager();
            var window = new MainWindow(
                CreateSettings(),
                settingsStore,
                new FakeStartupRegistrationService(),
                hotKeyManager,
                new FakeTranslationCredentialStore());
            window.Show();
            try
            {
                var screenshotRetention = Assert.IsType<TextBox>(
                    window.FindName("ScreenshotHistoryRetentionDaysTextBox"));
                var screenshotLimit = Assert.IsType<TextBox>(
                    window.FindName("ScreenshotHistoryLimitTextBox"));
                var screenshotKeepAll = Assert.IsType<CheckBox>(
                    window.FindName("KeepAllScreenshotHistoryCheckBox"));
                var videoRetention = Assert.IsType<TextBox>(
                    window.FindName("VideoHistoryRetentionDaysTextBox"));
                var videoLimit = Assert.IsType<TextBox>(
                    window.FindName("VideoHistoryLimitTextBox"));
                var videoKeepAll = Assert.IsType<CheckBox>(
                    window.FindName("KeepAllVideoHistoryCheckBox"));

                screenshotKeepAll.IsChecked = true;
                videoKeepAll.IsChecked = true;

                Assert.False(screenshotRetention.IsEnabled);
                Assert.False(screenshotLimit.IsEnabled);
                Assert.False(videoRetention.IsEnabled);
                Assert.False(videoLimit.IsEnabled);
            }
            finally
            {
                window.RequestExit();
            }
        });
    }

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
    public void RecordingControlPreferencesAreSavedForTheNextSession()
    {
        Directory.CreateDirectory(_testDirectory);
        var settingsPath = Path.Combine(_testDirectory, "recording-settings.json");
        var settingsStore = new SettingsStore(settingsPath);

        WpfTestHost.Invoke(() =>
        {
            using var hotKeyManager = new GlobalHotKeyManager();
            var window = new MainWindow(
                CreateSettings(),
                settingsStore,
                new FakeStartupRegistrationService(),
                hotKeyManager,
                new FakeTranslationCredentialStore());
            try
            {
                window.SaveVideoRecordingPreferences(
                    new VideoRecordingPreferences(
                        VideoRecordingCodec.H265,
                        60,
                        RecordSystemAudio: false,
                        RecordMicrophone: true,
                        ShowKeyboardInput: true,
                        ShowMouseInput: true,
                        ShowMouseTrail: true,
                        OutputFormat: VideoRecordingOutputFormat.Gif));
            }
            finally
            {
                window.RequestExit();
            }
        });

        var loaded = settingsStore.Load();
        Assert.Null(loaded.Warning);
        Assert.Equal(VideoRecordingCodec.H265, loaded.Settings.VideoRecordingCodec);
        Assert.Equal(60, loaded.Settings.VideoRecordingFrameRate);
        Assert.False(loaded.Settings.RecordSystemAudio);
        Assert.True(loaded.Settings.RecordMicrophone);
        Assert.True(loaded.Settings.ShowKeyboardInputInRecording);
        Assert.True(loaded.Settings.ShowMouseInputInRecording);
        Assert.True(loaded.Settings.ShowMouseTrailInRecording);
        Assert.Equal(
            VideoRecordingOutputFormat.Gif,
            loaded.Settings.RecordingOutputFormat);
    }

    [Fact]
    public void RecordingAnnotationPreferencesAreSavedForTheNextSession()
    {
        Directory.CreateDirectory(_testDirectory);
        var settingsPath = Path.Combine(
            _testDirectory,
            "recording-annotation-settings.json");
        var settingsStore = new SettingsStore(settingsPath);

        WpfTestHost.Invoke(() =>
        {
            using var hotKeyManager = new GlobalHotKeyManager();
            var window = new MainWindow(
                CreateSettings(),
                settingsStore,
                new FakeStartupRegistrationService(),
                hotKeyManager,
                new FakeTranslationCredentialStore());
            try
            {
                window.SaveVideoRecordingAnnotationPreferences(
                    new VideoRecordingAnnotationPreferences(
                        ShapeToolMode.Ellipse,
                        ArrowToolMode.Curved,
                        ArrowStyle.Hollow,
                        "#123456",
                        7));
            }
            finally
            {
                window.RequestExit();
            }
        });

        var loaded = settingsStore.Load();
        Assert.Null(loaded.Warning);
        Assert.Equal(ShapeToolMode.Ellipse, loaded.Settings.ShapeToolMode);
        Assert.Equal(ArrowToolMode.Curved, loaded.Settings.ArrowToolMode);
        Assert.Equal(ArrowStyle.Hollow, loaded.Settings.ArrowStyle);
        Assert.Equal("#123456", loaded.Settings.CustomStrokeColor);
        Assert.Equal(7, loaded.Settings.DefaultStrokeWidth);
    }

    private static string GetGroupTitle(MainWindow window, string groupName)
    {
        var group = Assert.IsType<StackPanel>(window.FindName(groupName));
        return Assert.IsType<TextBlock>(group.Children[0]).Text;
    }

    [Fact]
    public void AppliesFloatingCaptureSettingsImmediately()
    {
        Directory.CreateDirectory(_testDirectory);
        var settingsPath = Path.Combine(_testDirectory, "floating-settings.json");
        var settingsStore = new SettingsStore(settingsPath);

        WpfTestHost.Invoke(() =>
        {
            using var hotKeyManager = new GlobalHotKeyManager();
            var window = new MainWindow(
                CreateSettings(),
                settingsStore,
                new FakeStartupRegistrationService(),
                hotKeyManager,
                new FakeTranslationCredentialStore());

            window.Show();
            var enabled = Assert.IsType<CheckBox>(
                window.FindName("ShowFloatingCaptureButtonCheckBox"));
            var clickBehavior = Assert.IsType<ComboBox>(
                window.FindName("FloatingCaptureClickBehaviorComboBox"));
            enabled.IsChecked = true;
            Assert.Equal(7, clickBehavior.Items.Count);
            clickBehavior.SelectedValue =
                FloatingCaptureClickBehavior.CaptureAllScreens;
            window.RequestExit();
        });

        var loaded = settingsStore.Load();
        Assert.Null(loaded.Warning);
        Assert.True(loaded.Settings.ShowFloatingCaptureButton);
        Assert.Equal(
            FloatingCaptureClickBehavior.CaptureAllScreens,
            loaded.Settings.FloatingCaptureClickBehavior);
    }

    [Fact]
    public void FloatingCloseRequestCanPersistentlyDisableTheButton()
    {
        Directory.CreateDirectory(_testDirectory);
        var settingsPath = Path.Combine(_testDirectory, "floating-close.json");
        var settingsStore = new SettingsStore(settingsPath);

        WpfTestHost.Invoke(() =>
        {
            using var hotKeyManager = new GlobalHotKeyManager();
            var window = new MainWindow(
                CreateSettings() with { ShowFloatingCaptureButton = true },
                settingsStore,
                new FakeStartupRegistrationService(),
                hotKeyManager,
                new FakeTranslationCredentialStore());

            window.Show();
            window.SetFloatingCaptureButtonEnabled(false);
            window.RequestExit();
        });

        var loaded = settingsStore.Load();
        Assert.Null(loaded.Warning);
        Assert.False(loaded.Settings.ShowFloatingCaptureButton);
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
            var offlineQuality = Assert.IsType<ComboBox>(
                window.FindName("OfflineTranslationQualityComboBox"));
            Assert.IsType<Button>(
                window.FindName("FetchTranslationModelsButton"));

            Assert.NotEmpty(ocrLanguage.Items);
            Assert.Null(window.FindName("TranslationModeComboBox"));
            Assert.Equal(
                TranslationProviderFactory.ProviderDefinitions.Count,
                provider.Items.Count);
            Assert.Equal(
                "自定义兼容接口",
                Assert.IsType<SettingOption>(provider.Items[0]).Label);
            Assert.True(targetLanguage.Items.Count >= 5);
            Assert.True(model.IsEditable);
            Assert.Equal("deepseek-v4-flash", model.Text);
            Assert.Equal(
                OfflineTranslationQuality.High,
                offlineQuality.SelectedValue);

            targetLanguage.SelectedValue = "en";
            offlineQuality.SelectedValue = OfflineTranslationQuality.Ultra;
            window.RequestExit();
        });

        var loaded = settingsStore.Load();
        Assert.Null(loaded.Warning);
        Assert.Equal("en", loaded.Settings.TranslationTargetLanguage);
        Assert.Equal("deepseek-v4-flash", loaded.Settings.TranslationModel);
        Assert.Equal(
            OfflineTranslationQuality.Ultra,
            loaded.Settings.OfflineTranslationQuality);
    }

    [Fact]
    public void SelectingBuiltInProviderShowsItsOfficialEndpoint()
    {
        var viewModel = new SettingsViewModel(CreateSettings() with
        {
            TranslationProvider = TranslationProviderFactory.OpenAiCompatibleProviderId,
            TranslationEndpoint = "https://proxy.example/v1",
        });

        Assert.Equal(
            "https://proxy.example/v1",
            viewModel.TranslationEndpoint);
        Assert.Equal(
            "自定义兼容接口",
            viewModel.SelectedTranslationProvider.DisplayName);

        viewModel.TranslationProvider = "DeepSeek";
        viewModel.TranslationEndpoint =
            viewModel.SelectedTranslationProvider.OfficialEndpoint;

        Assert.Equal("https://api.deepseek.com", viewModel.TranslationEndpoint);
        Assert.Equal(
            "官方接口：https://api-docs.deepseek.com/",
            viewModel.SelectedTranslationProvider.OfficialSite);
    }

    [Fact]
    public void TranslationAvailabilitySurvivesPriorityReordering()
    {
        var viewModel = new SettingsViewModel(CreateSettings());
        viewModel.UpdateTranslationProviderAvailability(
            TranslationProviderKind.Online,
            isAvailable: true,
            "模型已验证");
        viewModel.UpdateTranslationProviderAvailability(
            TranslationProviderKind.Offline,
            isAvailable: false,
            "离线模型尚未下载");

        Assert.True(viewModel.MoveTranslationProvider(
            TranslationProviderKind.Offline,
            offset: -1));

        var online = Assert.Single(
            viewModel.TranslationPriorityItems,
            item => item.Provider == TranslationProviderKind.Online);
        var offline = Assert.Single(
            viewModel.TranslationPriorityItems,
            item => item.Provider == TranslationProviderKind.Offline);
        Assert.True(online.IsAvailable);
        Assert.Equal("可用", online.AvailabilityLabel);
        Assert.Equal("模型已验证", online.AvailabilityReason);
        Assert.False(offline.IsAvailable);
        Assert.Equal("不可用", offline.AvailabilityLabel);
        Assert.Equal("离线模型尚未下载", offline.AvailabilityReason);
    }

    [Fact]
    public void CaptureToolbarChoicesRoundTripThroughSettingsViewModel()
    {
        var viewModel = new SettingsViewModel(CreateSettings() with
        {
            VisibleCaptureToolbarFeatures =
            [
                CaptureToolbarFeature.Shape,
                CaptureToolbarFeature.Translation,
            ],
            CaptureToolbarFeatureOrder =
            [
                CaptureToolbarFeature.Text,
                CaptureToolbarFeature.Shape,
                CaptureToolbarFeature.Translation,
            ],
            CaptureToolbarRows = CaptureToolbarRowCount.Two,
        });

        Assert.True(Assert.Single(
            viewModel.CaptureToolbarFeatureItems,
            item => item.Feature == CaptureToolbarFeature.Shape).IsVisible);
        Assert.False(Assert.Single(
            viewModel.CaptureToolbarFeatureItems,
            item => item.Feature == CaptureToolbarFeature.Save).IsVisible);
        Assert.False(Assert.Single(
            viewModel.CaptureToolbarFeatureItems,
            item => item.Feature == CaptureToolbarFeature.CopyRecognizedText).IsVisible);
        Assert.Equal(
            CaptureToolbarFeature.Text,
            viewModel.CaptureToolbarFeatureItems[0].Feature);
        Assert.Equal(CaptureToolbarRowCount.Two, viewModel.CaptureToolbarRows);

        Assert.True(viewModel.MoveCaptureToolbarFeature(
            CaptureToolbarFeature.Shape,
            CaptureToolbarFeature.Text));
        Assert.False(viewModel.MoveCaptureToolbarFeature(
            CaptureToolbarFeature.Shape,
            CaptureToolbarFeature.Translation));

        foreach (var item in viewModel.CaptureToolbarFeatureItems)
        {
            item.IsVisible = item.Feature == CaptureToolbarFeature.PinImage;
        }

        Assert.Equal(
            [CaptureToolbarFeature.PinImage],
            viewModel.CreateSettings().VisibleCaptureToolbarFeatures);
        Assert.Equal(
            CaptureToolbarFeature.Shape,
            viewModel.CreateSettings().CaptureToolbarFeatureOrder[0]);
        Assert.Equal(
            CaptureToolbarFeature.Text,
            viewModel.CreateSettings().CaptureToolbarFeatureOrder[1]);
        Assert.Equal(
            CaptureToolbarRowCount.Two,
            viewModel.CreateSettings().CaptureToolbarRows);
    }

    [Fact]
    public void TranslationModelKeepsItsValueWhenTheCatalogIsRefreshed()
    {
        Directory.CreateDirectory(_testDirectory);
        WpfTestHost.Invoke(() =>
        {
            using var hotKeyManager = new GlobalHotKeyManager();
            var window = new MainWindow(
                CreateSettings() with
                {
                    TranslationEndpoint = "https://proxy.example/v1",
                    TranslationModel = "selected-model",
                },
                new SettingsStore(Path.Combine(
                    _testDirectory,
                    "settings-model-preservation.json")),
                new FakeStartupRegistrationService(),
                hotKeyManager,
                new FakeTranslationCredentialStore());
            window.Show();
            var model = Assert.IsType<ComboBox>(
                window.FindName("TranslationModelComboBox"));
            var refreshMethod = typeof(MainWindow).GetMethod(
                "SetTranslationModelsPreservingSelection",
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic);
            Assert.NotNull(refreshMethod);

            refreshMethod.Invoke(window, TranslationModelRefreshArguments);

            Assert.Equal("selected-model", model.Text);
            Assert.Contains("selected-model", model.Items.Cast<string>());
            window.RequestExit();
        });
    }

    [Fact]
    public void CollapsedTranslationModelBoxDoesNotConsumePageScrolling()
    {
        Directory.CreateDirectory(_testDirectory);
        WpfTestHost.Invoke(() =>
        {
            using var hotKeyManager = new GlobalHotKeyManager();
            var window = new MainWindow(
                CreateSettings() with
                {
                    TranslationEndpoint = "https://proxy.example/v1",
                    TranslationModel = "selected-model",
                },
                new SettingsStore(Path.Combine(
                    _testDirectory,
                    "settings-model-wheel.json")),
                new FakeStartupRegistrationService(),
                hotKeyManager,
                new FakeTranslationCredentialStore());
            window.Show();
            var model = Assert.IsType<ComboBox>(
                window.FindName("TranslationModelComboBox"));
            var wheel = new MouseWheelEventArgs(
                Mouse.PrimaryDevice,
                Environment.TickCount,
                delta: -120)
            {
                RoutedEvent = UIElement.PreviewMouseWheelEvent,
            };

            model.RaiseEvent(wheel);

            Assert.True(wheel.Handled);
            Assert.Equal("selected-model", model.Text);
            window.RequestExit();
        });
    }

    [Fact]
    public void ProviderSelectionUpdatesEndpointAndModelInTheSettingsWindow()
    {
        Directory.CreateDirectory(_testDirectory);
        var settingsStore = new SettingsStore(
            Path.Combine(_testDirectory, "settings-provider-selection.json"));

        WpfTestHost.Invoke(() =>
        {
            using var hotKeyManager = new GlobalHotKeyManager();
            var window = new MainWindow(
                CreateSettings() with
                {
                    TranslationEndpoint = "https://proxy.example/v1",
                    TranslationModel = "proxy-model",
                },
                settingsStore,
                new FakeStartupRegistrationService(),
                hotKeyManager,
                new FakeTranslationCredentialStore());

            window.Show();
            var provider = Assert.IsType<ComboBox>(
                window.FindName("TranslationProviderComboBox"));
            var endpoint = Assert.IsType<TextBox>(
                window.FindName("TranslationEndpointTextBox"));
            var model = Assert.IsType<ComboBox>(
                window.FindName("TranslationModelComboBox"));
            var officialSite = Assert.IsType<TextBlock>(
                window.FindName("TranslationProviderOfficialSiteText"));

            provider.SelectedValue = "GoogleGemini";
            Assert.Equal(
                "https://generativelanguage.googleapis.com/v1beta/openai",
                endpoint.Text);
            Assert.Equal("proxy-model", model.Text);
            Assert.Contains("ai.google.dev", officialSite.Text);

            window.RequestExit();
        });

        var loaded = settingsStore.Load();
        Assert.Null(loaded.Warning);
        Assert.Equal("GoogleGemini", loaded.Settings.TranslationProvider);
        Assert.Equal(
            "https://generativelanguage.googleapis.com/v1beta/openai",
            loaded.Settings.TranslationEndpoint);
        Assert.Equal("proxy-model", loaded.Settings.TranslationModel);
    }

    [Fact]
    public void TranslationSettingsAlwaysShowPriorityAndBothProviders()
    {
        Directory.CreateDirectory(_testDirectory);
        var settingsPath = Path.Combine(_testDirectory, "settings.json");
        var settingsStore = new SettingsStore(settingsPath);
        var initialSettings = CreateSettings() with
        {
            TranslationMode = TranslationMode.Disabled,
            SendTextToOnlineTranslation = false,
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
            var onlinePanel = Assert.IsType<StackPanel>(
                window.FindName("OnlineTranslationSettingsPanel"));
            var offlinePanel = Assert.IsType<StackPanel>(
                window.FindName("OfflineTranslationSettingsPanel"));
            var priorityPanel = Assert.IsType<StackPanel>(
                window.FindName("TranslationPriorityPanel"));
            var targetLanguage = Assert.IsType<ComboBox>(
                window.FindName("TranslationTargetLanguageComboBox"));
            Assert.Null(window.FindName("TranslationModeComboBox"));
            Assert.Equal(Visibility.Visible, onlinePanel.Visibility);
            Assert.Equal(Visibility.Visible, offlinePanel.Visibility);
            Assert.Equal(Visibility.Visible, priorityPanel.Visibility);
            Assert.Equal(
                TranslationLanguageCatalog.Languages.Count,
                targetLanguage.Items.Count);
            var viewModel = Assert.IsType<SettingsViewModel>(window.DataContext);
            Assert.Equal(
                TranslationMode.Automatic,
                viewModel.CreateSettings().TranslationMode);
            window.RequestExit();
        });
    }

    [Fact]
    public void AutomaticTranslationShowsBothProvidersAndPriorityControls()
    {
        Directory.CreateDirectory(_testDirectory);
        var settingsPath = Path.Combine(_testDirectory, "settings.json");
        var settingsStore = new SettingsStore(settingsPath);
        var initialSettings = CreateSettings() with
        {
            TranslationMode = TranslationMode.Automatic,
            TranslationProviderPriority =
            [
                TranslationProviderKind.Online,
                TranslationProviderKind.Offline,
            ],
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
            var onlinePanel = Assert.IsType<StackPanel>(
                window.FindName("OnlineTranslationSettingsPanel"));
            var offlinePanel = Assert.IsType<StackPanel>(
                window.FindName("OfflineTranslationSettingsPanel"));
            var priorityPanel = Assert.IsType<StackPanel>(
                window.FindName("TranslationPriorityPanel"));
            var targetLanguage = Assert.IsType<ComboBox>(
                window.FindName("TranslationTargetLanguageComboBox"));

            Assert.Equal(Visibility.Visible, onlinePanel.Visibility);
            Assert.Equal(Visibility.Visible, offlinePanel.Visibility);
            Assert.Equal(Visibility.Visible, priorityPanel.Visibility);
            Assert.Equal(
                TranslationLanguageCatalog.Languages.Count,
                targetLanguage.Items.Count);

            window.RequestExit();
        });
    }

    [Fact]
    public void TranslationPriorityOrderIsIncludedInCreatedSettings()
    {
        var viewModel = new SettingsViewModel(CreateSettings() with
        {
            TranslationMode = TranslationMode.Automatic,
            TranslationProviderPriority =
            [
                TranslationProviderKind.Online,
                TranslationProviderKind.Offline,
            ],
        });

        Assert.True(viewModel.MoveTranslationProvider(
            TranslationProviderKind.Offline,
            -1));
        var settings = viewModel.CreateSettings();

        Assert.Equal(TranslationMode.Automatic, settings.TranslationMode);
        Assert.True(settings.SendTextToOnlineTranslation);
        Assert.Equal(
            [TranslationProviderKind.Offline, TranslationProviderKind.Online],
            settings.TranslationProviderPriority);
    }

    [Fact]
    public void AppliesThemeAndAvailableHotKeyChangesWhenOneBindingIsOccupied()
    {
        Directory.CreateDirectory(_testDirectory);
        var settingsPath = Path.Combine(_testDirectory, "settings.json");
        var settingsStore = new SettingsStore(settingsPath);
        var initialSettings = CreateSettings() with
        {
            Theme = AppTheme.ForestNight,
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
                window.FindName("AuroraMistThemeOption"));
            lightTheme.IsChecked = true;
            var background = Assert.IsType<LinearGradientBrush>(
                window.Resources["AppWindowBackgroundBrush"]);
            Assert.Equal(
                Color.FromRgb(0xF7, 0xF8, 0xFB),
                background.GradientStops[0].Color);
            Assert.Null(window.FindName("CustomAccentButton"));
            Assert.NotNull(window.FindName("NeonDeepThemeOption"));

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
        Assert.Equal(AppTheme.AuroraMist, loaded.Settings.Theme);
        Assert.Equal("Ctrl+Alt+Shift+F18", loaded.Settings.RegionCaptureHotKey);
        Assert.Equal(initialSettings.PinHotKey, loaded.Settings.PinHotKey);
        Assert.True(settingsSavedCount >= 2);
    }

    [Fact]
    public void VideoRecordingShortcutIsVisibleSavedAndRegisteredImmediately()
    {
        Directory.CreateDirectory(_testDirectory);
        var settingsPath = Path.Combine(_testDirectory, "settings.json");
        var settingsStore = new SettingsStore(settingsPath);
        var initialSettings = CreateSettings();
        settingsStore.Save(initialSettings);

        WpfTestHost.Invoke(() =>
        {
            using var hotKeyManager = new GlobalHotKeyManager();
            Assert.True(hotKeyManager.ApplyAvailable(
                HotKeyConfiguration.CreateBindings(initialSettings)).IsSuccess);
            var window = new MainWindow(
                initialSettings,
                settingsStore,
                new FakeStartupRegistrationService(),
                hotKeyManager,
                new FakeTranslationCredentialStore());
            window.Show();

            var captureBox = Assert.IsType<HotKeyCaptureBox>(
                window.FindName("VideoRecordingHotKeyBox"));
            captureBox.ProcessCapturedVirtualKey(
                virtualKey: 0x82,
                HotKeyModifiers.Control |
                HotKeyModifiers.Alt |
                HotKeyModifiers.Shift);

            Assert.Contains(
                hotKeyManager.RegisteredBindings,
                binding =>
                    binding.Action == HotKeyAction.VideoRecording &&
                    binding.Gesture.VirtualKey == 0x82);
            window.RequestExit();
        });

        var loaded = settingsStore.Load();
        Assert.Null(loaded.Warning);
        Assert.Equal(
            "Ctrl+Alt+Shift+F19",
            loaded.Settings.VideoRecordingHotKey);
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

    [Fact]
    public async Task FolderPickerWorkRunsOnADedicatedStaThread()
    {
        var callerThreadId = Environment.CurrentManagedThreadId;
        var result = await MainWindow.RunOnStaThreadAsync(() =>
            (ThreadId: Environment.CurrentManagedThreadId,
             Apartment: Thread.CurrentThread.GetApartmentState()));

        Assert.NotEqual(callerThreadId, result.ThreadId);
        Assert.Equal(ApartmentState.STA, result.Apartment);
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
