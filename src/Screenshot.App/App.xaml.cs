using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using Screenshot.App.Capture;
using Screenshot.App.Core;
using Screenshot.App.Editor;
using Screenshot.App.Infrastructure;
using Screenshot.App.Pin;
using Screenshot.App.Presentation;
using Screenshot.App.Text;
using Screenshot.App.Update;

namespace Screenshot.App;

public partial class App : System.Windows.Application, IDisposable
{
    private const string SingleInstanceName = "Screenshot.App";
    // Keep the shortcut's current input/compositor turn free of WPF window
    // creation. This is deliberately limited to the shortcut hand-off; the
    // capture and long-screenshot algorithms are unchanged.
    // The WM_HOTKEY callback already returns before this dispatcher hand-off.
    // Waiting another display frame here only leaves the cursor in the old
    // foreground window while the user is already moving it. The overlay's
    // first screen copy remains asynchronous, so there is no GDI work on the
    // hook thread to protect with this delay.
    private static readonly TimeSpan HotKeyInputSettleDelay =
        TimeSpan.Zero;
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
    internal AppTheme FeedbackTheme => _themeManager?.ResolvedTheme ?? _currentSettings.Theme;
    private SingleInstanceCoordinator? _singleInstanceCoordinator;
    private AppSettings _currentSettings = AppSettings.CreateDefault();
    private Func<MainWindow>? _mainWindowFactory;
    private SettingsStore? _captureSettingsStore;
    private int _lastSettingsSection;
    private string? _pendingStartupStatus;
    private string? _pendingUpdateFailureStatus;
    private bool _isShuttingDown;
    private bool _isCaptureInProgress;
    private int _interactiveHotKeyPending;
    private long _captureStateNotificationVersion;
    private long _appliedCaptureStateNotificationVersion;
    private bool _startupCompleted;
    private IdleEfficiencyController? _idleEfficiency;
    private CancellationTokenSource? _modelCleanupCancellationSource;

    internal bool IsCaptureInProgressForDiagnostics => _isCaptureInProgress;

