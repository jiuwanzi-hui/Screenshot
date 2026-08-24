using System.IO;
using System.Net.Http;
using System.Text;
using System.Windows;
using Screenshot.App.Capture;
using Screenshot.App.Core;
using Screenshot.App.Infrastructure;
using Screenshot.App.Pin;
using Screenshot.App.Presentation;
using Screenshot.App.Text;
using Screenshot.App.Update;

namespace Screenshot.App;

public partial class App : System.Windows.Application, IDisposable
{
    internal const long MaximumCrashLogSizeBytes = 10 * 1024 * 1024;
    private static readonly Encoding CrashLogEncoding = new UTF8Encoding(
        encoderShouldEmitUTF8Identifier: false);
    private MainWindow? _mainWindow;
    private TrayIconService? _trayIconService;
    private GlobalHotKeyManager? _hotKeyManager;
    private RegionCaptureCoordinator? _regionCaptureCoordinator;
    private CaptureHistoryService? _captureHistoryService;
    private CaptureHistoryWindow? _captureHistoryWindow;
    private bool _isCaptureHistoryRestoreStarted;
    private bool _isCaptureHistoryRestored;
    private FloatingCaptureWindow? _floatingCaptureWindow;
    private TextTranslationWindow? _textTranslationWindow;
    private PinnedImageManager? _pinnedImageManager;
    private HttpClient? _translationHttpClient;
    private AppThemeManager? _themeManager;
    private SingleInstanceCoordinator? _singleInstanceCoordinator;
    private AppSettings _currentSettings = AppSettings.CreateDefault();
    private bool _isShuttingDown;
    private bool _isCaptureInProgress;

