using System.IO;
using System.Windows;
using System.Windows.Threading;
using Screenshot.App.Capture;
using Screenshot.App.Core;
using Screenshot.App.Editor;
using Screenshot.App.Infrastructure;
using Screenshot.App.Pin;
using Screenshot.App.Presentation;
using Screenshot.App.Text;
using Screenshot.App.Update;

namespace Screenshot.App;

public partial class App
{
    private readonly bool _useResidentHost;
    private readonly string? _featureWorkerPipeName;
    private readonly Task<FeatureProcessWorker.Startup>? _featureStartup;
    private FeatureProcessHost? _featureHost;
    private FeatureProcessWorker? _featureWorker;
    private bool _retiringFeatureWorker;
    private Task _residentCaptureReady = Task.CompletedTask;
    private ScreenRegion? _residentLastCaptureRegion;
    private int _featureEscapeVersion;
    private int _featureCapturePending;
    private ResidentStartupFeedback? _residentStartupFeedback;
    private void OnFeatureEscapePressed()
    {
        Interlocked.Increment(ref _featureEscapeVersion);
        if (_residentSelectionCancellation is { } cancellation)
            _ = Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!ReferenceEquals(_residentSelectionCancellation, cancellation)) return;
                if (_residentSelectionWindow is { } window) window.Cancel();
                else cancellation.Cancel();
            }));
        if (_waitingForResidentSelection && _featureWorker is { } worker)
            _ = worker.SendAsync(new("native-cancel"));
    }
    internal Task WaitForResidentCaptureChromeAsync() => _residentCaptureReady;

    public App() { }

    internal App(bool useResidentHost, string? featureWorkerPipeName = null)
    {
        _useResidentHost = useResidentHost;
        _featureWorkerPipeName = featureWorkerPipeName;
        if (featureWorkerPipeName is not null)
            _featureStartup = FeatureProcessWorker.PrepareAsync(featureWorkerPipeName);
    }

    private void StartResidentShell(string[] args, bool background, string? warning)
    {
        _pendingStartupStatus = warning;
        if (args.Contains(PortableUpdateRunner.UpdateFailedArgument, StringComparer.OrdinalIgnoreCase))
            _pendingUpdateFailureStatus = ReadUpdateFailureStatus();
        else if (TryGetArgumentValue(args, "--updated") is { } version)
            _pendingStartupStatus = AppMetadata.FormatUpdatedVersionStatus(version);

        _featureHost = new FeatureProcessHost(Dispatcher, () =>
        {
            // Transfer input only after the worker has loaded. It registers
            // immediately after this acknowledgement; idle startup stays live.
            DisposeHotKeyManager();
            return new("activate") { Settings = _currentSettings, LastCaptureRegion = _residentLastCaptureRegion,
                Flag = _residentSelectionWindow is not null };
        }, ResumeResidentInput, ReceiveFeatureState, () => _currentSettings, ReleaseResidentSelection);

        _trayIconService = new TrayIconService(_themeManager!.ResolvedTheme);
        _trayIconService.OpenSettingsRequested += OnOpenSettingsRequested;
        _trayIconService.RegionCaptureRequested += OnRegionCaptureRequested;
        _trayIconService.ScrollCaptureRequested += OnScrollCaptureRequested;
        _trayIconService.VideoRecordingRequested += OnVideoRecordingRequested;
        _trayIconService.HistoryRequested += OnHistoryRequested;
        _trayIconService.HidePinnedImagesRequested += OnHidePinnedImagesRequested;
        _trayIconService.ExitRequested += OnExitRequested;
        _trayIconService.RealtimeTranslationRequested += OnRealtimeTranslationRequested;
        _trayIconService.SetVisible(_currentSettings.ShowNotificationIcon);
        _startupCompleted = true;
        _idleEfficiency = new IdleEfficiencyController();
        _ = Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
        {
            UpdateFloatingCaptureWindow();
            if (new PinnedImagePersistenceStore().Load().Count > 0) ForwardFeature("restore");
            if (!background && (_currentSettings.OpenSettingsOnStartup || _pendingUpdateFailureStatus is not null))
                ShowMainWindow();
            else
            {
                try { _residentStartupFeedback = new ResidentStartupFeedback("已最小化启动", _themeManager.ResolvedTheme); }
                catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or
                    ArgumentException or System.Runtime.InteropServices.ExternalException)
                {
                    // Optional feedback must not prevent tray shortcuts from
                    // starting when a display/font resource is unavailable.
                }
            }
            _ = CheckOcrModelUpgradeAsync();
            _hotKeyManager?.StartInputHooks();
        }));
    }

    private void ResumeResidentInput()
    {
        if (_isShuttingDown) return;
        _residentSelectionCancellation?.Cancel();
        // The worker flushes settings before exiting. Reload after a crash too,
        // so the resident shortcuts never revert a successfully saved change.
        ApplyResidentSettings(_captureSettingsStore!.Load().Settings);
        if (_hotKeyManager is null)
        {
            _hotKeyManager = new GlobalHotKeyManager(deferLowLevelInputHooks: true);
            _hotKeyManager.HotKeyPressed += OnHotKeyPressed;
            _hotKeyManager.EscapePressed += OnFeatureEscapePressed;
            _pendingStartupStatus = TryApplyInitialHotKeys(_hotKeyManager, _currentSettings);
            _hotKeyManager.StartInputHooks();
        }
        _isCaptureInProgress = false;
        _idleEfficiency?.SetCaptureActive(false);
        _floatingCaptureWindow?.SetCaptureInProgress(false);
        _trayIconService?.UpdatePinnedImageCommands(false, false);
        MemoryFootprint.TrimAfterHeavyOperation();
    }

    private void ReceiveFeatureState(FeatureMessage message)
    {
        switch (message.Kind)
        {
            case "native-cancel": _residentSelectionWindow?.Cancel(); break;
            case "native-presented" when message.NativeSelectionId == _residentSelectionId:
                _residentSelectionPresented?.TrySetResult();
                break;
            case "settings" when message.Settings is { } settings:
                ApplyResidentSettings(settings);
                break;
            case "state":
                _residentLastCaptureRegion = message.LastCaptureRegion;
                _isCaptureInProgress = message.Flag;
                _idleEfficiency?.SetCaptureActive(message.Flag);
                _floatingCaptureWindow?.SetCaptureInProgress(message.Flag);
                _trayIconService?.UpdatePinnedImageCommands(message.Count > 0, false);
                TryShowOcrModelUpgrade();
                break;
            case "section": _lastSettingsSection = message.Section; break;
            case "prepare-capture": _floatingCaptureWindow?.SetCaptureInProgress(true); break;
            case "recording-active": _floatingCaptureWindow?.ShowRecordingAlreadyActiveFeedback(); break;
            case "exit": ExitApplication(); break;
            case "error": _ = StartupFeedbackWindow.ShowAsync(message.Text ?? "操作失败，请重试。"); break;
        }
    }

    private void ApplyResidentSettings(AppSettings settings)
    {
        var previous = _currentSettings;
        _currentSettings = settings;
        UiLocalization.Apply(settings.InterfaceLanguage);
        if (previous.Theme != settings.Theme) _themeManager?.Apply(settings.Theme);
        if (previous.ShowNotificationIcon != settings.ShowNotificationIcon)
            _trayIconService?.SetVisible(settings.ShowNotificationIcon);
        if (previous.ShowFloatingCaptureButton != settings.ShowFloatingCaptureButton)
            UpdateFloatingCaptureWindow();
    }

    private bool ForwardFeature(string command)
    {
        if (_featureHost is null) return false;
        _ = SendFeatureAsync(new("command") { Command = command });
        return true;
    }

    private bool ForwardHotKey(HotKeyAction action, CapturedImage? image, CapturePointerContinuation? continuation)
    {
        if (_featureHost is null) return false;
        _ = SendFeatureAsync(FeatureMessage.ForHotKey(new(action, image, continuation)));
        return true;
    }

    private async Task SendFeatureAsync(FeatureMessage message)
    {
        _idleEfficiency?.Wake();
        var capture = message.HotKey is HotKeyAction.RegionCapture or HotKeyAction.RecognizeText or
            HotKeyAction.PinImage or HotKeyAction.ScrollCapture or HotKeyAction.VideoRecording or HotKeyAction.RealtimeTranslation ||
            message.Command is "repeat" or "all-screens" or "preset";
        var escapeVersion = Volatile.Read(ref _featureEscapeVersion);
        var settings = message.Command == "settings" || message.HotKey == HotKeyAction.OpenSettings;
        if (capture && Interlocked.CompareExchange(ref _featureCapturePending, 1, 0) != 0)
        {
            message.Image?.Dispose();
            message.Image = null;
            return;
        }
        try
        {
            if (_featureHost!.ProcessId is null && CanUseResidentSelection(message))
            {
                await SendResidentSelectionAsync(message, escapeVersion);
                return;
            }
            if (_featureHost!.ProcessId is null)
                await Task.Run(() => _captureSettingsStore?.FlushPendingSave());
            await _featureHost.SendAsync(message with
            {
                Section = _lastSettingsSection,
                Text = settings
                    ? _pendingUpdateFailureStatus ?? _pendingStartupStatus : message.Text,
                Flag = settings ? _pendingUpdateFailureStatus is not null : message.Flag,
            }, capture ? () => Volatile.Read(ref _featureEscapeVersion) != escapeVersion : null);
            if (settings)
            {
                _pendingStartupStatus = null;
                _pendingUpdateFailureStatus = null;
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or
            OperationCanceledException or TimeoutException or System.ComponentModel.Win32Exception)
        {
            message.Image?.Dispose();
            message.Image = null;
            if (!_isShuttingDown)
                await StartupFeedbackWindow.ShowAsync($"无法打开功能，请重试：{exception.Message}");
        }
        finally { if (capture) Volatile.Write(ref _featureCapturePending, 0); }
    }

    private void StartFeatureWorker(string pipeName)
    {
        _featureWorker = new FeatureProcessWorker(Dispatcher, IsFeatureWorkerBusy,
            ReceiveFeatureCommandAsync, () => _ = RetireFeatureWorkerAsync(),
            () => _ = StopDisconnectedFeatureWorkerAsync());
        _ = _featureWorker.ConnectAsync(_featureStartup ?? FeatureProcessWorker.PrepareAsync(pipeName));
    }

    private async Task ShowFeedbackWithoutBlockingMouseAsync(string text)
    {
        using var presentation = _hotKeyManager?.SuspendMouseForWindowPresentation();
        await StartupFeedbackWindow.ShowAsync(text);
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
    }

    private bool IsFeatureWorkerBusy() => _isCaptureInProgress ||
        _waitingForResidentSelection ||
        _regionCaptureCoordinator?.IsRecordingInProgress == true ||
        _mainWindow is not null || _pinnedImageManager is { Count: > 0 } ||
        _isCaptureHistoryRestoreStarted || _interactiveHotKeyPending != 0 ||
        Windows.OfType<Window>().Any(window => window.IsVisible);

    private async Task ReceiveFeatureCommandAsync(FeatureMessage message)
    {
        if (message.Kind == "bootstrap")
        {
            CaptureTimingDiagnostics.Mark("worker-bootstrap-start", $"action={message.HotKey} prepare={message.Flag}");
            if (message.Settings is { } initialSettings) _currentSettings = initialSettings;
            // Prepare the visual tree without showing a window. Actual
            // presentation happens once, after the final selection is known.
            if (message.Flag)
            {
                ApplyWorkerUiPreferences();
                CaptureTimingDiagnostics.UiContext("worker-bootstrap-theme-ready",
                    $"theme={_currentSettings.Theme} toolbarScale={_currentSettings.ToolbarScalePercent}");
                PrepareResidentToolbar(message.HotKey);
            }
            CaptureTimingDiagnostics.Mark("worker-bootstrap-ready");
            return;
        }
        if (message.Kind == "activate")
        {
            using var activationTiming = CaptureTimingDiagnostics.Begin("worker-activate");
            _waitingForResidentSelection = message.Flag;
            if (message.Settings is { } settings) _currentSettings = settings;
            ApplyWorkerUiPreferences();
            _captureHistoryService!.ConfigureRetentionPolicy(
                _currentSettings.ScreenshotHistoryRetentionDays, _currentSettings.HistoryLimit);
            if (_regionCaptureCoordinator is { } coordinator)
                coordinator.LastOrdinaryCaptureRegion = message.LastCaptureRegion;
            _pendingStartupStatus = TryApplyInitialHotKeys(_hotKeyManager!, _currentSettings);
            // The resident HWND still owns selection input. A low-level hook
            // on this dispatcher makes Windows wait while the cold toolbar is
            // built, stalling the resident's otherwise independent drag loop.
            // The first command installs hooks after startup/theme work has
            // drained. RegisterHotKey already handles keyboard activation.
            await _featureWorker!.SendAsync(new("started"));
            _ = BeginDeferredStartupInfrastructureAsync();
            return;
        }
        if (message.Kind == "prepare-capture-toolbar")
        {
            using var preparationTiming = CaptureTimingDiagnostics.Begin("worker-prepare-toolbar");
            if (_waitingForResidentSelection) PrepareResidentToolbar(message.HotKey);
            return;
        }
        if (message.Kind == "prepare-capture-frame")
        {
            CaptureTimingDiagnostics.Mark("worker-prepared-frame-ready", $"selection={message.NativeSelectionId}");
            var image = message.Image;
            message.Image = null;
            if (_waitingForResidentSelection && image is not null)
                _regionCaptureCoordinator!.PrepareResidentFrame(message.NativeSelectionId, image);
            else image?.Dispose();
            return;
        }
        if (message.Kind != "command") return;
        _waitingForResidentSelection = false;
        if (message.NativeSelectionId == 0 || message.HotKey != HotKeyAction.RegionCapture)
            _regionCaptureCoordinator?.DiscardPreparedCaptureToolbar();
        if (message.NativeSelectionId == 0 || message.HotKey != HotKeyAction.VideoRecording)
            _regionCaptureCoordinator?.DiscardPreparedRecordingToolbar();
        _hotKeyManager!.StartInputHooks();
        CaptureTimingDiagnostics.Mark("worker-command-input-ready", $"selection={message.NativeSelectionId} action={message.HotKey}");
        if (message.Command == "cancel-native")
        {
            if (_activeResidentSelectionId == message.NativeSelectionId)
                CaptureOverlayWindow.CloseActiveInteractiveSelectionForTransition();
            return;
        }
        if (message.NativeSelectionId != 0 && message.InitialSelection is { } selected)
        {
            var nativeImage = message.Image ?? _regionCaptureCoordinator!.TakePreparedResidentFrame(message.NativeSelectionId);
            message.Image = null;
            _activeResidentSelectionId = message.NativeSelectionId;
            if (message.HotKey == HotKeyAction.RealtimeTranslation)
            {
                nativeImage?.Dispose();
                await _featureWorker!.PrepareResidentCaptureAsync(message.NativeSelectionId);
                await ToggleRealtimeTranslationAsync(0, selected);
                PublishFeatureState();
                return;
            }
            if (message.HotKey == HotKeyAction.VideoRecording)
            {
                nativeImage?.Dispose();
                await _regionCaptureCoordinator!.RequestVideoRecordingAsync(initialSelection: selected,
                    selectionPresented: () => _featureWorker!.PrepareResidentCaptureAsync(message.NativeSelectionId));
                return;
            }
            if (nativeImage is null) throw new InvalidDataException("缺少框选时的原始图像。");
            if (message.HotKey == HotKeyAction.PinImage)
            {
                await _regionCaptureCoordinator!.RequestPinCaptureAsync(nativeImage, initialSelection: selected,
                    selectionPresented: () => _featureWorker!.PrepareResidentCaptureAsync(message.NativeSelectionId));
                return;
            }
            await _regionCaptureCoordinator!.RequestCaptureAsync(nativeImage, initialSelection: selected,
                capturePresented: () => _ = _featureWorker!.SendAsync(new("native-presented")
                    { NativeSelectionId = message.NativeSelectionId }),
                residentSelectionWindow: new IntPtr(message.NativeSelectionWindow));
            return;
        }
        _lastSettingsSection = message.Section;
        if (message.Command == "settings" || message.HotKey == HotKeyAction.OpenSettings)
        {
            if (message.Flag) _pendingUpdateFailureStatus = message.Text;
            else _pendingStartupStatus = message.Text ?? _pendingStartupStatus;
        }
        if (message.Command == "realtime" || message.HotKey == HotKeyAction.RealtimeTranslation)
        {
            _ = ToggleRealtimeTranslationAsync(new nint(message.TargetWindow), message.InitialSelection);
            return;
        }
        if (message.HotKey is { } action)
        {
            CapturePointerContinuation? continuation = null;
            if (message.Button is { } button)
            {
                continuation = new(button,
                    message.PointerX is { } x && message.PointerY is { } y ? new System.Drawing.Point(x, y) : null,
                    message.EnterPickerOnRelease);
                _hotKeyManager!.AdoptPointerContinuation(continuation);
            }
            var image = message.Image;
            message.Image = null;
            var args = new HotKeyPressedEventArgs(action, image, continuation);
            try { OnHotKeyPressed(this, args); }
            finally { args.DisposeUnusedPreCapturedScreen(); }
            return;
        }
        switch (message.Command)
        {
            case "startup-feedback":
                if (!_isCaptureInProgress) await ShowFeedbackWithoutBlockingMouseAsync("已最小化启动");
                break;
            case "color-copied":
                await ShowFeedbackWithoutBlockingMouseAsync($"已复制 {message.Text}");
                break;
            case "settings":
                ShowMainWindow();
                break;
            case "ocr-model-upgrade":
                _pendingOcrModelUpgrade = new(true, message.Text);
                TryShowOcrModelUpgrade();
                break;
            case "repeat": await RequestFloatingCaptureAsync(); break;
            case "all-screens": await RequestAllScreensCaptureAsync(); break;
            case "preset": await RequestPresetCaptureAsync(); break;
            case "history": ShowCaptureHistory(); break;
            case "video-history": ShowCaptureHistory(true); break;
            case "hide-pins": _pinnedImageManager?.HideAll(); break;
            case "hide-floating":
                PersistCapturePreference(current => current with { ShowFloatingCaptureButton = false },
                    window => window.SetFloatingCaptureButtonEnabled(false));
                break;
            case "text": ShowTextTranslationWindow(message.Text, message.Flag); break;
            case "image-question": await ShowEmptyImageQuestionAsync(); break;
            case "restore": PublishFeatureState(); break;
        }
    }

    private void ApplyWorkerUiPreferences()
    {
        _themeManager!.Apply(_currentSettings.Theme);
        UiLocalization.Apply(_currentSettings.InterfaceLanguage);
        TranslationTargetPreferences.Configure(_currentSettings.TranslationTargetLanguage, language =>
            PersistCapturePreference(current => current with { TranslationTargetLanguage = language },
                window => window.SaveTranslationTargetLanguage(language)));
        AnnotationToolPreferences.Configure(_currentSettings.AnnotationToolSettings,
            preferences => PersistCapturePreference(
                current => current with { AnnotationToolSettings = preferences.ToArray() },
                window => window.SaveAnnotationToolSettings(preferences)));
    }

    private void PrepareResidentToolbar(HotKeyAction? action)
    {
        if (action == HotKeyAction.VideoRecording) _regionCaptureCoordinator!.PrepareResidentRecordingToolbar();
        else if (action is null or HotKeyAction.RegionCapture) _regionCaptureCoordinator!.PrepareResidentCaptureToolbar();
    }

    private void PublishFeatureSettings()
    {
        if (_featureWorker is not null)
            _ = _featureWorker.SendAsync(new("settings") { Settings = _currentSettings });
    }

    private void ShowRecordingAlreadyActiveFeedback()
    {
        if (_featureWorker is not null) _ = _featureWorker.SendAsync(new("recording-active"));
        else _floatingCaptureWindow?.ShowRecordingAlreadyActiveFeedback();
    }

    private void PublishFeatureState()
    {
        if (_featureWorker is null) return;
        TryShowOcrModelUpgrade();
        if (!_isCaptureInProgress) _activeResidentSelectionId = 0;
        _ = _featureWorker.SendAsync(new("state")
        {
            Flag = _isCaptureInProgress,
            Count = _pinnedImageManager?.Count ?? 0,
            LastCaptureRegion = _regionCaptureCoordinator?.LastOrdinaryCaptureRegion,
        });
        _featureWorker.ScheduleIdle();
    }

    private async Task RetireFeatureWorkerAsync()
    {
        if (_retiringFeatureWorker) return;
        _retiringFeatureWorker = true;
        _regionCaptureCoordinator?.DiscardPreparedCaptureToolbar();
        _regionCaptureCoordinator?.DiscardPreparedRecordingToolbar();
        // A closed feature process is the release boundary for WPF/WinRT and
        // native allocator caches. No working-set trimming or process restart.
        DisposeHotKeyManager();
        await Task.Run(() => _captureSettingsStore?.FlushPendingSave());
        ExitApplication();
    }

    private async Task ExitFeatureAndResidentAsync()
    {
        _retiringFeatureWorker = true;
        await _featureWorker!.SendAsync(new("exit"));
        await StopFeatureOperationsAsync();
    }

    private async Task StopDisconnectedFeatureWorkerAsync()
    {
        if (_retiringFeatureWorker || _isShuttingDown) return;
        _retiringFeatureWorker = true;
        await StopFeatureOperationsAsync();
    }

    private async Task StopFeatureOperationsAsync()
    {
        _regionCaptureCoordinator?.DiscardPreparedCaptureToolbar();
        _regionCaptureCoordinator?.DiscardPreparedRecordingToolbar();
        DisposeHotKeyManager();
        _realtimeTranslationWindow?.Close();
        _mainWindow?.RequestExit();
        DisposePinnedImageManager();
        CaptureOverlayWindow.CloseActiveInteractiveSelectionForTransition();
        foreach (var controls in Windows.OfType<VideoRecordingControlWindow>().ToArray()) controls.Close();
        while (_regionCaptureCoordinator?.IsRecordingInProgress == true) await Task.Delay(50);
        foreach (var window in Windows.OfType<Window>().ToArray())
        {
            // The recorder owns finalization and closes itself afterwards.
            if (window is not VideoRecordingControlWindow) window.Close();
        }
        while (FeatureActivity.Count > 0 || _regionCaptureCoordinator?.IsRecordingInProgress == true)
            await Task.Delay(50);
        await Task.Run(() => _captureSettingsStore?.FlushPendingSave());
        ExitApplication();
    }
}