    protected override void OnStartup(StartupEventArgs e)
    {
        TranslationProfileHealth.Changed += OnTranslationProfileHealthChanged;
        WpfRenderingCompatibility.ConfigureForCurrentSession();
        base.OnStartup(e);
        TextBoxClipboardCommands.Install();
#if DEBUG
        AppDomain.CurrentDomain.UnhandledException += OnDebugUnhandledException;
        TaskScheduler.UnobservedTaskException += OnDebugUnobservedTaskException;
        AppDomain.CurrentDomain.ProcessExit += OnDebugProcessExit;
#endif
        StartupDiagnostics.ClearOldLogs();
        StartupDiagnostics.LogElevation($"OnStartup: pid={Environment.ProcessId}, args=[{string.Join(", ", e.Args)}]");
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
        CaptureTimingDiagnostics.Mark(
            "app-startup",
            $"pid={Environment.ProcessId} base={AppContext.BaseDirectory} background={startInBackground}");
        if (_featureWorkerPipeName is null)
        {
            _singleInstanceCoordinator = SingleInstanceCoordinator.TryAcquire(
                SingleInstanceName,
                RequestPrimaryWindowActivation,
                signalExistingInstance: !startInBackground);
            if (_singleInstanceCoordinator is null)
            {
                Shutdown();
                return;
            }
        }
        var settingsStore = new SettingsStore();
        _captureSettingsStore = settingsStore;
        settingsStore.DeferredSaveFailed += exception =>
        {
            if (Dispatcher.HasShutdownStarted) return;
            _ = Dispatcher.BeginInvoke(new Action(() =>
            {
                var message = $"无法保存标注设置：{exception.Message}";
                if (_mainWindow is { } window) window.ShowStatus(message);
                else _pendingStartupStatus = message;
            }));
        };
        CaptureTimingDiagnostics.Mark("startup-settings-store-created");
        var elevationLaunchService = new ElevationLaunchService();
        // A feature worker receives the resident's already validated settings
        // in the activation packet. Do not repeat disk/DPAPI/migration work on
        // every cold screenshot before that packet can be read.
        var elevationSettings = _featureWorkerPipeName is null
            ? settingsStore.Load().Settings : _currentSettings;
        var elevationResult = new ElevationLaunchResult(
            RelaunchStarted: false,
            Warning: null);
        var hasElevatedRelaunchMarker = e.Args.Any(argument =>
            string.Equals(
                argument,
                ElevationLaunchService.ElevatedRelaunchArgument,
                StringComparison.OrdinalIgnoreCase));
        var isElevated = _featureWorkerPipeName is null && ElevationLaunchService.IsCurrentProcessElevated();
        var isPortableLaunch = _featureWorkerPipeName is not null || !AppMetadata.IsInstalled;
        StartupDiagnostics.LogElevation($"Elevation state check: elevated={isElevated}, marker={hasElevatedRelaunchMarker}, request={elevationSettings.RequestAdministratorPrivileges}");
        CaptureTimingDiagnostics.Mark(
            "elevation-state",
            $"elevated={isElevated} portable={isPortableLaunch} marker={hasElevatedRelaunchMarker} request={elevationSettings.RequestAdministratorPrivileges}");

        // The elevated child is the only process that can create the
        // highest-run-level task. This is done after the first UAC consent;
        // failures are intentionally non-fatal and retain the runas fallback.
        if (_featureWorkerPipeName is null && !isPortableLaunch &&
            hasElevatedRelaunchMarker &&
            isElevated &&
            Environment.ProcessPath is { } elevatedProcessPath)
        {
            StartupDiagnostics.LogElevation($"Attempting to create persistent elevation task for: {elevatedProcessPath}");
            // Register the task before this elevated child becomes the
            // primary instance. A fire-and-forget registration can race with
            // shutdown/restart and cause another UAC prompt on the next run.
            var created = ElevationLaunchService.TryEnsurePersistentElevationTask(
                elevatedProcessPath);
            StartupDiagnostics.LogElevation($"Persistent elevation task creation result: {created}");
            CaptureTimingDiagnostics.Mark(
                "elevation-task-ensured",
                $"created={created}");
        }

#if !DEBUG
        if (_featureWorkerPipeName is null && !isPortableLaunch &&
            elevationLaunchService.ShouldRequestElevation(elevationSettings, e.Args))
        {
            StartupDiagnostics.LogElevation("Elevation required - entering elevation path");
            CaptureTimingDiagnostics.Mark("elevation-path-entered");
            // Release the per-session instance event before either the
            // persistent task or the UAC child starts. Otherwise the new
            // elevated process can be mistaken for a duplicate and exit.
            _singleInstanceCoordinator?.Dispose();
            _singleInstanceCoordinator = null;

            if (Environment.ProcessPath is { } processPath)
            {
                StartupDiagnostics.LogElevation($"Attempting to run persistent elevation task for: {processPath}");
                var taskStarted = ElevationLaunchService.TryRunPersistentElevationTask(
                    processPath,
                    e.Args);
                StartupDiagnostics.LogElevation($"Persistent task run result: {taskStarted}");
                if (taskStarted)
                {
                    CaptureTimingDiagnostics.Mark("elevation-persistent-task-selected");
                    Shutdown();
                    return;
                }
            }

            StartupDiagnostics.LogElevation("Persistent task not available, falling back to UAC relaunch");
            elevationResult = elevationLaunchService.TryRelaunchElevated(
                elevationSettings,
                e.Args);
            StartupDiagnostics.LogElevation($"UAC relaunch result: started={elevationResult.RelaunchStarted}, warning={elevationResult.Warning}");
            if (elevationResult.RelaunchStarted)
            {
                CaptureTimingDiagnostics.Mark("elevation-runas-child-started");
                // Keep the user's preference unchanged. The elevated child
                // carries the relaunch marker, while the persisted setting
                // remains checked for the next normal/startup launch.
                Shutdown();
                return;
            }

            _singleInstanceCoordinator = SingleInstanceCoordinator.TryAcquire(
                SingleInstanceName,
                RequestPrimaryWindowActivation,
                signalExistingInstance: !startInBackground);
            if (_singleInstanceCoordinator is null)
            {
                Shutdown();
                return;
            }
        }
#endif
        CaptureTimingDiagnostics.Mark("elevation-path-complete");
        var elevationWarning = elevationResult.Warning;

        var dataMigrationResult = _featureWorkerPipeName is null
            ? InstalledDataMigration.TryMigrateLegacyData()
            : new InstalledDataMigrationResult(false, null);
        WindowPlacementService.Initialize(AppMetadata.WindowPlacementsPath);
        var loadResult = _featureWorkerPipeName is null
            ? settingsStore.Load() : new SettingsLoadResult(_currentSettings, null);
        var credentialStore = new DpapiTranslationCredentialStore();
        var loadedSettings = _featureWorkerPipeName is not null ? _currentSettings : MigrateLegacySaveDirectory(
            loadResult.Settings,
            dataMigrationResult.Migrated);
        _currentSettings = _featureWorkerPipeName is not null ? loadedSettings : MigrateLegacyTranslationSettings(
            loadedSettings,
            settingsStore,
            credentialStore);
        TranslationTargetPreferences.Configure(_currentSettings.TranslationTargetLanguage, language =>
            PersistCapturePreference(current => current with { TranslationTargetLanguage = language },
                window => window.SaveTranslationTargetLanguage(language)));
        CaptureTimingDiagnostics.Mark("startup-settings-loaded");
        UiLocalization.Apply(_currentSettings.InterfaceLanguage);
        if (_featureWorkerPipeName is null) _ = Task.Run(() =>
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
        _themeManager = new AppThemeManager(residentShell: _useResidentHost);
        WindowActivation.Initialize();
        ClosedWindowAutomation.Initialize();
        _themeManager.ThemeChanged += OnThemeChanged;
        if (_featureWorkerPipeName is null) _themeManager.Apply(_currentSettings.Theme);
        CaptureTimingDiagnostics.Mark("startup-theme-applied");
        var startupRegistrationService = new StartupRegistrationService();
        var startupWarning = elevationWarning ?? (loadResult.Warning is null && _featureWorkerPipeName is null
            ? SynchronizeStartupRegistration(
                startupRegistrationService,
                _currentSettings.LaunchAtStartup)
            : null);
        // RegisterHotKey is enough for keyboard actions during startup. The
        // low-level/raw input hooks are installed after the first dispatcher
        // turn so hook setup cannot contend with the launch cursor.
        _hotKeyManager = new GlobalHotKeyManager(
            deferLowLevelInputHooks: true);
        CaptureTimingDiagnostics.Mark("startup-hotkey-manager-created");
        _hotKeyManager.HotKeyPressed += OnHotKeyPressed;
        _hotKeyManager.EscapePressed += OnFeatureEscapePressed;
        var hotKeyWarning = _featureWorkerPipeName is not null ? null : TryApplyInitialHotKeys(
            _hotKeyManager,
            _currentSettings);
        CaptureTimingDiagnostics.Mark(
            "hotkey-registration-complete",
            $"warning={(hotKeyWarning is null ? "none" : hotKeyWarning)}");
        if (_useResidentHost)
        {
            StartResidentShell(e.Args, startInBackground,
                dataMigrationResult.Warning ?? loadResult.Warning ?? startupWarning ?? hotKeyWarning);
            return;
        }
        _translationHttpClient = new HttpClient
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };

        // The settings window is not needed by the tray or capture paths. Its
        // XAML tree is the largest synchronous startup cost, so keep a factory
        // and construct it only when settings are actually opened.
        _mainWindowFactory = () => new MainWindow(
            _currentSettings,
            settingsStore,
            startupRegistrationService,
            _hotKeyManager,
            credentialStore,
            _translationHttpClient);
        AnnotationToolPreferences.Configure(
            _currentSettings.AnnotationToolSettings,
            settings => PersistCapturePreference(
                current => current with { AnnotationToolSettings = settings.ToArray() },
                window => window.SaveAnnotationToolSettings(settings)));
        _captureHistoryService = new CaptureHistoryService();
        _captureHistoryService.ConfigureRetentionPolicy(
            _currentSettings.ScreenshotHistoryRetentionDays,
            _currentSettings.HistoryLimit);
        _pinnedImageManager = new PinnedImageManager(
            RecognizePinnedImageAsync,
            TranslatePinnedImageAsync,
            () => _ = Dispatcher.BeginInvoke(ShowMainWindow),
            () => _currentSettings,
            colorText => PersistCapturePreference(
                current => current with { CustomStrokeColor = colorText },
                window => window.SaveCustomStrokeColor(colorText)),
            colors => PersistCapturePreference(
                current => current with { CustomColorPalette = colors.ToArray() },
                window => window.SaveCustomColorPalette(colors)),
            arrowStyle => PersistCapturePreference(
                current => current with { ArrowStyle = arrowStyle },
                window => window.SaveArrowStyle(arrowStyle)),
            arrowToolMode => PersistCapturePreference(
                current => current with { ArrowToolMode = arrowToolMode },
                window => window.SaveArrowToolMode(arrowToolMode)),
            shapeToolMode => PersistCapturePreference(
                current => current with { ShapeToolMode = shapeToolMode },
                window => window.SaveShapeToolMode(shapeToolMode)),
            tool => _mainWindow?.SaveLastAnnotationTool(tool),
            CopyPinnedGroupImageToClipboardAndHistory,
            RecognizePinnedImageAsync,
            TranslatePinnedImageWithCancellationAsync);
        _pinnedImageManager.DisplayStateChanged +=
            OnPinnedImageDisplayStateChanged;
        // Persisted pin images are optional state recovery. Defer image decode
        // and window creation until the shell has become responsive.
        _regionCaptureCoordinator = new RegionCaptureCoordinator(
            () => _currentSettings,
            _captureHistoryService,
            _pinnedImageManager,
            credentialStore,
            _translationHttpClient,
            message => _mainWindow?.ShowStatus(message),
            suspended => _hotKeyManager.SetMouseShortcutsSuspended(suspended),
            preferences => PersistCapturePreference(
                current => current with
                {
                    VideoRecordingCodec = preferences.Codec,
                    VideoRecordingFrameRate = preferences.FrameRate,
                    RecordSystemAudio = preferences.RecordSystemAudio,
                    RecordMicrophone = preferences.RecordMicrophone,
                    MicrophoneDeviceId = preferences.MicrophoneDeviceId,
                    CameraDeviceId = preferences.CameraDeviceId,
                    ShowKeyboardInputInRecording = preferences.ShowKeyboardInput,
                    ShowMouseInputInRecording = preferences.ShowMouseInput,
                    ShowMouseTrailInRecording = preferences.ShowMouseTrail,
                    ShowCameraInRecording = preferences.ShowCamera,
                    RecordingOutputFormat = preferences.OutputFormat,
                },
                window => window.SaveVideoRecordingPreferences(preferences)),
            preferences => PersistCapturePreference(
                current => current with
                {
                    ShapeToolMode = preferences.ShapeToolMode,
                    ArrowToolMode = preferences.ArrowToolMode,
                    ArrowStyle = preferences.ArrowStyle,
                    CustomStrokeColor = preferences.StrokeColor.Trim(),
                    DefaultStrokeWidth = preferences.StrokeWidth,
                },
                window => window.SaveVideoRecordingAnnotationPreferences(preferences)),
            arrowStyle => PersistCapturePreference(
                current => current with { ArrowStyle = arrowStyle },
                window => window.SaveArrowStyle(arrowStyle)),
            arrowToolMode => PersistCapturePreference(
                current => current with { ArrowToolMode = arrowToolMode },
                window => window.SaveArrowToolMode(arrowToolMode)),
            shapeToolMode => PersistCapturePreference(
                current => current with { ShapeToolMode = shapeToolMode },
                window => window.SaveShapeToolMode(shapeToolMode)),
            tool => _mainWindow?.SaveLastAnnotationTool(tool),
            colorText => PersistCapturePreference(
                current => current with { CustomStrokeColor = colorText },
                window => window.SaveCustomStrokeColor(colorText)),
            colors => PersistCapturePreference(
                current => current with { CustomColorPalette = colors.ToArray() },
                window => window.SaveCustomColorPalette(colors)),
            (x, y) => _mainWindow?.SaveCaptureToolbarPosition(x, y),
            () => _ = Dispatcher.BeginInvoke(
                () => ShowCaptureHistory(showVideo: true)),
            ShowRecordingAlreadyActiveFeedback,
            (kind, available, reason) => _mainWindow?.UpdateTranslationAvailabilityFromUse(
                kind, available, reason),
            captureAspectRatioChanged: SaveCaptureAspectRatio);
        _regionCaptureCoordinator.CaptureStateChanged += OnCaptureStateChanged;

        var updateFailureRequested = e.Args.Any(argument =>
            string.Equals(
                argument,
                PortableUpdateRunner.UpdateFailedArgument,
                StringComparison.OrdinalIgnoreCase));

        if (updateFailureRequested)
        {
            _pendingUpdateFailureStatus = ReadUpdateFailureStatus();
        }
        else if (dataMigrationResult.Warning is not null)
        {
            _pendingStartupStatus = dataMigrationResult.Warning;
        }
        else if (loadResult.Warning is not null)
        {
            _pendingStartupStatus = loadResult.Warning;
        }
        else if (startupWarning is not null)
        {
            _pendingStartupStatus = startupWarning;
        }
        else if (hotKeyWarning is not null)
        {
            _pendingStartupStatus = hotKeyWarning;
        }
        else if (TryGetArgumentValue(e.Args, "--updated") is { } updatedVersion)
        {
            _pendingStartupStatus =
                AppMetadata.FormatUpdatedVersionStatus(updatedVersion);
        }

        if (_featureWorkerPipeName is null)
        {
            _trayIconService = new TrayIconService(_themeManager.ResolvedTheme);
            _trayIconService.OpenSettingsRequested += OnOpenSettingsRequested;
            _trayIconService.RegionCaptureRequested += OnRegionCaptureRequested;
            _trayIconService.ScrollCaptureRequested += OnScrollCaptureRequested;
            _trayIconService.VideoRecordingRequested += OnVideoRecordingRequested;
            _trayIconService.HistoryRequested += OnHistoryRequested;
            _trayIconService.HidePinnedImagesRequested += OnHidePinnedImagesRequested;
            _trayIconService.ExitRequested += OnExitRequested;
            _trayIconService.RealtimeTranslationRequested += OnRealtimeTranslationRequested;
            UpdatePinnedImageTrayCommands();
            _trayIconService.SetVisible(_currentSettings.ShowNotificationIcon);
            CaptureTimingDiagnostics.Mark("startup-tray-ready");
            // The floating button is optional chrome; create it during deferred
            // startup so its HWND transition cannot hitch the launch cursor.

            // Respect explicit background launches. An ordinary launch opens the
            // settings only when requested or when an update failure needs action.
            if (!startInBackground &&
                (_currentSettings.OpenSettingsOnStartup || _pendingUpdateFailureStatus is not null))
            {
                _ = Dispatcher.BeginInvoke(
                    System.Windows.Threading.DispatcherPriority.ApplicationIdle,
                    new Action(ShowMainWindow));
            }
            else
            {
                // The unchecked startup option is also a background launch, even
                // when the executable was opened without --background.
                _ = StartupFeedbackWindow.ShowAsync("已最小化启动");
            }
        }
        // Do not force a full GC/workset trim during the first idle turn. It
        // suspends managed threads and can pause the desktop cursor exactly
        // when a low-end machine is still finishing its first paint. Capture
        // sessions keep their existing boundary trim after heavy work.
        _startupCompleted = true;
        _idleEfficiency = new IdleEfficiencyController();
        if (_featureWorkerPipeName is not null) StartFeatureWorker(_featureWorkerPipeName);

        // Keep startup responsive. These are best-effort warm-ups and must
        // never occupy the launch dispatcher or delay hotkey registration.
        if (_featureWorkerPipeName is null) _ = BeginDeferredStartupInfrastructureAsync();
    }

