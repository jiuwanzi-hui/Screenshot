using System.IO;
using System.Net.Http;
using System.Windows;
using Screenshot.App.Capture;
using Screenshot.App.Core;
using Screenshot.App.Infrastructure;
using Screenshot.App.Pin;
using Screenshot.App.Presentation;
using Screenshot.App.Text;

namespace Screenshot.App;

public partial class App : System.Windows.Application, IDisposable
{
    private MainWindow? _mainWindow;
    private TrayIconService? _trayIconService;
    private GlobalHotKeyManager? _hotKeyManager;
    private RegionCaptureCoordinator? _regionCaptureCoordinator;
    private CaptureHistoryService? _captureHistoryService;
    private CaptureHistoryWindow? _captureHistoryWindow;
    private PinnedImageManager? _pinnedImageManager;
    private HttpClient? _translationHttpClient;
    private AppThemeManager? _themeManager;
    private SingleInstanceCoordinator? _singleInstanceCoordinator;
    private AppSettings _currentSettings = AppSettings.CreateDefault();
    private bool _isShuttingDown;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

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

        var dataMigrationResult = InstalledDataMigration.TryMigrateLegacyData();
        var settingsStore = new SettingsStore();
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
        if (dataMigrationResult.Migrated)
        {
            settingsStore.Save(_currentSettings);
        }
        _themeManager = new AppThemeManager();
        _themeManager.Apply(_currentSettings.Theme);
        var startupRegistrationService = new StartupRegistrationService();
        var startupWarning = loadResult.Warning is null
            ? SynchronizeStartupRegistration(
                startupRegistrationService,
                _currentSettings.LaunchAtStartup)
            : null;
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
        MainWindow = _mainWindow;
        _mainWindow.SettingsSaved += OnSettingsSaved;
        _mainWindow.ExitRequested += OnExitRequested;
        _mainWindow.ConfigureTaskbarVisibility(_currentSettings.ShowTaskbarIcon);
        _captureHistoryService = new CaptureHistoryService();
        _pinnedImageManager = new PinnedImageManager(RecognizePinnedImageAsync);
        _regionCaptureCoordinator = new RegionCaptureCoordinator(
            () => _currentSettings,
            _captureHistoryService,
            _pinnedImageManager,
            credentialStore,
            _translationHttpClient,
            message => _mainWindow?.ShowStatus(message));

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

        _trayIconService = new TrayIconService(_themeManager.ResolvedTheme);
        _trayIconService.OpenSettingsRequested += OnOpenSettingsRequested;
        _trayIconService.RegionCaptureRequested += OnRegionCaptureRequested;
        _trayIconService.ScrollCaptureRequested += OnScrollCaptureRequested;
        _trayIconService.HistoryRequested += OnHistoryRequested;
        _trayIconService.ExitRequested += OnExitRequested;
        _trayIconService.SetVisible(_currentSettings.ShowNotificationIcon);

        if (!startInBackground || isFirstRun || hotKeyWarning is not null)
        {
            ShowMainWindow();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Dispose();
        base.OnExit(e);
    }

