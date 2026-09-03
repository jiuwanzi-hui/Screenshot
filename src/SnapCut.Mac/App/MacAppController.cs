using Avalonia.Controls;
using Avalonia;
using Avalonia.Input;
using Avalonia.Platform;
using Avalonia.Threading;
using SnapCut.Mac.Input;
using SnapCut.Mac.Presentation;
using SnapCut.Mac.Text;
using SnapCut.Mac.Pin;
using SnapCut.Mac.Editor;

namespace SnapCut.Mac.App;

internal sealed class MacAppController : IDisposable
{
    private readonly MacSettingsStore _settingsStore = new();
    private readonly CaptureHistoryStore _history;
    private readonly MacGlobalHotkeyService _hotkeys = new();
    private readonly MacOcrModelManager _ocrModels = new();
    private readonly MacOcrService _ocr;
    private readonly MacTranslationService _translation;
    private readonly MacPinnedImageManager _pins = new();
    private readonly NativeMenuItem _showPinsMenuItem;
    private readonly NativeMenuItem _hidePinsMenuItem;
    private readonly NativeMenuItem _groupPinsMenuItem;
    private readonly CaptureCoordinator _capture;
    private readonly TrayIcon _trayIcon;
    private FloatingCaptureButtonWindow? _floatingButton;
    private readonly NativeMenuItem _normalMenuItem;
    private readonly NativeMenuItem _scrollMenuItem;
    private MacSettings _settings;
    private string _appliedTheme;
    private SettingsWindow? _settingsWindow;
    private bool _disposed;

    public MacAppController(Action shutdown)
    {
        _settings = _settingsStore.Load();
        _appliedTheme = _settings.Theme;
        _history = new CaptureHistoryStore(_settings.SaveDirectory);
        MacTheme.Apply(_settings.Theme);
        _ocr = new MacOcrService(_ocrModels);
        _translation = new MacTranslationService(
            () => _settings,
            new MacKeychainCredentialStore());
        _capture = new CaptureCoordinator(
            _history,
            () => _settings,
            SaveSettings,
            _ocr,
            _translation,
            _pins);
        _capture.CaptureCompleted += _ => RefreshSettingsWindow();
        _hotkeys.Update(_settings);
        _hotkeys.Pressed += action => Dispatcher.UIThread.Post(
            () => HandleHotkey(action));
        var hotkeysRunning = _hotkeys.TryStart();

        _normalMenuItem = new NativeMenuItem("区域截图")
        {
            Gesture = ToKeyGesture(_settings.CaptureHotkey),
        };
        _normalMenuItem.Click += (_, _) => StartCapture(scrollCapture: false);
        _scrollMenuItem = new NativeMenuItem("长截图")
        {
            Gesture = ToKeyGesture(_settings.ScrollHotkey),
        };
        _scrollMenuItem.Click += (_, _) => StartCapture(scrollCapture: true);
        var settings = new NativeMenuItem("设置与历史…");
        settings.Click += (_, _) => ShowSettings();
        var allScreens = new NativeMenuItem("全部显示器截图");
        allScreens.Click += (_, _) => _ = _capture.CaptureAllDisplaysAsync();
        _showPinsMenuItem = new NativeMenuItem("显示全部钉图");
        _showPinsMenuItem.Click += (_, _) => _pins.ShowAll();
        _hidePinsMenuItem = new NativeMenuItem("隐藏全部钉图");
        _hidePinsMenuItem.Click += (_, _) => _pins.HideAll();
        _groupPinsMenuItem = new NativeMenuItem("组合可见钉图");
        _groupPinsMenuItem.Click += (_, _) => _pins.GroupVisible();
        _pins.Changed += UpdatePinMenuVisibility;
        var quit = new NativeMenuItem("退出 SnapCut");
        quit.Click += (_, _) => shutdown();
        var menu = new NativeMenu
        {
            Items =
            {
                _normalMenuItem,
                _scrollMenuItem,
                allScreens,
                new NativeMenuItemSeparator(),
                settings,
                _showPinsMenuItem,
                _hidePinsMenuItem,
                _groupPinsMenuItem,
                new NativeMenuItemSeparator(),
                quit,
            },
        };
        _trayIcon = new TrayIcon
        {
            ToolTipText = hotkeysRunning
                ? "SnapCut"
                : "SnapCut · 需要输入监控权限",
            Menu = menu,
            IsVisible = _settings.ShowNotificationIcon,
        };
        using var iconStream = AssetLoader.Open(
            new Uri("avares://snapcut/Assets/Screenshot.png"));
        _trayIcon.Icon = new WindowIcon(iconStream);
        _trayIcon.Clicked += (_, _) => ShowSettings();
        UpdateFloatingButton();
        _pins.Restore();
        UpdatePinMenuVisibility();
    }