    private async Task BeginDeferredStartupInfrastructureAsync()
    {
        // RegisterHotKey is already active. Finish the optional first-paint
        // surfaces before attaching UI-thread low-level hooks: otherwise a
        // WPF constructor or image decode makes Windows wait for mouse input.
        await System.Windows.Threading.Dispatcher.Yield(
            System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        if (_isShuttingDown) return;
        if (_featureWorkerPipeName is null) UpdateFloatingCaptureWindow();
        await System.Windows.Threading.Dispatcher.Yield(
            System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        if (_isShuttingDown) return;
        _pinnedImageManager?.RestorePersisted();
        await System.Windows.Threading.Dispatcher.Yield(
            System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        if (_isShuttingDown) return;
        if (_featureWorkerPipeName is null) _hotKeyManager?.StartInputHooks();
        _ = CleanupLegacyTranslationModelAfterStartupAsync();
        // Capture windows, OCR models, audio devices and hardware encoders
        // initialize on demand. Launching the tray app must not initialize
        // every capture backend a few seconds later.
    }

    private async Task CleanupLegacyTranslationModelAfterStartupAsync()
    {
        using var cancellation = new CancellationTokenSource();
        _modelCleanupCancellationSource = cancellation;
        try
        {
            // Migrating an existing install must not hash gigabytes during
            // startup, on the dispatcher, or while a capture is active.
            await Task.Delay(TimeSpan.FromSeconds(30), cancellation.Token);
            while (_isCaptureInProgress && !_isShuttingDown)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), cancellation.Token);
            }

            if (!_isShuttingDown)
            {
                var released = await LocalLargeTranslationModelManager.Shared.CleanupLegacyModelAsync(
                    cancellation.Token);
                if (released == 0)
                {
                    await LocalLargeTranslationModelManager.LightweightShared.CleanupLegacyModelAsync(
                        cancellation.Token);
                }
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        finally
        {
            _modelCleanupCancellationSource = null;
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
#if DEBUG || SNAPCUT_CAPTURE_DIAGNOSTICS
        CaptureTimingDiagnostics.Mark(
            "app-exit",
            $"code={e.ApplicationExitCode} shuttingDown={_isShuttingDown} " +
            $"capture={_isCaptureInProgress}");
#endif
        _captureSettingsStore?.FlushPendingSave();
        Dispose();
        StartupDiagnostics.Flush();
        CaptureTimingDiagnostics.Flush();
        base.OnExit(e);
    }

#if DEBUG
    private static void OnDebugUnhandledException(
        object sender,
        UnhandledExceptionEventArgs e)
    {
        var exception = e.ExceptionObject as Exception;
        CaptureTimingDiagnostics.Mark(
            "appdomain-unhandled",
            $"terminating={e.IsTerminating} exception={exception}");
    }

    private static void OnDebugUnobservedTaskException(
        object? sender,
        UnobservedTaskExceptionEventArgs e)
    {
        CaptureTimingDiagnostics.Mark(
            "task-unobserved",
            $"exception={e.Exception}");
        e.SetObserved();
    }

    private static void OnDebugProcessExit(
        object? sender,
        EventArgs e)
    {
        CaptureTimingDiagnostics.Mark("process-exit");
    }
#endif

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
        _residentStartupFeedback?.Dispose();
        _residentStartupFeedback = null;
        _realtimeTranslationWindow?.Close();
        _featureHost?.Dispose();
        _residentSelectionCancellation?.Cancel();
        _residentSelectionWindow?.Dispose();
        _featureWorker?.Dispose();
        TranslationProfileHealth.Changed -= OnTranslationProfileHealthChanged;
        _idleEfficiency?.Dispose();
        _idleEfficiency = null;
        _modelCleanupCancellationSource?.Cancel();
        _mainWindowFactory = null;
        DisposeFloatingCaptureWindow();
        if (_regionCaptureCoordinator is not null)
        {
            _regionCaptureCoordinator.CaptureStateChanged -= OnCaptureStateChanged;
            _regionCaptureCoordinator.Dispose();
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
            _mainWindow.ImageQuestionRequested -= OnImageQuestionRequested;
        _mainWindow.RealtimeTranslationRequested -= OnRealtimeTranslationRequested;
            _mainWindow.HiddenIdle -= OnSettingsWindowHiddenIdle;
            _mainWindow.Closed -= OnSettingsWindowClosed;
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
        if (ForwardFeature("hide-pins")) return;
        _ = Dispatcher.BeginInvoke(() => _pinnedImageManager?.HideAll());
    }

    private void OnPinnedImageDisplayStateChanged(object? sender, EventArgs e)
    {
        PublishFeatureState();
        _hotKeyManager?.NotifyDesktopContentChanged();
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
        _idleEfficiency?.Wake();
        _ = Dispatcher.BeginInvoke((Action)RequestRegionCapture);
    }

    private void OnScrollCaptureRequested(object? sender, EventArgs e)
    {
        _idleEfficiency?.Wake();
        _ = Dispatcher.BeginInvoke(
            (Action)(() => RequestScrollCapture(pointerContinuation: null)));
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

    private void OnTranslationProfileHealthChanged(TranslationProfileHealthUpdate update)
    {
        if (_isShuttingDown || Dispatcher.HasShutdownStarted) return;
        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_isShuttingDown || TranslationProfileHealth.Latest(update.Profile) != update) return;
            var profiles = _currentSettings.TranslationProfiles;
            if (!profiles.Any(profile => TranslationProfileHealth.SameConnection(profile, update.Profile))) return;
            _currentSettings = _currentSettings with
            {
                TranslationProfiles = profiles.Select(profile => TranslationProfileHealth.SameConnection(profile, update.Profile)
                    ? profile with { LastAvailability = update.IsAvailable, LastAvailabilityReason = update.Reason }
                    : profile).ToArray(),
            };
            _mainWindow?.ApplyTranslationProfileHealth(update);
            _captureSettingsStore?.QueueSave(_currentSettings);
        }));
    }

