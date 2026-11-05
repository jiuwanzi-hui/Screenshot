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

public sealed partial class ImmediateSettingsTests : IDisposable
{
    [Theory]
    [InlineData(AppTheme.GlassInk)]
    [InlineData(AppTheme.GlassMist)]
    [InlineData(AppTheme.AuroraMist)]
    public void ResizingSettingsImmediatelyReflowsAndKeepsOcrControlsAligned(AppTheme theme)
    {
        WpfTestHost.Invoke(() =>
        {
            using var hotkeys = new GlobalHotKeyManager();
            var window = new MainWindow(CreateSettings() with { Theme = theme },
                new SettingsStore(Path.Combine(_testDirectory, "resize-ocr.json")),
                new FakeStartupRegistrationService(), hotkeys, new FakeTranslationCredentialStore());
            try
            {
                AppThemeManager.ApplySettingsPalette(window.Resources, theme);
                ((ListBox)window.FindName("SettingsNavigation")).SelectedIndex = 2;
                var root = Assert.IsType<Border>(window.Content);
                root.Background = (Brush)window.FindResource("AppWindowBackgroundBrush");
                var page = (ScrollViewer)window.FindName("OcrSettingsPanel");
                var editor = (ComboBox)window.FindName("OcrEngineComboBox");
                var checkbox = (CheckBox)window.FindName("AutoOcrAfterCaptureCheckBox");
                var label = Assert.IsType<TextBlock>(((Grid)checkbox.Parent).Children[0]);
                Assert.Same(window.FindResource("AppTextPrimaryBrush"), label.Foreground);
                foreach (var size in new[] { new Size(980, 720), new Size(840, 700), new Size(620, 500),
                    new Size(480, 560), new Size(1180, 760), new Size(900, 440) })
                {
                    root.Measure(size); root.Arrange(new Rect(size)); root.UpdateLayout();
                    Assert.Equal(size.Width < 860 ? 1 : 0, Grid.GetRow(editor));
                    Assert.Equal(Grid.GetRow(editor), Grid.GetRow(checkbox));
                    var editorBounds = editor.TransformToAncestor(root).TransformBounds(new Rect(editor.RenderSize));
                    var toggleBounds = checkbox.TransformToAncestor(root).TransformBounds(new Rect(checkbox.RenderSize));
                    Assert.Equal(240, editorBounds.Width, 2);
                    Assert.Equal(editorBounds.Right, toggleBounds.Right, 2);
                    var track = Assert.IsType<Border>(checkbox.Template.FindName("SwitchTrack", checkbox));
                    var trackBounds = track.TransformToAncestor(root).TransformBounds(new Rect(track.RenderSize));
                    Assert.Equal(48, trackBounds.Width, 2);
                    Assert.Equal(25, trackBounds.Height, 2);
                    Assert.True(page.ScrollableWidth < 1);
                    if (Environment.GetEnvironmentVariable("SNAPCUT_TEST_RENDER_DIR") is { Length: > 0 } directory &&
                        size.Width is 980 or 620 or 480)
                    {
                        Directory.CreateDirectory(directory);
                        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32);
                        bitmap.Render(root);
                        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                        using var output = File.Create(Path.Combine(directory, FormattableString.Invariant($"settings-{theme}-{size.Width}.png")));
                        encoder.Save(output);
                    }
                }
            }
            finally { window.RequestExit(); }
        });
    }

    [Fact]
    public void AutomaticOcrSwitchLivesInRecognitionSettingsAndPersistsItsClick()
    {
        Directory.CreateDirectory(_testDirectory);
        var store = new SettingsStore(Path.Combine(_testDirectory, "automatic-ocr.json"));
        WpfTestHost.Invoke(() =>
        {
            using var hotkeys = new GlobalHotKeyManager();
            var window = new MainWindow(CreateSettings(), store, new FakeStartupRegistrationService(), hotkeys,
                new FakeTranslationCredentialStore());
            try
            {
                AppThemeManager.ApplySettingsPalette(window.Resources, AppTheme.GlassInk);
                var checkbox = Assert.IsType<CheckBox>(window.FindName("AutoOcrAfterCaptureCheckBox"));
                var panel = Assert.IsType<ScrollViewer>(window.FindName("OcrSettingsPanel"));
                panel.Visibility = Visibility.Visible;
                panel.Background = (Brush)window.FindResource("AppWindowBackgroundBrush");
                panel.Measure(new Size(744, 720));
                panel.Arrange(new Rect(0, 0, 744, 720));
                panel.UpdateLayout();
                Assert.True(panel.IsAncestorOf(checkbox));
                Assert.False(checkbox.IsChecked);
                Assert.Same(window.FindResource("SettingsSwitch"), checkbox.Style);
                if (Environment.GetEnvironmentVariable("SNAPCUT_TEST_RENDER_DIR") is { Length: > 0 } renderDirectory)
                {
                    Directory.CreateDirectory(renderDirectory);
                    var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(744, 720, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(panel);
                    var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                    encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                    using var output = File.Create(Path.Combine(renderDirectory, "automatic-ocr-settings.png"));
                    encoder.Save(output);
                }
                typeof(System.Windows.Controls.Primitives.ToggleButton).GetMethod("OnClick",
                    BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(checkbox, null);
                Assert.True(((SettingsViewModel)window.DataContext).AutoOcrAfterCapture);
                typeof(MainWindow).GetMethod("ApplySettingsImmediately", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(window, [null]);
                Assert.True(store.Load().Settings.AutoOcrAfterCapture);
            }
            finally { window.RequestExit(); }
        });
    }

    [DesktopFact]
    public void EditingSecondOnlineProfileOpensAndNameOnlySavePreservesBothConnections()
    {
        Directory.CreateDirectory(_testDirectory);
        var path = Path.Combine(_testDirectory, "profiles.json");
        WpfTestHost.Invoke(() =>
        {
            var settings = CreateSettings() with
            {
                TranslationProvider = "Zhipu", TranslationEndpoint = "https://open.bigmodel.cn/api/paas/v4",
                TranslationModel = "glm-4-flash",
                TranslationProfiles =
                [new AiTranslationProfile { Id = "first", Name = "第一条", Provider = "Zhipu", Endpoint = "https://open.bigmodel.cn/api/paas/v4", Model = "glm-4-flash", IsEnabled = true },
                 new AiTranslationProfile { Id = "second", Name = "第二条", Provider = "OpenAICompatible", Endpoint = "https://example.test/v1", Model = "gpt-5.6-luna", IsEnabled = true }],
            };
            using var hotkeys = new GlobalHotKeyManager();
            var store = new SettingsStore(path);
            var window = new MainWindow(settings, store, new FakeStartupRegistrationService(), hotkeys,
                new FakeTranslationCredentialStore());
            try
            {
                window.ShowActivated = false;
                window.Show();
                ((ListBox)window.FindName("SettingsNavigation")).SelectedIndex = 3;
                window.UpdateLayout();
                var viewModel = (SettingsViewModel)window.DataContext;
                var second = viewModel.TranslationProfiles.Single(p => p.Id == "second");
                for (var attempt = 0; attempt < 2; attempt++)
                {
                    window.UpdateLayout();
                    var button = VisualChildren(window).OfType<Button>().Single(b =>
                        Equals(b.Content, "编辑") && b.DataContext is AiTranslationProfileItem p && p.Id == "second");
                    Assert.True(button.IsEnabled);
                    Exception? failure = null;
                    var opened = false;
                    window.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        var editor = window.OwnedWindows.OfType<TranslationProfileEditorWindow>().SingleOrDefault();
                        try
                        {
                            Assert.NotNull(editor);
                            opened = true;
                            Assert.Equal("https://example.test/v1", ((TextBox)editor.FindName("EndpointBox")).Text);
                            Assert.Equal("gpt-5.6-luna", ((ComboBox)editor.FindName("ModelBox")).Text);
                            ((TextBox)editor.FindName("NameBox")).Text = "只改名称";
                            VisualChildren(editor).OfType<Button>().Single(b => Equals(b.Content, "保存"))
                                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                            Assert.False(editor.IsVisible, "Saving should close the modal editor.");
                        }
                        catch (Exception exception) { failure = exception; if (editor is not null) editor.Close(); }
                    }), System.Windows.Threading.DispatcherPriority.Background);
                    button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                    Assert.Null(failure);
                    Assert.True(opened);
                    Assert.Equal("https://example.test/v1", second.Endpoint);
                    Assert.Equal("gpt-5.6-luna", second.Model);
                    Assert.Equal("glm-4-flash", viewModel.TranslationProfiles[0].Model);
                }
                var saved = store.Load().Settings;
                Assert.Equal("只改名称", saved.TranslationProfiles.Single(p => p.Id == "second").Name);
                Assert.Equal("gpt-5.6-luna", saved.TranslationProfiles.Single(p => p.Id == "second").Model);
            }
            finally { window.RequestExit(); }
        });
    }

    private static IEnumerable<DependencyObject> VisualChildren(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var nested in VisualChildren(child)) yield return nested;
        }
    }
    private static readonly string[] UniformComboNames = ["OcrEngineComboBox", "OcrLanguageComboBox", "TranslationTargetLanguageComboBox"];
    private static readonly double[] ResponsiveWidths = [980, 600, 480, 980];
    private static readonly string[] UpdateResponsiveActions = ["CheckForUpdatesButton", "SelectedReleaseStateText"];
    private static readonly string[] TranslationResponsiveActions = ["OpenOfflineTextTranslationButton", "AddOnlineTranslationProfileButton"];
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
                Assert.Equal("保存与输出", GetGroupTitle(
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
                var chrome = System.Windows.Shell.WindowChrome.GetWindowChrome(window);
                Assert.NotNull(chrome);
                Assert.Equal(46, chrome.CaptionHeight);
                foreach (var name in UniformComboNames)
                {
                    var combo = Assert.IsType<ComboBox>(window.FindName(name));
                    Assert.Equal(240, combo.Width);
                    Assert.Equal(36, combo.Height);
                }
                var hotKey = Assert.IsType<HotKeyCaptureBox>(window.FindName("RegionCaptureHotKeyBox"));
                Assert.Equal(240, hotKey.Width);
                Assert.Equal(36, hotKey.Height);
            }
            finally
            {
                window.RequestExit();
            }
        });
    }

    [DesktopFact]
    public void NarrowSettingsStackLabelsWithoutChangingControlWidths()
    {
        WpfTestHost.Invoke(() =>
        {
            using var hotKeys = new GlobalHotKeyManager();
            var window = new MainWindow(CreateSettings(),
                new SettingsStore(Path.Combine(_testDirectory, "narrow-settings.json")),
                new FakeStartupRegistrationService(), hotKeys, new FakeTranslationCredentialStore());
            try
            {
                window.ShowActivated = false;
                window.Show();
                var target = Assert.IsType<System.Windows.Interop.HwndTarget>(
                    PresentationSource.FromVisual(window).CompositionTarget);
                Assert.Equal(WpfRenderingCompatibility.GetCurrentSessionRenderMode(), target.RenderMode);
                foreach (var width in ResponsiveWidths)
                {
                    window.Width = width;
                    window.Height = 600;
                    window.Measure(new Size(width, 600));
                    window.Arrange(new Rect(0, 0, width, 600));
                    window.UpdateLayout();
                    window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                    Assert.Equal(width, window.ActualWidth);
                    var editor = Assert.IsType<ComboBox>(window.FindName("OcrEngineComboBox"));
                    var row = Assert.IsType<Grid>(editor.Parent);
                    Assert.Equal(width < 860 ? 1 : 0, Grid.GetRow(editor));
                    Assert.Equal(240, editor.Width);
                    var navigation = Assert.IsType<ListBox>(window.FindName("SettingsNavigation"));
                    var entry = Assert.IsType<ListBoxItem>(navigation.Items[0]);
                    Assert.Equal("常规设置", entry.ToolTip);
                    Assert.True(window.MinWidth <= 480);
                    Assert.True(window.MinHeight <= 360);
                }
            }
            finally { window.RequestExit(); }
        });
    }

    [DesktopTheory]
    [InlineData(AppTheme.GlassInk)]
    [InlineData(AppTheme.ForestNight)]
    public void UpdateAndTranslationActionsStayInsideNarrowViewport(AppTheme theme)
    {
        WpfTestHost.Invoke(() =>
        {
            using var hotKeys = new GlobalHotKeyManager();
            var window = new MainWindow(CreateSettings() with { Theme = theme },
                new SettingsStore(Path.Combine(_testDirectory, "responsive-actions.json")),
                new FakeStartupRegistrationService(), hotKeys, new FakeTranslationCredentialStore());
            try
            {
                window.ShowActivated = false;
                window.Show();
                ((FrameworkElement)window.FindName("GeneralSettingsPanel")).Visibility = Visibility.Collapsed;
                var update = (ScrollViewer)window.FindName("UpdateSettingsPanel");
                var translation = (ScrollViewer)window.FindName("TranslationSettingsPanel");
                var releaseTitle = (TextBlock)window.FindName("SelectedReleaseTitleText");
                var releaseState = (TextBlock)window.FindName("SelectedReleaseStateText");
                releaseTitle.Text = "v3.8.0 · SnapCut 3.8.0";
                releaseState.Text = "可回退";
                for (DependencyObject? parent = releaseState; parent is not null && parent != update; parent = VisualTreeHelper.GetParent(parent))
                    if (parent is FrameworkElement element) element.Visibility = Visibility.Visible;
                foreach (var width in ResponsiveWidths)
                {
                    window.Width = width;
                    window.Height = 720;
                    foreach (var (page, names) in new[]
                    {
                        (update, UpdateResponsiveActions),
                        (translation, TranslationResponsiveActions),
                    })
                    {
                        update.Visibility = page == update ? Visibility.Visible : Visibility.Collapsed;
                        translation.Visibility = page == translation ? Visibility.Visible : Visibility.Collapsed;
                        window.UpdateLayout();
                        window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                        Assert.True(page.ScrollableWidth < 1);
                        foreach (var name in names)
                        {
                            var action = (FrameworkElement)window.FindName(name);
                            var rect = action.TransformToAncestor(page).TransformBounds(new Rect(action.RenderSize));
                            Assert.True(rect.Left >= -1 && rect.Right <= page.ActualWidth + 1,
                                $"{name} at {width}: {rect}, page {page.ActualWidth}");
                        }
                    }
                }
            }
            finally { window.RequestExit(); }
        });
    }

    [DesktopTheory]
    [InlineData(AppTheme.GlassMist)]
    [InlineData(AppTheme.GlassInk)]
    public void ToolbarSettingsWrapShortcutsAndKeepIconsCentered(AppTheme theme)
    {
        WpfTestHost.Invoke(() =>
        {
            using var hotKeys = new GlobalHotKeyManager();
            var window = new MainWindow(CreateSettings() with { Theme = theme },
                new SettingsStore(Path.Combine(_testDirectory, "toolbar-layout.json")),
                new FakeStartupRegistrationService(), hotKeys, new FakeTranslationCredentialStore());
            try
            {
                AppThemeManager.ApplySettingsPalette(window.Resources, theme);
                window.ShowActivated = false;
                window.Show();
                typeof(MainWindow).GetMethod("ShowGeneralCategory", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(window, [1]);
                var page = Assert.IsType<ScrollViewer>(window.FindName("GeneralSettingsPanel"));
                foreach (var width in ResponsiveWidths)
                {
                    window.Width = width;
                    window.Height = 720;
                    window.UpdateLayout();
                    window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                    Assert.True(page.ScrollableWidth < 1);
                    var icons = Descendants(page).OfType<ToolbarFeatureIcon>().ToArray();
                    Assert.Equal(8, icons.Length);
                    var shortcutBoxes = Descendants(page).OfType<HotKeyCaptureBox>().ToArray();
                    Assert.Equal(8, shortcutBoxes.Length);
                    foreach (var box in shortcutBoxes)
                    {
                        box.Text = string.Empty;
                        box.GetBindingExpression(TextBox.TextProperty)!.UpdateSource();
                        Assert.True(box.Background.Opacity > 0);
                        Assert.True(box.BorderBrush.Opacity > 0);
                        if (box.BorderBrush is SolidColorBrush solid) Assert.True(solid.Color.A > 0);
                        Assert.True(box.BorderThickness.Left >= 1);
                    }
                    foreach (var icon in icons)
                    {
                        var viewbox = Assert.IsType<Viewbox>(VisualTreeHelper.GetParent(
                            VisualTreeHelper.GetParent(icon)));
                        var border = Assert.IsType<Border>(viewbox.Parent);
                        var origin = viewbox.TranslatePoint(new Point(), border);
                        Assert.Equal(18, viewbox.ActualWidth);
                        Assert.Equal(18, viewbox.ActualHeight);
                        Assert.InRange(Math.Abs(origin.X + 9 - border.ActualWidth / 2), 0, .5);
                        Assert.InRange(Math.Abs(origin.Y + 9 - border.ActualHeight / 2), 0, .5);
                        var location = border.TranslatePoint(new Point(), page);
                        Assert.InRange(location.X, 0, page.ActualWidth - border.ActualWidth);
                    }
                    if (width <= 600)
                        Assert.True(icons.Select(icon => Math.Round(icon.TranslatePoint(new Point(), page).Y)).Distinct().Count() > 1);
                }
            }
            finally { window.RequestExit(); }
        });
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
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
                        OutputFormat: VideoRecordingOutputFormat.Gif,
                        ShowCamera: true,
                        MicrophoneDeviceId: "microphone-2",
                        CameraDeviceId: "camera-2"));
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
        Assert.True(loaded.Settings.ShowCameraInRecording);
        Assert.Equal("microphone-2", loaded.Settings.MicrophoneDeviceId);
        Assert.Equal("camera-2", loaded.Settings.CameraDeviceId);
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

    [Theory]
    [InlineData(RecognitionResultPresentationMode.Popup)]
    [InlineData(RecognitionResultPresentationMode.Overlay)]
    public void RecognitionPresentationHasAnInitialSelectionAndChangesWithOneClick(
        RecognitionResultPresentationMode initial)
    {
        Directory.CreateDirectory(_testDirectory);
        var store = new SettingsStore(Path.Combine(_testDirectory, "presentation.json"));
        var settings = CreateSettings() with { RecognitionResultPresentation = initial };
        WpfTestHost.Invoke(() =>
        {
            using var hotKeys = new GlobalHotKeyManager();
            MainWindow Create(AppSettings saved) => new(saved, store,
                new FakeStartupRegistrationService(), hotKeys, new FakeTranslationCredentialStore());
            var window = Create(settings);
            var expected = initial == RecognitionResultPresentationMode.Popup
                ? RecognitionResultPresentationMode.Overlay : RecognitionResultPresentationMode.Popup;
            try
            {
                var popup = Assert.IsType<RadioButton>(window.FindName("RecognitionPopupChoice"));
                var overlay = Assert.IsType<RadioButton>(window.FindName("RecognitionOverlayChoice"));
                Assert.Equal(initial == RecognitionResultPresentationMode.Popup, popup.IsChecked);
                Assert.Equal(initial == RecognitionResultPresentationMode.Overlay, overlay.IsChecked);
                var target = expected == RecognitionResultPresentationMode.Popup ? popup : overlay;
                // Invoke the real ToggleButton click pipeline once (no desktop input).
                typeof(System.Windows.Controls.Primitives.ToggleButton).GetMethod("OnClick",
                    BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, null);
                Assert.Equal(expected, Assert.IsType<SettingsViewModel>(window.DataContext).RecognitionResultPresentation);
                Assert.Equal(expected == RecognitionResultPresentationMode.Popup, popup.IsChecked);
                Assert.Equal(expected == RecognitionResultPresentationMode.Overlay, overlay.IsChecked);
                typeof(MainWindow).GetMethod("ApplySettingsImmediately", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(window, [null]);
            }
            finally { window.RequestExit(); }
            var restored = Create(store.Load().Settings);
            try
            {
                Assert.True(Assert.IsType<RadioButton>(restored.FindName(expected == RecognitionResultPresentationMode.Popup
                    ? "RecognitionPopupChoice" : "RecognitionOverlayChoice")).IsChecked);
            }
            finally { restored.RequestExit(); }
        });
    }

    [Fact]
    public void TranslationProfileAvailabilitySurvivesProfileReorderingAndSave()
    {
        var settings = CreateSettings() with
        {
            TranslationProfiles =
            [
                new AiTranslationProfile
                {
                    Id = "primary",
                    Name = "主模型",
                    IsEnabled = true,
                    Provider = TranslationProviderFactory.OpenAiCompatibleProviderId,
                    Endpoint = "https://api.example.com/v1",
                    Model = "model-primary",
                },
                new AiTranslationProfile
                {
                    Id = "backup",
                    Name = "备用模型",
                    IsEnabled = true,
                    Provider = TranslationProviderFactory.OpenAiCompatibleProviderId,
                    Endpoint = "https://backup.example.com/v1",
                    Model = "model-backup",
                },
            ],
        };
        var viewModel = new SettingsViewModel(settings);
        var primary = Assert.Single(
            viewModel.TranslationProfiles,
            profile => profile.Id == "primary");
        primary.SetAvailability(true, "模型已验证");

        Assert.True(viewModel.MoveTranslationProfileToInsertionIndex(
            primary,
            insertionIndex: 2));

        viewModel.Apply(viewModel.CreateSettings());

        var savedPrimary = Assert.Single(
            viewModel.TranslationProfiles,
            profile => profile.Id == "primary");
        Assert.Same(primary, savedPrimary);
        Assert.True(savedPrimary.IsAvailable);
        Assert.False(savedPrimary.IsAvailabilityChecking);
        Assert.Equal("模型已验证", savedPrimary.AvailabilityReason);

        var reloaded = new SettingsViewModel(viewModel.CreateSettings());
        var reloadedPrimary = Assert.Single(
            reloaded.TranslationProfiles,
            profile => profile.Id == "primary");
        Assert.True(reloadedPrimary.IsAvailable);
        Assert.Equal("可用", reloadedPrimary.AvailabilityLabel);
        Assert.Equal("模型已验证", reloadedPrimary.AvailabilityReason);
    }

    [Fact]
    public void TranslationProfileAvailabilityKeepsLastResultWhileChecking()
    {
        var profile = new AiTranslationProfileItem(new AiTranslationProfile
        {
            Name = "在线模型",
            IsEnabled = true,
        });
        profile.SetAvailability(true, "模型已验证");
        var availabilityLabelNotifications = 0;
        profile.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(AiTranslationProfileItem.AvailabilityLabel))
            {
                availabilityLabelNotifications++;
            }
        };

        profile.SetAvailabilityChecking();

        Assert.True(profile.IsAvailabilityChecking);
        Assert.True(profile.IsAvailable);
        Assert.Equal("可用", profile.AvailabilityLabel);
        Assert.Equal("模型已验证", profile.AvailabilityReason);

        profile.SetAvailability(true, "模型已验证");
        Assert.False(profile.IsAvailabilityChecking);
        Assert.Equal(0, availabilityLabelNotifications);
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
            ToolbarScalePercent = 125,
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
        Assert.Equal(125, viewModel.ToolbarScalePercent);

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
        Assert.Equal(125, viewModel.CreateSettings().ToolbarScalePercent);
    }

    [Fact]
    public void CaptureToolbarSettingsPreviewsScaleImmediately()
    {
        WpfTestHost.Invoke(() =>
        {
            var viewModel = new SettingsViewModel(CreateSettings() with
            {
                ToolbarScalePercent = 100,
            });
            var editor = new CaptureToolbarLayoutEditor
            {
                DataContext = viewModel,
            };
            var layout = Assert.IsType<System.Windows.Controls.StackPanel>(
                editor.FindName("EditorRowsHost"));
            var preview = Assert.IsType<System.Windows.Controls.StackPanel>(
                editor.FindName("PreviewRowsHost"));

            viewModel.ToolbarScalePercent = 65;

            var layoutScale = Assert.IsType<System.Windows.Media.ScaleTransform>(
                layout.LayoutTransform);
            var previewScale = Assert.IsType<System.Windows.Media.ScaleTransform>(
                preview.LayoutTransform);
            Assert.Equal(0.65, layoutScale.ScaleX);
            Assert.Equal(0.65, layoutScale.ScaleY);
            Assert.Equal(0.65, previewScale.ScaleX);
            Assert.Equal(0.65, previewScale.ScaleY);
        });
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
            var viewModel = Assert.IsType<SettingsViewModel>(window.DataContext);
            Assert.Null(window.FindName("TranslationModeComboBox"));
            Assert.Equal(Visibility.Collapsed, onlinePanel.Visibility);
            Assert.Equal(Visibility.Visible, offlinePanel.Visibility);
            Assert.Equal(Visibility.Visible, priorityPanel.Visibility);
            Assert.NotEmpty(viewModel.TranslationProfiles);
            Assert.Equal(
                TranslationLanguageCatalog.Languages.Count,
                targetLanguage.Items.Count);
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

            Assert.Equal(Visibility.Collapsed, onlinePanel.Visibility);
            Assert.Equal(Visibility.Visible, offlinePanel.Visibility);
            Assert.Equal(Visibility.Visible, priorityPanel.Visibility);
            Assert.NotEmpty(window.DataContext is SettingsViewModel model
                ? model.TranslationProfiles
                : []);
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
            [TranslationProviderKind.Offline, TranslationProviderKind.Online, TranslationProviderKind.Public],
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
            var background = Assert.IsType<DrawingBrush>(window.Resources["AppWindowBackgroundBrush"]);
            Assert.True(background.IsFrozen);
            Assert.Equal((Color)ColorConverter.ConvertFromString(AppThemeManager.GetChromeColors(AppTheme.AuroraMist).Foreground),
                ((SolidColorBrush)window.Resources["AppControlForegroundBrush"]).Color);
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