    protected override void OnStartup(StartupEventArgs e)
    {
        WpfRenderingCompatibility.ConfigureForCurrentSession();
        base.OnStartup(e);
        TrimCrashLogIfOversized();
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        if (PortableUpdateRunner.IsUpdateRequest(e.Args))
        {
            Shutdown(PortableUpdateRunner.Run(e.Args));
            return;
        }

        PortableUpdateRunner.ScheduleCleanup(e.Args);

        var startInBackground = e.Args.Any(argument =>
            string.Equals(argument, "--background", StringComparison.OrdinalIgnoreCase));
        _singleInstanceCoordinator = SingleInstanceCoordinator.TryAcquire(
            "Screenshot.App",
            () => _ = Dispatcher.BeginInvoke(ShowMainWindow),
            signalExistingInstance: !startInBackground);
        if (_singleInstanceCoordinator is null)
        {
            Shutdown();
            return;
        }

        var settingsStore = new SettingsStore();
        var elevationLaunchService = new ElevationLaunchService();
        var elevationSettings = settingsStore.Load().Settings;
        var elevationResult = new ElevationLaunchResult(
            RelaunchStarted: false,
            Warning: null);
        if (elevationLaunchService.ShouldRequestElevation(elevationSettings, e.Args))
        {
            // Release the per-session instance event before the elevated child
            // starts, otherwise it would be treated as a second app instance.
            _singleInstanceCoordinator.Dispose();
            _singleInstanceCoordinator = null;
            elevationResult = elevationLaunchService.TryRelaunchElevated(
                elevationSettings,
                e.Args);
            if (elevationResult.RelaunchStarted)
            {
                Shutdown();
                return;
            }

            _singleInstanceCoordinator = SingleInstanceCoordinator.TryAcquire(
                "Screenshot.App",
                () => _ = Dispatcher.BeginInvoke(ShowMainWindow),
                signalExistingInstance: !startInBackground);
            if (_singleInstanceCoordinator is null)
            {
                Shutdown();
                return;
            }
        }
        var elevationWarning = elevationResult.Warning;

        var dataMigrationResult = InstalledDataMigration.TryMigrateLegacyData();
        WindowPlacementService.Initialize(AppMetadata.WindowPlacementsPath);
        var isFirstRun = !File.Exists(settingsStore.SettingsPath);
        var loadResult = settingsStore.Load();
        var credentialStore = new DpapiTranslationCredentialStore();
        var loadedSettings = MigrateLegacySaveDirectory(
            loadResult.Settings,
            dataMigrationResult.Migrated);
        _currentSettings = MigrateLegacyTranslationSettings(
            loadedSettings,
            settingsStore,
            credentialStore);
        _ = Task.Run(() =>
        {
            CaptureHistoryService.PruneCacheDirectory(
                _currentSettings.HistoryLimit);
            CaptureHistoryService.PruneCacheDirectoryByAge(
                _currentSettings.ScreenshotHistoryRetentionDays);
            VideoHistoryService.ApplyRetentionPolicy(
                _currentSettings.VideoSaveDirectory,
                _currentSettings.VideoHistoryRetentionDays,
                _currentSettings.VideoHistoryLimit);
        });
        if (dataMigrationResult.Migrated)
        {
            settingsStore.Save(_currentSettings);
        }
        _themeManager = new AppThemeManager();
        _themeManager.ThemeChanged += OnThemeChanged;
        _themeManager.Apply(_currentSettings.Theme);
        var startupRegistrationService = new StartupRegistrationService();
        var startupWarning = elevationWarning ?? (loadResult.Warning is null
            ? SynchronizeStartupRegistration(
                startupRegistrationService,
                _currentSettings.LaunchAtStartup)
            : null);
        _hotKeyManager = new GlobalHotKeyManager();
        _hotKeyManager.HotKeyPressed += OnHotKeyPressed;
        var hotKeyWarning = TryApplyInitialHotKeys(
            _hotKeyManager,
            _currentSettings);
        _translationHttpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30),
        };

        _mainWindow = new MainWindow(
            _currentSettings,
            settingsStore,
            startupRegistrationService,
            _hotKeyManager,
            credentialStore,
            _translationHttpClient);
        _mainWindow.ApplySettingsPalette(_themeManager.ResolvedTheme);
        MainWindow = _mainWindow;
        _mainWindow.SettingsSaved += OnSettingsSaved;
        _mainWindow.ExitRequested += OnExitRequested;
        _mainWindow.UpdateInstallationStarted += OnUpdateInstallationStarted;
        _mainWindow.TextTranslationRequested += OnTextTranslationRequested;
        _mainWindow.ConfigureTaskbarVisibility(_currentSettings.ShowTaskbarIcon);
        _captureHistoryService = new CaptureHistoryService();
        _captureHistoryService.ConfigureRetentionPolicy(
            _currentSettings.ScreenshotHistoryRetentionDays,
            _currentSettings.HistoryLimit);
        _pinnedImageManager = new PinnedImageManager(
            RecognizePinnedImageAsync,
            TranslatePinnedImageAsync,
            () => _ = Dispatcher.BeginInvoke(ShowMainWindow),
            () => _currentSettings,
            colorText => _mainWindow?.SaveCustomStrokeColor(colorText),
            colors => _mainWindow?.SaveCustomColorPalette(colors),
            arrowStyle => _mainWindow?.SaveArrowStyle(arrowStyle),
            arrowToolMode => _mainWindow?.SaveArrowToolMode(arrowToolMode),
            shapeToolMode => _mainWindow?.SaveShapeToolMode(shapeToolMode),
            tool => _mainWindow?.SaveLastAnnotationTool(tool));
        _pinnedImageManager.DisplayStateChanged +=
            OnPinnedImageDisplayStateChanged;
        _pinnedImageManager.RestorePersisted();
        _regionCaptureCoordinator = new RegionCaptureCoordinator(
            () => _currentSettings,
            _captureHistoryService,
            _pinnedImageManager,
            credentialStore,
            _translationHttpClient,
            message => _mainWindow?.ShowStatus(message),
            suspended => _hotKeyManager.SetMouseShortcutsSuspended(suspended),
            preferences => _mainWindow?.SaveVideoRecordingPreferences(preferences),
            preferences => _mainWindow?.SaveVideoRecordingAnnotationPreferences(
                preferences),
            arrowStyle => _mainWindow?.SaveArrowStyle(arrowStyle),
            arrowToolMode => _mainWindow?.SaveArrowToolMode(arrowToolMode),
            shapeToolMode => _mainWindow?.SaveShapeToolMode(shapeToolMode),
            tool => _mainWindow?.SaveLastAnnotationTool(tool),
            colorText => _mainWindow?.SaveCustomStrokeColor(colorText),
            colors => _mainWindow?.SaveCustomColorPalette(colors),
            (x, y) => _mainWindow?.SaveCaptureToolbarPosition(x, y),
            () => _ = Dispatcher.BeginInvoke(
                () => ShowCaptureHistory(showVideo: true)),
            () => _floatingCaptureWindow?.ShowRecordingAlreadyActiveFeedback());
        _regionCaptureCoordinator.CaptureStateChanged += OnCaptureStateChanged;

        if (dataMigrationResult.Warning is not null)
        {
            _mainWindow.ShowStatus(dataMigrationResult.Warning);
        }
        else if (loadResult.Warning is not null)
        {
            _mainWindow.ShowStatus(loadResult.Warning);
        }
        else if (startupWarning is not null)
        {
            _mainWindow.ShowStatus(startupWarning);
        }
        else if (hotKeyWarning is not null)
        {
            _mainWindow.ShowStatus(hotKeyWarning);
        }
        else if (TryGetArgumentValue(e.Args, "--updated") is { } updatedVersion)
        {
            _mainWindow.ShowStatus(
                AppMetadata.FormatUpdatedVersionStatus(updatedVersion));
        }

        _trayIconService = new TrayIconService(_themeManager.ResolvedTheme);
        _trayIconService.OpenSettingsRequested += OnOpenSettingsRequested;
        _trayIconService.RegionCaptureRequested += OnRegionCaptureRequested;
        _trayIconService.ScrollCaptureRequested += OnScrollCaptureRequested;
        _trayIconService.VideoRecordingRequested += OnVideoRecordingRequested;
        _trayIconService.HistoryRequested += OnHistoryRequested;
        _trayIconService.HidePinnedImagesRequested += OnHidePinnedImagesRequested;
        _trayIconService.ExitRequested += OnExitRequested;
        UpdatePinnedImageTrayCommands();
        _trayIconService.SetVisible(_currentSettings.ShowNotificationIcon);
        UpdateFloatingCaptureWindow();

        if (!startInBackground ||
            isFirstRun ||
            hotKeyWarning is not null ||
            elevationWarning is not null)
        {
            ShowMainWindow();
        }

        _ = Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.ApplicationIdle,
            Core.MemoryFootprint.TrimAfterHeavyOperation);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Dispose();
        base.OnExit(e);
    }

    /// <summary>
    /// A background-resident tool must survive a failure in one interaction:
    /// losing an overlay is recoverable, losing the tray process is not. The
    /// exception is preserved for diagnosis instead of tearing the app down.
    /// </summary>
    private void OnDispatcherUnhandledException(
        object sender,
        System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(Core.AppMetadata.DiagnosticsDirectoryPath);
            AppendCrashLog(
                Path.Combine(
                    Core.AppMetadata.DiagnosticsDirectoryPath,
                    "crash.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {e.Exception}" +
                    Environment.NewLine + Environment.NewLine);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        _mainWindow?.ShowStatus(
            "操作出现异常，已记录到 ScreenshotData\\Diagnostics\\crash.log。");
        e.Handled = true;
    }

    internal static void AppendCrashLog(string path, string entry)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(entry);

        var entrySize = CrashLogEncoding.GetByteCount(entry);
        var existingSize = File.Exists(path) ? new FileInfo(path).Length : 0;
        if (existingSize + entrySize > MaximumCrashLogSizeBytes)
        {
            File.WriteAllText(path, entry, CrashLogEncoding);
            return;
        }

        File.AppendAllText(path, entry, CrashLogEncoding);
    }

    internal static void TrimCrashLogIfOversized()
    {
        TrimCrashLogIfOversized(Path.Combine(
            Core.AppMetadata.DiagnosticsDirectoryPath,
            "crash.log"));
    }

    internal static void TrimCrashLogIfOversized(string path)
    {
        try
        {
            if (File.Exists(path) &&
                new FileInfo(path).Length > MaximumCrashLogSizeBytes)
            {
                File.WriteAllText(path, string.Empty, CrashLogEncoding);
            }
        }
        catch (IOException)
        {
            // Crash-log maintenance must never prevent the app from starting.
        }
        catch (UnauthorizedAccessException)
        {
            // Crash-log maintenance must never prevent the app from starting.
        }
    }

    public void Dispose()
    {
        DisposeFloatingCaptureWindow();
        if (_regionCaptureCoordinator is not null)
        {
            _regionCaptureCoordinator.CaptureStateChanged -= OnCaptureStateChanged;
            _regionCaptureCoordinator = null;
        }
        DisposeTrayIconService();
        DisposeHotKeyManager();
        DisposePinnedImageManager();
        if (_themeManager is not null)
        {
            _themeManager.ThemeChanged -= OnThemeChanged;
            _themeManager.Dispose();
        }
        _themeManager = null;
        _translationHttpClient?.Dispose();
        _translationHttpClient = null;

        if (_mainWindow is not null)
        {
            _mainWindow.SettingsSaved -= OnSettingsSaved;
            _mainWindow.ExitRequested -= OnExitRequested;
            _mainWindow.UpdateInstallationStarted -= OnUpdateInstallationStarted;
            _mainWindow.TextTranslationRequested -= OnTextTranslationRequested;
        }

        _singleInstanceCoordinator?.Dispose();
        _singleInstanceCoordinator = null;

        GC.SuppressFinalize(this);
    }

    private void OnOpenSettingsRequested(object? sender, EventArgs e)
    {
        _ = Dispatcher.BeginInvoke(ShowMainWindow);
    }

    private void OnExitRequested(object? sender, EventArgs e)
    {
        _ = Dispatcher.BeginInvoke(ExitApplication);
    }

    private void OnHidePinnedImagesRequested(object? sender, EventArgs e)
    {
        _ = Dispatcher.BeginInvoke(() => _pinnedImageManager?.HideAll());
    }

    private void OnPinnedImageDisplayStateChanged(object? sender, EventArgs e)
    {
        UpdatePinnedImageTrayCommands();
    }

    private void UpdatePinnedImageTrayCommands()
    {
        _trayIconService?.UpdatePinnedImageCommands(
            _pinnedImageManager is { Count: > 0 },
            _pinnedImageManager?.HasHiddenWindows == true);
    }

    private void OnUpdateInstallationStarted(object? sender, EventArgs e)
    {
        _ = Dispatcher.BeginInvoke(ExitApplication);
    }

    private void OnRegionCaptureRequested(object? sender, EventArgs e)
    {
        _ = Dispatcher.BeginInvoke((Action)RequestRegionCapture);
    }

    private void OnScrollCaptureRequested(object? sender, EventArgs e)
    {
        _ = Dispatcher.BeginInvoke(RequestScrollCapture);
    }

    private void OnVideoRecordingRequested(object? sender, EventArgs e)
    {
        _ = Dispatcher.BeginInvoke(() => _ = RequestVideoRecordingAsync());
    }

    private void OnHistoryRequested(object? sender, EventArgs e)
    {
        _ = Dispatcher.BeginInvoke(() => ShowCaptureHistory());
    }

    private void OnTextTranslationRequested(object? sender, EventArgs e)
    {
        _ = Dispatcher.BeginInvoke(() => ShowTextTranslationWindow());
    }

    private void OnSettingsSaved(object? sender, SettingsSavedEventArgs e)
    {
        _currentSettings = e.Settings;
        _captureHistoryService?.ConfigureRetentionPolicy(
            e.Settings.ScreenshotHistoryRetentionDays,
            e.Settings.HistoryLimit);
        _captureHistoryWindow?.UpdateRetentionPolicy(
            e.Settings.ScreenshotHistoryRetentionDays,
            e.Settings.HistoryLimit,
            e.Settings.VideoHistoryRetentionDays,
            e.Settings.VideoHistoryLimit);
        if (_captureHistoryWindow is not null)
        {
            BeginCaptureHistoryRestore();
        }
        _ = Task.Run(() =>
        {
            CaptureHistoryService.PruneCacheDirectory(e.Settings.HistoryLimit);
            CaptureHistoryService.PruneCacheDirectoryByAge(
                e.Settings.ScreenshotHistoryRetentionDays);
            VideoHistoryService.ApplyRetentionPolicy(
                e.Settings.VideoSaveDirectory,
                e.Settings.VideoHistoryRetentionDays,
                e.Settings.VideoHistoryLimit);
        });
        _themeManager?.Apply(e.Settings.Theme);
        _trayIconService?.SetVisible(e.Settings.ShowNotificationIcon);
        _captureHistoryWindow?.UpdateDirectories(
            e.Settings.SaveDirectory,
            e.Settings.VideoSaveDirectory);
        UpdateFloatingCaptureWindow();
    }

    private async Task RestoreCaptureHistoryAsync(
        CaptureHistoryService historyService)
    {
        try
        {
            var items = await Task.Run(() => historyService.LoadPersistedItems(
                _currentSettings.HistoryLimit));
            await Dispatcher.InvokeAsync(() =>
            {
                historyService.MergePersistedItems(
                    items,
                    _currentSettings.HistoryLimit);
                _isCaptureHistoryRestored = true;
            });
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or ArgumentException
                or NotSupportedException)
        {
            _mainWindow?.ShowStatus($"无法恢复截图历史：{exception.Message}");
        }
        finally
        {
            _isCaptureHistoryRestoreStarted = false;
        }
    }

    private void BeginCaptureHistoryRestore()
    {
        if (_captureHistoryService is not { } history ||
            _isCaptureHistoryRestoreStarted ||
            _isCaptureHistoryRestored)
        {
            return;
        }

        _isCaptureHistoryRestoreStarted = true;
        _ = RestoreCaptureHistoryAsync(history);
    }

    private void OnThemeChanged(object? sender, AppTheme theme)
    {
        _mainWindow?.ApplySettingsPalette(theme);
        _trayIconService?.ApplyTheme(theme);
    }

    private void UpdateFloatingCaptureWindow()
    {
        if (!_currentSettings.ShowFloatingCaptureButton)
        {
            DisposeFloatingCaptureWindow();
            return;
        }

        if (_floatingCaptureWindow is not null)
        {
            if (!_isCaptureInProgress && !_floatingCaptureWindow.IsVisible)
            {
                _floatingCaptureWindow.Show();
            }

            return;
        }

        var window = new FloatingCaptureWindow();
        window.RepeatCaptureRequested += OnFloatingRepeatCaptureRequested;
        window.RegionCaptureRequested += OnFloatingRegionCaptureRequested;
        window.ScrollCaptureRequested += OnFloatingScrollCaptureRequested;
        window.VideoRecordingRequested += OnFloatingVideoRecordingRequested;
        window.PinCaptureRequested += OnFloatingPinCaptureRequested;
        window.AllScreensCaptureRequested += OnFloatingAllScreensCaptureRequested;
        window.HistoryRequested += OnFloatingHistoryRequested;
        window.SettingsRequested += OnFloatingSettingsRequested;
        window.TextTranslationRequested += OnFloatingTextTranslationRequested;
        window.CloseRequested += OnFloatingCloseRequested;
        window.Closed += OnFloatingCaptureWindowClosed;
        _floatingCaptureWindow = window;
        window.Show();
        if (_isCaptureInProgress)
        {
            window.SetCaptureInProgress(true);
        }
    }

    private void DisposeFloatingCaptureWindow()
    {
        var window = _floatingCaptureWindow;
        if (window is null)
        {
            return;
        }

        _floatingCaptureWindow = null;
        window.RepeatCaptureRequested -= OnFloatingRepeatCaptureRequested;
        window.RegionCaptureRequested -= OnFloatingRegionCaptureRequested;
        window.ScrollCaptureRequested -= OnFloatingScrollCaptureRequested;
        window.VideoRecordingRequested -= OnFloatingVideoRecordingRequested;
        window.PinCaptureRequested -= OnFloatingPinCaptureRequested;
        window.AllScreensCaptureRequested -= OnFloatingAllScreensCaptureRequested;
        window.HistoryRequested -= OnFloatingHistoryRequested;
        window.SettingsRequested -= OnFloatingSettingsRequested;
        window.TextTranslationRequested -= OnFloatingTextTranslationRequested;
        window.CloseRequested -= OnFloatingCloseRequested;
        window.Closed -= OnFloatingCaptureWindowClosed;
        window.Close();
    }

    private void OnFloatingCaptureWindowClosed(object? sender, EventArgs e)
    {
        if (ReferenceEquals(sender, _floatingCaptureWindow))
        {
            _floatingCaptureWindow = null;
        }
    }

    private void OnFloatingSettingsRequested(object? sender, EventArgs e)
    {
        _ = Dispatcher.BeginInvoke(ShowMainWindow);
    }

    private void OnFloatingTextTranslationRequested(object? sender, EventArgs e)
    {
        _ = Dispatcher.BeginInvoke(() => ShowTextTranslationWindow());
    }

    private void OnCaptureStateChanged(bool isInProgress)
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(
                () => OnCaptureStateChanged(isInProgress));
            return;
        }

        _isCaptureInProgress = isInProgress;
        VideoRecordingControlWindow.SetCaptureInteractionActive(isInProgress);
        _floatingCaptureWindow?.SetCaptureInProgress(isInProgress);
    }

    private void OnFloatingRepeatCaptureRequested(object? sender, EventArgs e)
    {
        _ = RequestFloatingCaptureAsync();
    }

    private void OnFloatingRegionCaptureRequested(object? sender, EventArgs e)
    {
        RequestRegionCapture();
    }

    private void OnFloatingScrollCaptureRequested(object? sender, EventArgs e)
    {
        RequestScrollCapture(pointerContinuation: null);
    }

    private void OnFloatingVideoRecordingRequested(object? sender, EventArgs e)
    {
        _ = RequestVideoRecordingAsync();
    }

    private void OnFloatingPinCaptureRequested(object? sender, EventArgs e)
    {
        RequestPinCapture(pointerContinuation: null);
    }

    private void OnFloatingAllScreensCaptureRequested(object? sender, EventArgs e)
    {
        _ = RequestAllScreensCaptureAsync();
    }

    private void OnFloatingHistoryRequested(object? sender, EventArgs e)
    {
        ShowCaptureHistory();
    }

    private void OnFloatingCloseRequested(object? sender, EventArgs e)
    {
        _mainWindow?.SetFloatingCaptureButtonEnabled(false);
    }

    private async Task RequestFloatingCaptureAsync()
    {
        try
        {
            if (_regionCaptureCoordinator is not null)
            {
                await _regionCaptureCoordinator.RequestFloatingCaptureAsync(
                    _currentSettings.FloatingCaptureClickBehavior);
            }
        }
        catch (Exception)
        {
            _mainWindow?.ShowStatus("悬浮按钮功能执行失败，请重试。");
        }
    }

    private async Task RequestAllScreensCaptureAsync()
    {
        try
        {
            if (_regionCaptureCoordinator is not null)
            {
                await _regionCaptureCoordinator.RequestAllScreensCaptureAsync();
            }
        }
        catch (Exception)
        {
            _mainWindow?.ShowStatus("全部屏幕截图失败，请重试。");
        }
    }

    private async Task RequestVideoRecordingAsync(
        CapturePointerContinuation? pointerContinuation = null)
    {
        try
        {
            if (_regionCaptureCoordinator is not null)
            {
                await _regionCaptureCoordinator.RequestVideoRecordingAsync(
                    pointerContinuation);
            }
        }
        catch (Exception)
        {
            _mainWindow?.ShowStatus("无法开始区域录制，请重试。");
        }
    }

    private void OnHotKeyPressed(object? sender, HotKeyPressedEventArgs e)
    {
        if (_mainWindow?.IsCapturingHotKey == true)
        {
            return;
        }

        if (e.Action == HotKeyAction.RegionCapture)
        {
            RequestRegionCapture(
                DetachUsablePreCapturedScreen(e),
                e.CapturePointerContinuation);
        }
        else if (e.Action == HotKeyAction.CompleteCapture)
        {
            CaptureOverlayWindow.TryCompleteActiveInteractiveSelection();
        }
        else if (e.Action == HotKeyAction.RecognizeText)
        {
            RequestTranslationCapture(
                DetachUsablePreCapturedScreen(e),
                e.CapturePointerContinuation);
        }
        else if (e.Action == HotKeyAction.PinImage)
        {
            RequestPinCapture(
                DetachUsablePreCapturedScreen(e),
                e.CapturePointerContinuation);
        }
        else if (e.Action == HotKeyAction.ScrollCapture)
        {
            RequestScrollCapture(e.CapturePointerContinuation);
        }
        else if (e.Action == HotKeyAction.VideoRecording)
        {
            _ = RequestVideoRecordingAsync(e.CapturePointerContinuation);
        }
        else if (e.Action == HotKeyAction.OpenSettings)
        {
            _ = Dispatcher.BeginInvoke(ShowMainWindow);
        }
        else if (e.Action == HotKeyAction.TranslateSelectedText)
        {
            _ = TranslateSelectedTextAsync();
        }
    }

    private CapturedImage? DetachUsablePreCapturedScreen(
        HotKeyPressedEventArgs eventArgs)
    {
        var snapshot = eventArgs.DetachPreCapturedScreen();
        if (_regionCaptureCoordinator?.IsRecordingInProgress != true)
        {
            return snapshot;
        }

        snapshot?.Dispose();
        return null;
    }

    private void ShowMainWindow()
    {
        _mainWindow?.ShowFromTray();
    }

    private async Task TranslateSelectedTextAsync()
    {
        try
        {
            var selectedText = await SelectedTextCaptureService
                .TryCopySelectedTextAsync();
            if (string.IsNullOrWhiteSpace(selectedText))
            {
                ShowTextTranslationWindow();
                return;
            }

            ShowTextTranslationWindow(selectedText, translateImmediately: true);
        }
        catch
        {
            ShowTextTranslationWindow();
        }
    }

    private void ShowTextTranslationWindow(
        string? sourceText = null,
        bool translateImmediately = false)
    {
        if (_textTranslationWindow is null)
        {
            _textTranslationWindow = new TextTranslationWindow(
                () => _currentSettings,
                languageTag =>
                    _mainWindow?.SaveTranslationTargetLanguage(languageTag),
                languageTag =>
                    _mainWindow?.OpenTranslationModelSettings(languageTag));
            _textTranslationWindow.Closed += OnTextTranslationWindowClosed;
            _textTranslationWindow.Show();
        }
        else
        {
            _textTranslationWindow.Show();
            _textTranslationWindow.Activate();
        }

        if (!string.IsNullOrWhiteSpace(sourceText))
        {
            _textTranslationWindow.SetSourceText(
                sourceText,
                translateImmediately);
        }
    }

    private void OnTextTranslationWindowClosed(object? sender, EventArgs e)
    {
        if (_textTranslationWindow is not { } window ||
            !ReferenceEquals(sender, window))
        {
            return;
        }

        window.Closed -= OnTextTranslationWindowClosed;
        _textTranslationWindow = null;
    }

    private void ShowCaptureHistory() => ShowCaptureHistory(showVideo: false);

    private void ShowCaptureHistory(bool showVideo)
    {
        if (_captureHistoryService is null)
        {
            return;
        }

        try
        {
            BeginCaptureHistoryRestore();
            if (_captureHistoryWindow is null)
            {
                _captureHistoryWindow = new CaptureHistoryWindow(
                    _captureHistoryService,
                    _currentSettings.SaveDirectory,
                    _currentSettings.VideoSaveDirectory,
                    _currentSettings.ScreenshotHistoryRetentionDays,
                    _currentSettings.HistoryLimit,
                    _currentSettings.VideoHistoryRetentionDays,
                    _currentSettings.VideoHistoryLimit);
                _captureHistoryWindow.Closed += OnCaptureHistoryWindowClosed;
                if (showVideo)
                {
                    _captureHistoryWindow.ShowVideoHistory();
                }
                _captureHistoryWindow.Show();
                return;
            }

            _captureHistoryWindow.UpdateDirectories(
                _currentSettings.SaveDirectory,
                _currentSettings.VideoSaveDirectory);
            if (showVideo)
            {
                _captureHistoryWindow.ShowVideoHistory();
            }
            _captureHistoryWindow.Show();
            _captureHistoryWindow.Activate();
        }
        catch (Exception exception)
        {
            if (_captureHistoryWindow is not null)
            {
                _captureHistoryWindow.Closed -= OnCaptureHistoryWindowClosed;
                _captureHistoryWindow = null;
            }

            _mainWindow?.ShowStatus($"无法打开历史查看：{exception.Message}");
        }
    }

    private void OnCaptureHistoryWindowClosed(object? sender, EventArgs e)
    {
        if (_captureHistoryWindow is not null)
        {
            _captureHistoryWindow.Closed -= OnCaptureHistoryWindowClosed;
            _captureHistoryWindow = null;
        }
    }

    private void RequestRegionCapture()
    {
        RequestRegionCapture(
            initialScreenSnapshot: null,
            pointerContinuation: null);
    }

    private void RequestRegionCapture(
        CapturedImage? initialScreenSnapshot,
        CapturePointerContinuation? pointerContinuation)
    {
        _ = RequestRegionCaptureAsync(
            initialScreenSnapshot,
            pointerContinuation);
    }

    private void RequestTranslationCapture(
        CapturedImage? initialScreenSnapshot,
        CapturePointerContinuation? pointerContinuation)
    {
        _ = RequestTranslationCaptureAsync(
            initialScreenSnapshot,
            pointerContinuation);
    }

    private void RequestPinCapture(
        CapturedImage? initialScreenSnapshot = null,
        CapturePointerContinuation? pointerContinuation = null)
    {
        _ = RequestPinCaptureAsync(initialScreenSnapshot, pointerContinuation);
    }

    private void RequestScrollCapture(
        CapturePointerContinuation? pointerContinuation)
    {
        _ = RequestScrollCaptureAsync(pointerContinuation);
    }

    private async Task RequestRegionCaptureAsync(
        CapturedImage? initialScreenSnapshot,
        CapturePointerContinuation? pointerContinuation)
    {
        try
        {
            if (_regionCaptureCoordinator is not null)
            {
                await _regionCaptureCoordinator.RequestCaptureAsync(
                    initialScreenSnapshot,
                    pointerContinuation);
            }
            else
            {
                initialScreenSnapshot?.Dispose();
            }
        }
        catch (Exception)
        {
            _mainWindow?.ShowStatus("截图失败，请重试。");
        }
    }

    private async Task RequestTranslationCaptureAsync(
        CapturedImage? initialScreenSnapshot,
        CapturePointerContinuation? pointerContinuation)
    {
        try
        {
            if (_regionCaptureCoordinator is not null)
            {
                await _regionCaptureCoordinator.RequestTranslationCaptureAsync(
                    initialScreenSnapshot,
                    pointerContinuation);
            }
            else
            {
                initialScreenSnapshot?.Dispose();
            }
        }
        catch (Exception)
        {
            _mainWindow?.ShowStatus("翻译失败，请检查文字识别与翻译设置。");
        }
    }

    private async Task RequestPinCaptureAsync(
        CapturedImage? initialScreenSnapshot,
        CapturePointerContinuation? pointerContinuation)
    {
        try
        {
            if (_regionCaptureCoordinator is not null)
            {
                await _regionCaptureCoordinator.RequestPinCaptureAsync(
                    initialScreenSnapshot,
                    pointerContinuation);
                initialScreenSnapshot = null;
            }
            else
            {
                initialScreenSnapshot?.Dispose();
                initialScreenSnapshot = null;
            }
        }
        catch (Exception)
        {
            initialScreenSnapshot?.Dispose();
            _mainWindow?.ShowStatus("钉图失败，请重试。");
        }
    }

    private async Task<OcrRecognitionResult> RecognizePinnedImageAsync(
        CapturedImage image)
    {
        try
        {
            return await OcrProviderFactory.RecognizeAsync(
                image,
                _currentSettings);
        }
        catch (Exception)
        {
            _mainWindow?.ShowStatus("钉图文字识别失败，请检查语言设置。");
            return OcrRecognitionResult.Failure(
                "钉图文字识别失败，请检查语言设置。");
        }
    }

    private async Task<TranslationSegmentsResult> TranslatePinnedImageAsync(
        OcrRecognitionResult recognition)
    {
        if (_translationHttpClient is null)
        {
            return TranslationSegmentsResult.Failure("翻译服务尚未初始化。");
        }

        try
        {
            var provider = TranslationProviderFactory.Create(
                _currentSettings,
                new DpapiTranslationCredentialStore(),
                _translationHttpClient,
                preferFastOffline: true);
            return await provider.TranslateSegmentsAsync(
                recognition.Regions.Select(region => region.Text).ToArray(),
                "auto",
                _currentSettings.TranslationTargetLanguage);
        }
        catch (Exception)
        {
            _mainWindow?.ShowStatus("钉图翻译失败，请检查翻译设置。");
            return TranslationSegmentsResult.Failure(
                "钉图翻译失败，请检查翻译设置。");
        }
    }

    private async Task RequestScrollCaptureAsync(
        CapturePointerContinuation? pointerContinuation)
    {
        try
        {
            if (_regionCaptureCoordinator is not null)
            {
                await _regionCaptureCoordinator.RequestScrollCaptureAsync(
                    pointerContinuation);
            }
        }
        catch (Exception)
        {
            _mainWindow?.ShowStatus("长截图失败，请改用普通截图。");
        }
    }

    private void ExitApplication()
    {
        if (_isShuttingDown)
        {
            return;
        }

        _isShuttingDown = true;
        DisposeTrayIconService();
        _mainWindow?.RequestExit();
        Shutdown();
    }

    private void DisposeTrayIconService()
    {
        if (_trayIconService is null)
        {
            return;
        }

        _trayIconService.OpenSettingsRequested -= OnOpenSettingsRequested;
        _trayIconService.RegionCaptureRequested -= OnRegionCaptureRequested;
        _trayIconService.ScrollCaptureRequested -= OnScrollCaptureRequested;
        _trayIconService.VideoRecordingRequested -= OnVideoRecordingRequested;
        _trayIconService.HistoryRequested -= OnHistoryRequested;
        _trayIconService.HidePinnedImagesRequested -= OnHidePinnedImagesRequested;
        _trayIconService.ExitRequested -= OnExitRequested;
        _trayIconService.Dispose();
        _trayIconService = null;
    }

    private void DisposeHotKeyManager()
    {
        if (_hotKeyManager is null)
        {
            return;
        }

        _hotKeyManager.HotKeyPressed -= OnHotKeyPressed;
        _hotKeyManager.Dispose();
        _hotKeyManager = null;
    }

    private void DisposePinnedImageManager()
    {
        if (_pinnedImageManager is null)
        {
            return;
        }

        _pinnedImageManager.DisplayStateChanged -=
            OnPinnedImageDisplayStateChanged;
        _pinnedImageManager.Dispose();
        _pinnedImageManager = null;
    }

    private static AppSettings MigrateLegacyTranslationSettings(
        AppSettings settings,
        SettingsStore settingsStore,
        DpapiTranslationCredentialStore credentialStore)
    {
        var configuredProvider = settings.TranslationProvider?.Trim() ?? string.Empty;
        var migrated = settings with
        {
            TranslationProvider = TranslationProviderFactory.OpenAiCompatibleProviderId,
        };

        if (configuredProvider.StartsWith("sk-", StringComparison.OrdinalIgnoreCase) &&
            string.IsNullOrWhiteSpace(credentialStore.GetApiKey(
                TranslationProviderFactory.OpenAiCompatibleProviderId)))
        {
            credentialStore.SetApiKey(
                TranslationProviderFactory.OpenAiCompatibleProviderId,
                configuredProvider);
        }

        if (!string.Equals(
                configuredProvider,
                TranslationProviderFactory.OpenAiCompatibleProviderId,
                StringComparison.Ordinal))
        {
            settingsStore.Save(migrated);
        }

        return migrated;
    }

    private static AppSettings MigrateLegacySaveDirectory(
        AppSettings settings,
        bool dataWasMigrated)
    {
        if (!dataWasMigrated)
        {
            return settings;
        }

        var legacyCaptureDirectory = Path.Combine(
            AppMetadata.LegacyInstalledDataDirectoryPath,
            AppMetadata.CapturesDirectoryName);
        try
        {
            return string.Equals(
                Path.GetFullPath(settings.SaveDirectory),
                Path.GetFullPath(legacyCaptureDirectory),
                StringComparison.OrdinalIgnoreCase)
                ? settings with { SaveDirectory = AppMetadata.DefaultCaptureDirectory }
                : settings;
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or NotSupportedException
                or PathTooLongException)
        {
            return settings;
        }
    }

    private static string? SynchronizeStartupRegistration(
        StartupRegistrationService startupRegistrationService,
        bool shouldLaunchAtStartup)
    {
        try
        {
            if (startupRegistrationService.IsEnabled() != shouldLaunchAtStartup)
            {
                startupRegistrationService.SetEnabled(shouldLaunchAtStartup);
            }

            return null;
        }
        catch (Exception)
        {
            return "无法同步开机启动设置。";
        }
    }

    private static string? TryApplyInitialHotKeys(
        GlobalHotKeyManager hotKeyManager,
        AppSettings settings)
    {
        try
        {
            var bindings = HotKeyConfiguration.CreateBindings(settings);
            var result = hotKeyManager.ApplyAvailable(bindings);
            return result.IsSuccess ? null : result.ErrorMessage;
        }
        catch (ArgumentException exception)
        {
            return exception.Message;
        }
    }

    private static string? TryGetArgumentValue(
        IReadOnlyList<string> arguments,
        string name)
    {
        for (var index = 0; index + 1 < arguments.Count; index++)
        {
            if (string.Equals(arguments[index], name, StringComparison.OrdinalIgnoreCase))
            {
                return arguments[index + 1];
            }
        }

        return null;
    }
}