    private void OnSettingsSaved(object? sender, SettingsSavedEventArgs e)
    {
        var previous = _currentSettings;
        _currentSettings = e.Settings;
        UiLocalization.Apply(e.Settings.InterfaceLanguage);
        RefreshRealtimeTranslationSettings(previous, e.Settings);
        PublishFeatureSettings();
        if (previous.AutoOcrAfterCapture != e.Settings.AutoOcrAfterCapture)
        {
            _pinnedImageManager?.RefreshAutomaticOcrSetting();
            CaptureOverlayWindow.ApplyAutomaticOcrSetting(e.Settings.AutoOcrAfterCapture);
        }
        if (previous.TranslationTargetLanguage != e.Settings.TranslationTargetLanguage)
            TranslationTargetPreferences.Publish(e.Settings.TranslationTargetLanguage);
        var screenshotRetentionChanged = previous.HistoryLimit != e.Settings.HistoryLimit ||
            previous.ScreenshotHistoryRetentionDays != e.Settings.ScreenshotHistoryRetentionDays;
        var videoRetentionChanged = previous.VideoHistoryLimit != e.Settings.VideoHistoryLimit ||
            previous.VideoHistoryRetentionDays != e.Settings.VideoHistoryRetentionDays ||
            previous.VideoSaveDirectory != e.Settings.VideoSaveDirectory;
        if (screenshotRetentionChanged) _captureHistoryService?.ConfigureRetentionPolicy(
            e.Settings.ScreenshotHistoryRetentionDays,
            e.Settings.HistoryLimit);
        if (screenshotRetentionChanged || videoRetentionChanged) _captureHistoryWindow?.UpdateRetentionPolicy(
            e.Settings.ScreenshotHistoryRetentionDays,
            e.Settings.HistoryLimit,
            e.Settings.VideoHistoryRetentionDays,
            e.Settings.VideoHistoryLimit);
        if (_captureHistoryWindow is not null && (screenshotRetentionChanged || videoRetentionChanged))
        {
            BeginCaptureHistoryRestore();
        }
        if (screenshotRetentionChanged || videoRetentionChanged) _ = Task.Run(() =>
        {
            if (screenshotRetentionChanged)
            {
                CaptureHistoryService.PruneCacheDirectory(e.Settings.HistoryLimit);
                CaptureHistoryService.PruneCacheDirectoryByAge(e.Settings.ScreenshotHistoryRetentionDays);
            }
            if (videoRetentionChanged) VideoHistoryService.ApplyRetentionPolicy(
                e.Settings.VideoSaveDirectory,
                e.Settings.VideoHistoryRetentionDays,
                e.Settings.VideoHistoryLimit);
        });
        if (previous.Theme != e.Settings.Theme) _themeManager?.Apply(e.Settings.Theme);
        if (previous.ShowNotificationIcon != e.Settings.ShowNotificationIcon)
            _trayIconService?.SetVisible(e.Settings.ShowNotificationIcon);
        if (previous.SaveDirectory != e.Settings.SaveDirectory || previous.VideoSaveDirectory != e.Settings.VideoSaveDirectory)
            _captureHistoryWindow?.UpdateDirectories(
            e.Settings.SaveDirectory,
            e.Settings.VideoSaveDirectory);
        if (previous.ShowFloatingCaptureButton != e.Settings.ShowFloatingCaptureButton)
            UpdateFloatingCaptureWindow();
    }