    public void ShowSettings()
    {
        if (_settingsWindow is not null)
        {
            if (!_settingsWindow.IsVisible)
            {
                _settingsWindow.Show();
            }

            _settingsWindow.Activate();
            return;
        }

        _settingsWindow = new SettingsWindow(
            _settings,
            _history,
            _hotkeys.IsRunning,
            () => StartCapture(scrollCapture: false),
            () => StartCapture(scrollCapture: true),
            _ocrModels,
            new MacKeychainCredentialStore(),
            SaveSettings);
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    private void HandleHotkey(MacHotkeyAction action)
    {
        switch (action)
        {
            case MacHotkeyAction.OpenSettings:
                ShowSettings();
                return;
            case MacHotkeyAction.ScrollCapture:
                StartCapture(scrollCapture: true);
                return;
            case MacHotkeyAction.VideoRecording:
                StartCapture(false, MacCaptureAction.VideoRecording);
                return;
            case MacHotkeyAction.RecognizeText:
                StartCapture(false, MacCaptureAction.RecognizeText);
                return;
            case MacHotkeyAction.Translation:
                StartCapture(false, MacCaptureAction.Translation);
                return;
            case MacHotkeyAction.PinImage:
                StartCapture(false, MacCaptureAction.PinImage);
                return;
            default:
                StartCapture(scrollCapture: false);
                return;
        }
    }

    private void StartCapture(
        bool scrollCapture,
        MacCaptureAction action = MacCaptureAction.Complete)
    {
        if (_capture.IsBusy)
        {
            return;
        }

        var settingsWindow = _settingsWindow;
        var restoreSettings = settingsWindow?.IsVisible == true;
        settingsWindow?.Hide();
        _ = RunCaptureAsync(scrollCapture, action, settingsWindow, restoreSettings);
    }

    private async Task RunCaptureAsync(
        bool scrollCapture,
        MacCaptureAction action,
        SettingsWindow? settingsWindow,
        bool restoreSettings)
    {
        try
        {
            await _capture.StartAsync(scrollCapture, action);
        }
        finally
        {
            if (restoreSettings &&
                ReferenceEquals(_settingsWindow, settingsWindow) &&
                settingsWindow is { IsVisible: false })
            {
                settingsWindow.Show();
                settingsWindow.Activate();
            }
        }
    }

    private void SaveSettings(MacSettings settings)
    {
        var themeChanged = !string.Equals(
            _appliedTheme,
            settings.Theme,
            StringComparison.Ordinal);
        if (string.IsNullOrWhiteSpace(settings.SaveDirectory))
        {
            settings.SaveDirectory = MacSettings.DefaultSaveDirectory();
        }

        _settings = settings;
        _settingsStore.Save(settings);
        _history.UpdateDirectory(settings.SaveDirectory);
        MacTheme.Apply(settings.Theme);
        _appliedTheme = settings.Theme;
        MacLaunchAtStartupService.Apply(settings.LaunchAtStartup);
        _trayIcon.IsVisible = settings.ShowNotificationIcon;
        UpdateFloatingButton();
        _hotkeys.Update(settings);
        _normalMenuItem.Gesture = ToKeyGesture(settings.CaptureHotkey);
        _scrollMenuItem.Gesture = ToKeyGesture(settings.ScrollHotkey);

        if (themeChanged && _settingsWindow is not null)
        {
            var previousWindow = _settingsWindow;
            _settingsWindow = null;
            previousWindow.Close();
            Dispatcher.UIThread.Post(ShowSettings);
        }
    }

    private void RefreshSettingsWindow()
    {
        if (_settingsWindow is null)
        {
            return;
        }

        _settingsWindow.Close();
        _settingsWindow = null;
    }

    private void UpdatePinMenuVisibility()
    {
        _showPinsMenuItem.IsVisible = _pins.HasHidden;
        _hidePinsMenuItem.IsVisible = _pins.HasVisible;
        _groupPinsMenuItem.IsVisible = _pins.CanGroup;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _trayIcon.IsVisible = false;
        _trayIcon.Dispose();
        _floatingButton?.Close();
        _floatingButton = null;
        _hotkeys.Dispose();
        _ocrModels.Dispose();
        _translation.Dispose();
        _settingsWindow?.Close();
    }

    private void UpdateFloatingButton()
    {
        if (!_settings.ShowFloatingCaptureButton)
        {
            _floatingButton?.Close();
            _floatingButton = null;
            return;
        }

        if (_floatingButton is not null)
        {
            return;
        }

        _floatingButton = new FloatingCaptureButtonWindow(
            () => RunFloatingAction(_settings.FloatingCaptureClickBehavior),
            RunFloatingAction,
            () =>
            {
                _settings.ShowFloatingCaptureButton = false;
                SaveSettings(_settings);
            });
        _floatingButton.Position = new PixelPoint(40, 180);
        _floatingButton.Show();
    }

    private void RunFloatingAction(string action)
    {
        switch (action)
        {
            case "Region":
            case "CaptureImmediately":
            case "ShowSelection":
                StartCapture(scrollCapture: false);
                break;
            case "Scroll":
            case "ScrollCapture":
                StartCapture(scrollCapture: true);
                break;
            case "Video":
            case "VideoRecording":
                StartCapture(false, MacCaptureAction.VideoRecording);
                break;
            case "Pin":
            case "PinCapture":
                StartCapture(false, MacCaptureAction.PinImage);
                break;
            case "AllScreens":
            case "CaptureAllScreens":
                _ = _capture.CaptureAllDisplaysAsync();
                break;
            case "Settings":
                ShowSettings();
                break;
        }
    }

    private static KeyGesture ToKeyGesture(MacHotkeyGesture gesture)
    {
        var key = gesture.KeyCode switch
        {
            0 => Key.A,
            1 => Key.S,
            2 => Key.D,
            3 => Key.F,
            7 => Key.X,
            15 => Key.R,
            17 => Key.T,
            31 => Key.O,
            35 => Key.P,
            43 => Key.OemComma,
            _ => Key.None,
        };
        var modifiers = KeyModifiers.None;
        if (gesture.Modifiers.HasFlag(MacHotkeyModifiers.Command))
        {
            modifiers |= KeyModifiers.Meta;
        }

        if (gesture.Modifiers.HasFlag(MacHotkeyModifiers.Shift))
        {
            modifiers |= KeyModifiers.Shift;
        }

        if (gesture.Modifiers.HasFlag(MacHotkeyModifiers.Control))
        {
            modifiers |= KeyModifiers.Control;
        }

        if (gesture.Modifiers.HasFlag(MacHotkeyModifiers.Option))
        {
            modifiers |= KeyModifiers.Alt;
        }

        return new KeyGesture(key, modifiers);
    }
}