    public void Dispose()
    {
        DisposeTrayIconService();
        DisposeHotKeyManager();
        DisposePinnedImageManager();
        _themeManager?.Dispose();
        _themeManager = null;
        _translationHttpClient?.Dispose();
        _translationHttpClient = null;

        if (_mainWindow is not null)
        {
            _mainWindow.SettingsSaved -= OnSettingsSaved;
            _mainWindow.ExitRequested -= OnExitRequested;
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

    private void OnRegionCaptureRequested(object? sender, EventArgs e)
    {
        _ = Dispatcher.BeginInvoke(RequestRegionCapture);
    }

    private void OnScrollCaptureRequested(object? sender, EventArgs e)
    {
        _ = Dispatcher.BeginInvoke(RequestScrollCapture);
    }

    private void OnHistoryRequested(object? sender, EventArgs e)
    {
        _ = Dispatcher.BeginInvoke(ShowCaptureHistory);
    }

    private void OnSettingsSaved(object? sender, SettingsSavedEventArgs e)
    {
        _currentSettings = e.Settings;
        _themeManager?.Apply(e.Settings.Theme);
        if (_themeManager is not null)
        {
            _trayIconService?.ApplyTheme(_themeManager.ResolvedTheme);
        }
        _trayIconService?.SetVisible(e.Settings.ShowNotificationIcon);
    }

    private void OnHotKeyPressed(object? sender, HotKeyPressedEventArgs e)
    {
        if (_mainWindow?.IsCapturingHotKey == true)
        {
            return;
        }

        if (e.Action == HotKeyAction.RegionCapture)
        {
            RequestRegionCapture(e.DetachPreCapturedScreen());
        }
        else if (e.Action == HotKeyAction.RecognizeText)
        {
            RequestOcrCapture(e.DetachPreCapturedScreen());
        }
        else if (e.Action == HotKeyAction.PinImage)
        {
            RequestPinCapture();
        }
        else if (e.Action == HotKeyAction.ScrollCapture)
        {
            RequestScrollCapture();
        }
        else if (e.Action == HotKeyAction.OpenSettings)
        {
            _ = Dispatcher.BeginInvoke(ShowMainWindow);
        }
    }

    private void ShowMainWindow()
    {
        _mainWindow?.ShowFromTray();
    }

    private void ShowCaptureHistory()
    {
        if (_captureHistoryService is null)
        {
            return;
        }

        try
        {
            if (_captureHistoryWindow is null)
            {
                _captureHistoryWindow = new CaptureHistoryWindow(
                    _captureHistoryService,
                    _currentSettings.SaveDirectory);
                _captureHistoryWindow.Closed += OnCaptureHistoryWindowClosed;
                _captureHistoryWindow.Show();
                return;
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

            _mainWindow?.ShowStatus($"无法打开截图历史：{exception.Message}");
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

    private void RequestRegionCapture(CapturedImage? initialScreenSnapshot = null)
    {
        _ = RequestRegionCaptureAsync(initialScreenSnapshot);
    }

    private void RequestOcrCapture(CapturedImage? initialScreenSnapshot = null)
    {
        _ = RequestOcrCaptureAsync(initialScreenSnapshot);
    }

    private void RequestPinCapture()
    {
        _ = RequestPinCaptureAsync();
    }

    private void RequestScrollCapture()
    {
        _ = RequestScrollCaptureAsync();
    }

    private async Task RequestRegionCaptureAsync(
        CapturedImage? initialScreenSnapshot = null)
    {
        try
        {
            if (_regionCaptureCoordinator is not null)
            {
                await _regionCaptureCoordinator.RequestCaptureAsync(
                    initialScreenSnapshot);
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

    private async Task RequestOcrCaptureAsync(
        CapturedImage? initialScreenSnapshot = null)
    {
        try
        {
            if (_regionCaptureCoordinator is not null)
            {
                await _regionCaptureCoordinator.RequestOcrCaptureAsync(
                    initialScreenSnapshot);
            }
            else
            {
                initialScreenSnapshot?.Dispose();
            }
        }
        catch (Exception)
        {
            _mainWindow?.ShowStatus("文字识别失败，请检查语言设置。");
        }
    }

    private async Task RequestPinCaptureAsync()
    {
        try
        {
            if (_regionCaptureCoordinator is not null)
            {
                await _regionCaptureCoordinator.RequestPinCaptureAsync();
            }
        }
        catch (Exception)
        {
            _mainWindow?.ShowStatus("钉图失败，请重试。");
        }
    }

    private async Task RecognizePinnedImageAsync(CapturedImage image)
    {
        if (_translationHttpClient is null)
        {
            return;
        }

        try
        {
            var result = await OcrService.RecognizeAsync(
                image,
                _currentSettings.OcrLanguageTag);
            var window = new OcrResultWindow(
                result,
                () => _currentSettings,
                new DpapiTranslationCredentialStore(),
                _translationHttpClient)
            {
                Topmost = true,
            };
            window.Show();
            window.Activate();
        }
        catch (Exception)
        {
            _mainWindow?.ShowStatus("钉图文字识别失败，请检查语言设置。");
        }
    }

    private async Task RequestScrollCaptureAsync()
    {
        try
        {
            if (_regionCaptureCoordinator is not null)
            {
                await _regionCaptureCoordinator.RequestScrollCaptureAsync();
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
        _trayIconService.HistoryRequested -= OnHistoryRequested;
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
}