    private async Task RestoreCaptureHistoryAsync(
        CaptureHistoryService historyService)
    {
        using var featureActivity = FeatureActivity.Begin();
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
        if (_mainWindow is { } settingsWindow)
            settingsWindow.ApplySettingsPalette(theme);
        _trayIconService?.ApplyTheme(theme);
    }

    private void UpdateFloatingCaptureWindow()
    {
        if (_featureWorkerPipeName is not null) return;
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
        window.PresetCaptureRequested += OnFloatingPresetCaptureRequested;
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
        window.PresetCaptureRequested -= OnFloatingPresetCaptureRequested;
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

            // A transient close must not permanently remove the background
            // control. Explicit user closure updates settings separately;
            // only recreate here when the configured feature is still on and
            // no capture is active.
            if (!_isShuttingDown &&
                !_isCaptureInProgress &&
                _currentSettings.ShowFloatingCaptureButton)
            {
                _ = Dispatcher.BeginInvoke(
                    System.Windows.Threading.DispatcherPriority.ApplicationIdle,
                    new Action(UpdateFloatingCaptureWindow));
            }
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
        var notificationVersion = Interlocked.Increment(
            ref _captureStateNotificationVersion);
        CaptureTimingDiagnostics.Mark(
            "capture-state-notified",
            $"inProgress={isInProgress} version={notificationVersion}");
        using var timing = CaptureTimingDiagnostics.Begin(
            "app-capture-state",
            $"inProgress={isInProgress} version={notificationVersion}");
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(
                () => ApplyCaptureStateChanged(
                    isInProgress,
                    notificationVersion));
            return;
        }

        ApplyCaptureStateChanged(isInProgress, notificationVersion);
    }

    private void ApplyCaptureStateChanged(
        bool isInProgress,
        long notificationVersion)
    {
        if (notificationVersion < _appliedCaptureStateNotificationVersion)
        {
            CaptureTimingDiagnostics.Mark(
                "capture-state-stale",
                $"inProgress={isInProgress} version={notificationVersion} " +
                $"applied={_appliedCaptureStateNotificationVersion}");
            return;
        }

        _appliedCaptureStateNotificationVersion = notificationVersion;
        _isCaptureInProgress = isInProgress;
        _residentCaptureReady = isInProgress && _featureWorker is not null
            ? _featureWorker.PrepareResidentCaptureAsync() : Task.CompletedTask;
        PublishFeatureState();
        _idleEfficiency?.SetCaptureActive(isInProgress);
        MemoryFootprint.SetInteractionActive(isInProgress);
        CaptureTimingDiagnostics.Mark(
            "capture-state-applied",
            $"inProgress={isInProgress} version={notificationVersion} " +
            $"floatingVisible={_floatingCaptureWindow?.IsVisible}");
        // State cleanup must continue even if one optional subscriber has a
        // stale native handle during overlay teardown.
        try
        {
            _hotKeyManager?.SetCaptureOverlayActive(isInProgress);
        }
        catch (Exception)
        {
            // A stale native hook must not prevent the rest of the teardown.
        }

        try
        {
            VideoRecordingControlWindow.SetCaptureInteractionActive(isInProgress);
        }
        catch (Exception)
        {
            // A closing recording window must not strand capture state.
        }

        if (_floatingCaptureWindow is null &&
            !isInProgress &&
            !_isShuttingDown &&
            _currentSettings.ShowFloatingCaptureButton)
        {
            // Recreating/showing the floating HWND in the same turn as the
            // capture hotkey adds another z-order transition. Let the cursor
            // finish its current input burst before restoring the button.
            _ = Dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.ContextIdle,
                new Action(() =>
                {
                    if (!_isShuttingDown &&
                        !_isCaptureInProgress &&
                        _floatingCaptureWindow is null &&
                        _currentSettings.ShowFloatingCaptureButton)
                    {
                        UpdateFloatingCaptureWindow();
                        CaptureTimingDiagnostics.Mark(
                            "floating-recreated-after-capture");
                    }
                }));
        }
        else
        {
            try
            {
                var floatingWindow = _floatingCaptureWindow;
                if (isInProgress && floatingWindow is not null)
                {
                    // Hiding a WPF window synchronously here causes another
                    // z-order/DWM transition on the shortcut's first frame.
                    // Queue before the overlay's input-priority startup so
                    // its first desktop copy cannot include our floating UI.
                    _ = Dispatcher.BeginInvoke(
                        System.Windows.Threading.DispatcherPriority.Input,
                        new Action(() =>
                        {
                            if (!_isShuttingDown &&
                                _isCaptureInProgress &&
                                ReferenceEquals(
                                    _floatingCaptureWindow,
                                    floatingWindow))
                            {
                                floatingWindow.SetCaptureInProgress(true);
                            }
                        }));
                }
                else
                {
                    // Restoring the floating HWND can synchronously perform
                    // layout and display reconciliation. Keep it out of the
                    // overlay close/input turn so the pointer returns to the
                    // foreground app without a final hitch.
                    if (floatingWindow is not null)
                    {
                        _ = Dispatcher.BeginInvoke(
                            System.Windows.Threading.DispatcherPriority.ContextIdle,
                            new Action(() =>
                            {
                                if (!_isShuttingDown &&
                                    !_isCaptureInProgress &&
                                    ReferenceEquals(
                                        _floatingCaptureWindow,
                                        floatingWindow))
                                {
                                    floatingWindow.SetCaptureInProgress(false);
                                }
                            }));
                    }
                }
            }
            catch (Exception)
            {
                // The window may be in its Closed callback; recovery is
                // retried by the normal floating-window lifecycle path.
                CaptureTimingDiagnostics.Mark("floating-state-apply-failed");
            }
        }
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

    private void OnFloatingPresetCaptureRequested(object? sender, EventArgs e)
    {
        _ = RequestPresetCaptureAsync();
    }

    private async Task RequestPresetCaptureAsync()
    {
        if (ForwardFeature("preset")) return;
        using var featureActivity = FeatureActivity.Begin();
        try
        {
            var store = new PresetCaptureRegionStore();
            var current = store.Load();
            if (current.Count == 0)
            {
                var configured = await PresetCaptureWindow.ShowAsync(current);
                if (configured is null)
                {
                    return;
                }

                store.Save(configured);
                current = configured;
                _mainWindow?.ShowStatus(
                    configured.Count == 0
                        ? "预设截图已清空。"
                        : $"已保存 {configured.Count} 个预设截图区域。鼠标移入编号后点击即可截图。");
            }

            if (current.Count == 0)
            {
                return;
            }

            while (current.Count > 0)
            {
                var result = await PresetCaptureExecuteWindow.ShowAsync(current);
                if (result?.ClearAll == true)
                {
                    store.Save([]);
                    _mainWindow?.ShowStatus("已清空全部预设截图区域。");
                    return;
                }

                if (result?.EditIndex is int editIndex)
                {
                    _mainWindow?.ShowStatus(
                        editIndex >= 0
                            ? $"正在编辑第 {editIndex + 1} 个预设截图区域。可拖动移动，拖右下角调整大小，右键返回。"
                            : "正在添加预设截图区域。最多可设置 5 个区域，右键返回。" );
                    var edited = await PresetCaptureWindow.ShowAsync(current, editIndex);
                    if (edited is not null)
                    {
                        store.Save(edited);
                        current = edited;
                    }

                    continue;
                }

                if (result?.Region is { } region)
                {
                    await CapturePresetRegionAsync(region);
                }

                break;
            }
        }
        catch (Exception exception)
        {
            _mainWindow?.ShowStatus($"预设截图失败：{exception.Message}");
        }
    }

    private async Task CapturePresetRegionAsync(ScreenRegion region)
    {
        if (region.IsEmpty || _captureHistoryService is null)
        {
            return;
        }

        var settings = _currentSettings;
        using var image = await Task.Run(() => ScreenCaptureService.Capture(region));
        await ClipboardImageService.SetImageAsync(image.Preview);
        _ = _captureHistoryService.Add(image, Math.Max(1, settings.HistoryLimit));
        await CaptureSuccessWindow.ShowAsync(region);
        _mainWindow?.ShowStatus("预设截图已完成，已复制到剪贴板并保存到截图历史。");
    }

    private void OnFloatingHistoryRequested(object? sender, EventArgs e)
    {
        ShowCaptureHistory();
    }

    private void OnFloatingCloseRequested(object? sender, EventArgs e)
    {
        if (_featureHost?.ProcessId is not null)
        {
            ForwardFeature("hide-floating");
            return;
        }
        PersistCapturePreference(
            current => current with { ShowFloatingCaptureButton = false },
            window => window.SetFloatingCaptureButtonEnabled(false));
        DisposeFloatingCaptureWindow();
    }

    private async Task RequestFloatingCaptureAsync()
    {
        if (ForwardFeature("repeat")) return;
        using var featureActivity = FeatureActivity.Begin();
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
        if (ForwardFeature("all-screens")) return;
        using var featureActivity = FeatureActivity.Begin();
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
        if (ForwardHotKey(HotKeyAction.VideoRecording, null, pointerContinuation)) return;
        using var featureActivity = FeatureActivity.Begin();
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
        if (_featureHost is not null)
        {
            _ = SendFeatureAsync(FeatureMessage.ForHotKey(e) with
            { TargetWindow = e.Action == HotKeyAction.RealtimeTranslation ? Text.Realtime.RealtimeWindowTarget.Foreground.ToInt64() : 0 });
            return;
        }
        if (_waitingForResidentSelection) return;
        _idleEfficiency?.Wake();
        StartupDiagnostics.LogHotKey($"HotKey pressed: action={e.Action}");
        CaptureTimingDiagnostics.BeginInputWindow($"hotkey action={e.Action}");
        CaptureTimingDiagnostics.Mark("hotkey-handler", $"action={e.Action}");
        if (_mainWindow?.IsCapturingHotKey == true)
        {
            StartupDiagnostics.LogHotKey("HotKey ignored: main window is capturing");
            return;
        }

        if (e.Action == HotKeyAction.RegionCapture)
        {
            StartupDiagnostics.LogHotKey("Queueing RegionCapture");
            QueueHotKeyCapture(e, static (app, snapshot, continuation) =>
                app.RequestRegionCapture(
                    snapshot,
                    continuation,
                    // Keyboard shortcuts acquire the first frame in the
                    // overlay worker so the input dispatcher never waits on GDI.
                    deferInitialColorPickerActivation:
                        continuation is null));
        }
        else if (e.Action == HotKeyAction.CompleteCapture)
        {
            StartupDiagnostics.LogHotKey("CompleteCapture action");
            CaptureOverlayWindow.TryCompleteActiveInteractiveSelection();
        }
        else if (e.Action == HotKeyAction.RecognizeText)
        {
            StartupDiagnostics.LogHotKey("Queueing RecognizeText");
            QueueHotKeyCapture(e, static (app, snapshot, continuation) =>
                app.RequestTranslationCapture(
                    snapshot,
                    continuation,
                    deferInitialColorPickerActivation:
                        continuation is null));
        }
        else if (e.Action == HotKeyAction.PinImage)
        {
            StartupDiagnostics.LogHotKey("Queueing PinImage");
            QueueHotKeyCapture(e, static (app, snapshot, continuation) =>
                app.RequestPinCapture(
                    snapshot,
                    continuation,
                    deferInitialColorPickerActivation:
                        continuation is null));
        }
        else if (e.Action == HotKeyAction.ScrollCapture)
        {
            RequestScrollCapture(e.CapturePointerContinuation);
        }
        else if (e.Action == HotKeyAction.VideoRecording)
        {
            QueueHotKeyCapture(e, static (app, snapshot, continuation) =>
            {
                snapshot?.Dispose();
                // Release the pending-input slot after handing off selection.
                // This task lives until the recording ends; awaiting it here
                // blocked every subsequent screenshot hotkey for that duration.
                // The coordinator separately guards selection and recording.
                _ = app.RequestVideoRecordingAsync(continuation);
            });
        }
        else if (e.Action == HotKeyAction.EndVideoRecording)
        {
            _ = VideoRecordingControlWindow.TryEndActiveRecording();
        }
        else if (e.Action == HotKeyAction.OpenSettings)
        {
            _ = Dispatcher.BeginInvoke(ShowMainWindow);
        }
        else if (e.Action == HotKeyAction.RealtimeTranslation)
        {
            _ = ToggleRealtimeTranslationAsync(Text.Realtime.RealtimeWindowTarget.Foreground);
        }
        else if (e.Action == HotKeyAction.TranslateSelectedText)
        {
            _ = TranslateSelectedTextAsync();
        }
        else if (e.Action == HotKeyAction.ImageQuestion)
        {
            _ = ShowEmptyImageQuestionAsync();
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
        if (ForwardFeature("settings")) return;
        _idleEfficiency?.Wake();
        var inputPresentation = _hotKeyManager?.SuspendMouseForWindowPresentation();
        try
        {
            // Settings must own keyboard focus while shortcut fields are edited.
            // A still-visible topmost capture overlay would otherwise receive
            // standalone letters and create text annotations behind the settings
            // window.
            CaptureOverlayWindow.CloseActiveInteractiveSelectionForTransition();
            EnsureMainWindow();
            if (_mainWindow is null)
            {
                return;
            }

            if (_pendingUpdateFailureStatus is { } updateStatus)
            {
                _pendingUpdateFailureStatus = null;
                _mainWindow.ShowUpdateFailureRetry(updateStatus, showWindow: false);
            }

            if (_pendingStartupStatus is { } startupStatus)
            {
                _pendingStartupStatus = null;
                _mainWindow.ShowStatus(startupStatus);
            }

            _mainWindow.ShowFromTray();
        }
        finally
        {
            // Show() can return before first layout/render and activation
            // callbacks finish. Restore the hook only after that work drains.
            _ = Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, new Action(() =>
            {
                inputPresentation?.Dispose();
                FeatureStartupCompilation.Complete();
            }));
        }
    }

    private void SaveCaptureAspectRatio(CaptureAspectRatio ratio)
    {
        if (!Enum.IsDefined(ratio)) return;
        PersistCapturePreference(
            current => current with { CaptureAspectRatio = ratio },
            window => window.SaveCaptureAspectRatio(ratio));
    }

    private void PersistCapturePreference(
        Func<AppSettings, AppSettings> update,
        Action<MainWindow> saveInSettingsWindow)
    {
        if (_mainWindow is { } window)
        {
            saveInSettingsWindow(window);
            return;
        }
        // A tray-only session has no settings window. Update the shared state
        // immediately for the next capture, and debounce disk I/O off the UI.
        _currentSettings = update(_currentSettings);
        TranslationTargetPreferences.Publish(_currentSettings.TranslationTargetLanguage);
        _captureSettingsStore?.QueueSave(_currentSettings);
        PublishFeatureSettings();
    }

    private void EnsureMainWindow()
    {
        if (_mainWindow is not null || _mainWindowFactory is not { } factory)
        {
            return;
        }

        using (CaptureTimingDiagnostics.Begin("main-window-constructor"))
        {
            _mainWindow = factory();
        }

        if (_themeManager is not null)
        {
            _mainWindow.ApplySettingsPalette(_themeManager.ResolvedTheme);
        }

        MainWindow = _mainWindow;
        _mainWindow.SettingsSaved += OnSettingsSaved;
        _mainWindow.ExitRequested += OnExitRequested;
        _mainWindow.UpdateInstallationStarted += OnUpdateInstallationStarted;
        _mainWindow.TextTranslationRequested += OnTextTranslationRequested;
        _mainWindow.ImageQuestionRequested += OnImageQuestionRequested;
        _mainWindow.RealtimeTranslationRequested += OnRealtimeTranslationRequested;
        _mainWindow.HiddenIdle += OnSettingsWindowHiddenIdle;
        _mainWindow.Closed += OnSettingsWindowClosed;
        _mainWindow.ConfigureTaskbarVisibility(_currentSettings.ShowTaskbarIcon);
        _mainWindow.RestoreSection(_lastSettingsSection);
        CaptureTimingDiagnostics.Mark("main-window-created");
    }

    private void OnSettingsWindowHiddenIdle(object? sender, EventArgs e)
    {
        if (sender is not MainWindow window || !ReferenceEquals(window, _mainWindow)) return;
        if (_isCaptureInProgress)
        {
            window.ScheduleIdleMemoryTrim();
            return;
        }
        _lastSettingsSection = window.SelectedSection;
        // Close only the retired settings UI. Tray services and the current
        // settings snapshot remain available, and downloads keep their owner.
        window.RequestExit();
        MemoryFootprint.TrimAfterHeavyOperation();
    }

    private void OnSettingsWindowClosed(object? sender, EventArgs e)
    {
        if (sender is not MainWindow window || !ReferenceEquals(window, _mainWindow)) return;
        window.SettingsSaved -= OnSettingsSaved;
        window.ExitRequested -= OnExitRequested;
        window.UpdateInstallationStarted -= OnUpdateInstallationStarted;
        window.TextTranslationRequested -= OnTextTranslationRequested;
        window.ImageQuestionRequested -= OnImageQuestionRequested;
        window.RealtimeTranslationRequested -= OnRealtimeTranslationRequested;
        window.HiddenIdle -= OnSettingsWindowHiddenIdle;
        window.Closed -= OnSettingsWindowClosed;
        _mainWindow = null;
        if (ReferenceEquals(MainWindow, window)) MainWindow = null;
        if (_featureWorker is not null)
        {
            _ = _featureWorker.SendAsync(new("section") { Section = _lastSettingsSection });
            _featureWorker.ScheduleIdle();
        }
    }

    private void RequestPrimaryWindowActivation()
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
        {
            return;
        }

        if (!_startupCompleted)
        {
            // Ignore activation events received while this process is still
            // constructing its windows and hotkeys. Those events can be stale
            // signals left by a previous launch and must not replay a settings
            // window after startup, especially when the option is disabled.
            return;
        }

        // A second executable launch is only an instance-presence check. It
        // must never turn a background/tray launch into a settings window;
        // the user can open settings explicitly from the tray or hotkey.
    }

    private async Task TranslateSelectedTextAsync()
    {
        using var featureActivity = FeatureActivity.Begin();
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
        if (_featureHost is not null)
        {
            _ = SendFeatureAsync(new("command") { Command = "text", Text = sourceText, Flag = translateImmediately });
            return;
        }
        if (_textTranslationWindow is null)
        {
            _textTranslationWindow = new TextTranslationWindow(
                () => _currentSettings,
                languageTag => PersistCapturePreference(
                    current => current with { TranslationTargetLanguage = languageTag.Trim() },
                    window => window.SaveTranslationTargetLanguage(languageTag)),
                languageTag =>
                {
                    ShowMainWindow();
                    _mainWindow?.OpenTranslationModelSettings(languageTag);
                });
            _textTranslationWindow.Closed += OnTextTranslationWindowClosed;
            WindowActivation.Show(_textTranslationWindow);
        }
        else
        {
            WindowActivation.Show(_textTranslationWindow);
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
        if (ForwardFeature(showVideo ? "video-history" : "history")) return;
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
                    _currentSettings.VideoHistoryLimit,
                    _currentSettings.PngSaveLocationMode);
                _captureHistoryWindow.Closed += OnCaptureHistoryWindowClosed;
                if (showVideo)
                {
                    _captureHistoryWindow.ShowVideoHistory();
                }
                WindowActivation.Show(_captureHistoryWindow);
                return;
            }

            _captureHistoryWindow.UpdateDirectories(
                _currentSettings.SaveDirectory,
                _currentSettings.VideoSaveDirectory);
            if (showVideo)
            {
                _captureHistoryWindow.ShowVideoHistory();
            }
            WindowActivation.Show(_captureHistoryWindow);
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
        CapturePointerContinuation? pointerContinuation,
        bool deferInitialColorPickerActivation = false)
    {
        _ = RequestRegionCaptureAsync(
            initialScreenSnapshot,
            pointerContinuation,
            deferInitialColorPickerActivation);
    }

    private void RequestTranslationCapture(
        CapturedImage? initialScreenSnapshot,
        CapturePointerContinuation? pointerContinuation,
        bool deferInitialColorPickerActivation = false)
    {
        _ = RequestTranslationCaptureAsync(
            initialScreenSnapshot,
            pointerContinuation,
            deferInitialColorPickerActivation);
    }

    private void RequestPinCapture(
        CapturedImage? initialScreenSnapshot = null,
        CapturePointerContinuation? pointerContinuation = null,
        bool deferInitialColorPickerActivation = false)
    {
        _ = RequestPinCaptureAsync(
            initialScreenSnapshot,
            pointerContinuation,
            deferInitialColorPickerActivation);
    }

    private void RequestScrollCapture(
        CapturePointerContinuation? pointerContinuation)
    {
        _ = RequestScrollCaptureAsync(pointerContinuation);
    }

    private async Task RequestRegionCaptureAsync(
        CapturedImage? initialScreenSnapshot,
        CapturePointerContinuation? pointerContinuation,
        bool deferInitialColorPickerActivation = false)
    {
        if (ForwardHotKey(HotKeyAction.RegionCapture, initialScreenSnapshot, pointerContinuation)) return;
        using var featureActivity = FeatureActivity.Begin();
        try
        {
            if (_regionCaptureCoordinator is not null)
            {
                await _regionCaptureCoordinator.RequestCaptureAsync(
                    initialScreenSnapshot,
                    pointerContinuation,
                    deferInitialColorPickerActivation);
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
        CapturePointerContinuation? pointerContinuation,
        bool deferInitialColorPickerActivation = false)
    {
        if (ForwardHotKey(HotKeyAction.RecognizeText, initialScreenSnapshot, pointerContinuation)) return;
        using var featureActivity = FeatureActivity.Begin();
        try
        {
            if (_regionCaptureCoordinator is not null)
            {
                await _regionCaptureCoordinator.RequestTranslationCaptureAsync(
                    initialScreenSnapshot,
                    pointerContinuation,
                    deferInitialColorPickerActivation);
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
        CapturePointerContinuation? pointerContinuation,
        bool deferInitialColorPickerActivation = false)
    {
        if (ForwardHotKey(HotKeyAction.PinImage, initialScreenSnapshot, pointerContinuation)) return;
        using var featureActivity = FeatureActivity.Begin();
        try
        {
            if (_regionCaptureCoordinator is not null)
            {
                await _regionCaptureCoordinator.RequestPinCaptureAsync(
                    initialScreenSnapshot,
                    pointerContinuation,
                    deferInitialColorPickerActivation);
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

    private Task<OcrRecognitionResult> RecognizePinnedImageAsync(CapturedImage image) =>
        RecognizePinnedImageAsync(image, CancellationToken.None);

    private async Task<OcrRecognitionResult> RecognizePinnedImageAsync(
        CapturedImage image, CancellationToken cancellationToken)
    {
        try
        {
            return await OcrProviderFactory.RecognizeAsync(
                image,
                _currentSettings, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            _mainWindow?.ShowStatus("钉图文字识别失败，请检查语言设置。");
            return OcrRecognitionResult.Failure(
                "钉图文字识别失败，请检查语言设置。");
        }
    }

    private void CopyPinnedGroupImageToClipboardAndHistory(CapturedImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        var historyLimit = Math.Max(1, _currentSettings.HistoryLimit);
        _ = CopyPinnedGroupImageToClipboardAndHistoryAsync(image, historyLimit);
    }

    private async Task CopyPinnedGroupImageToClipboardAndHistoryAsync(
        CapturedImage image,
        int historyLimit)
    {
        using var featureActivity = FeatureActivity.Begin();
        CaptureHistoryItem? historyItem = null;
        try
        {
            historyItem = _captureHistoryService?.Add(image, historyLimit);
            try
            {
                await ClipboardImageService.SetImageAsync(image.Preview);
                historyItem?.MarkCopied();
            }
            catch (ExternalException)
            {
                _mainWindow?.ShowStatus("剪贴板正被其他程序使用，请重试。");
            }
        }
        catch (Exception)
        {
            _mainWindow?.ShowStatus("保存钉图编组失败，请重试。");
        }
        finally
        {
            image.Dispose();
        }
    }

    private Task<TranslationSegmentsResult> TranslatePinnedImageAsync(OcrRecognitionResult recognition) =>
        TranslatePinnedImageWithCancellationAsync(recognition, CancellationToken.None);

    private async Task<TranslationSegmentsResult> TranslatePinnedImageWithCancellationAsync(
        OcrRecognitionResult recognition, CancellationToken cancellationToken)
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
                _translationHttpClient);
            return await provider.TranslateSegmentsAsync(
                recognition.Regions.Select(region => region.Text).ToArray(),
                "auto",
                _currentSettings.TranslationTargetLanguage,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
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
        if (ForwardHotKey(HotKeyAction.ScrollCapture, null, pointerContinuation)) return;
        using var featureActivity = FeatureActivity.Begin();
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

        if (_featureWorker is not null && !_retiringFeatureWorker)
        {
            _ = ExitFeatureAndResidentAsync();
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
        _trayIconService.RealtimeTranslationRequested -= OnRealtimeTranslationRequested;
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
        _hotKeyManager.EscapePressed -= OnFeatureEscapePressed;
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
        // Preserve a recognized provider from the settings file. Older
        // versions sometimes stored an API key in TranslationProvider; only
        // that legacy shape should be converted to the custom entry.
        var resolvedProvider = TranslationProviderFactory.ResolveProviderId(
            configuredProvider);
        var migrated = settings with
        {
            TranslationProvider = configuredProvider.StartsWith("sk-", StringComparison.OrdinalIgnoreCase)
                ? TranslationProviderFactory.OpenAiCompatibleProviderId
                : resolvedProvider,
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
                migrated.TranslationProvider,
                configuredProvider,
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
            hotKeyManager.ConfigureMouseLongPress(settings.MouseLongPressMilliseconds, settings.MouseSideButtonsUseLongPress);
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

    private static string ReadUpdateFailureStatus()
    {
        var failurePath = Path.Combine(
            AppMetadata.UpdatesDirectoryPath,
            "last-update-failure.txt");
        try
        {
            if (!File.Exists(failurePath))
            {
                return "软件更新未完成，已恢复运行旧版本。请稍后重试。";
            }

            var details = File.ReadAllText(failurePath).Trim();
            File.Delete(failurePath);
            var detailLine = details
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .LastOrDefault(line =>
                    !line.StartsWith("版本:", StringComparison.OrdinalIgnoreCase) &&
                    !DateTimeOffset.TryParse(line, out _));
            return string.IsNullOrWhiteSpace(detailLine)
                ? "软件更新未完成，已恢复运行旧版本。请稍后重试。"
                : $"软件更新未完成，已恢复运行旧版本：{detailLine.Trim()}";
        }
        catch
        {
            return "软件更新未完成，已恢复运行旧版本。请稍后重试。";
        }
    }

    private void QueueHotKeyCapture(
        HotKeyPressedEventArgs eventArgs,
        Action<App, CapturedImage?, CapturePointerContinuation?> request)
    {
        if (!TryClaimInteractiveHotKey(eventArgs.Action))
        {
            eventArgs.DetachPreCapturedScreen()?.Dispose();
            return;
        }

        var snapshot = DetachUsablePreCapturedScreen(eventArgs);
        var continuation = eventArgs.CapturePointerContinuation;
        StartupDiagnostics.LogHotKey($"QueueHotKeyCapture: action={eventArgs.Action}, hasSnapshot={snapshot is not null}");
        CaptureTimingDiagnostics.Mark(
            "hotkey-queue-detached",
            $"action={eventArgs.Action} hasSnapshot={snapshot is not null}");
        // Even when a frame is already prepared, constructing the WPF
        // capture surface synchronously from the hotkey callback causes one
        // compositor frame of input hitch. Queue the hand-off after the
        // keyboard message returns; the prepared frame is still reused, so
        // this does not add another desktop capture.
        _ = Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Input,
            new Func<Task>(async () =>
            {
                try
                {
                    // Keep the hand-off on the input queue, but do not add an
                    // artificial frame delay. The WPF show path is already
                    // asynchronous and the initial desktop copy is deferred.
                    StartupDiagnostics.LogHotKey($"QueueHotKeyCapture delay: action={eventArgs.Action}");
                    await Task.Delay(HotKeyInputSettleDelay);
                    if (_isCaptureInProgress)
                    {
                        snapshot?.Dispose();
                        return;
                    }

                    StartupDiagnostics.LogHotKey($"QueueHotKeyCapture executing: action={eventArgs.Action}");
                    CaptureTimingDiagnostics.Mark(
                        "hotkey-dispatcher-callback",
                        $"action={eventArgs.Action}");
                    request(this, snapshot, continuation);
                }
                finally
                {
                    Interlocked.Exchange(ref _interactiveHotKeyPending, 0);
                }
            }));
    }

    private bool TryClaimInteractiveHotKey(HotKeyAction action)
    {
        if (_isCaptureInProgress ||
            Interlocked.CompareExchange(ref _interactiveHotKeyPending, 1, 0) != 0)
        {
            StartupDiagnostics.LogHotKey(
                $"HotKey ignored: interactive request already pending, action={action}");
            CaptureTimingDiagnostics.Mark(
                "hotkey-coalesced",
                $"action={action} captureInProgress={_isCaptureInProgress}");
            return false;
        }

        return true;
    }
}
