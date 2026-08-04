using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform;
using Avalonia.Threading;
using SnapCut.Mac.Input;
using SnapCut.Mac.Presentation;

namespace SnapCut.Mac.App;

internal sealed class MacAppController : IDisposable
{
    private readonly MacSettingsStore _settingsStore = new();
    private readonly CaptureHistoryStore _history = new();
    private readonly MacGlobalHotkeyService _hotkeys = new();
    private readonly CaptureCoordinator _capture;
    private readonly TrayIcon _trayIcon;
    private readonly NativeMenuItem _normalMenuItem;
    private readonly NativeMenuItem _scrollMenuItem;
    private MacSettings _settings;
    private SettingsWindow? _settingsWindow;
    private bool _disposed;

    public MacAppController(Action shutdown)
    {
        _settings = _settingsStore.Load();
        _capture = new CaptureCoordinator(_history, () => _settings);
        _capture.CaptureCompleted += _ => RefreshSettingsWindow();
        _hotkeys.Update(_settings);
        _hotkeys.Pressed += action => Dispatcher.UIThread.Post(
            () => StartCapture(action == MacHotkeyAction.ScrollCapture));
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
        var quit = new NativeMenuItem("退出 SnapCut");
        quit.Click += (_, _) => shutdown();
        var menu = new NativeMenu
        {
            Items =
            {
                _normalMenuItem,
                _scrollMenuItem,
                new NativeMenuItemSeparator(),
                settings,
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
            IsVisible = true,
        };
        using var iconStream = AssetLoader.Open(
            new Uri("avares://snapcut/Assets/Screenshot.png"));
        _trayIcon.Icon = new WindowIcon(iconStream);
        _trayIcon.Clicked += (_, _) => ShowSettings();
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
            SaveSettings);
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    private void StartCapture(bool scrollCapture)
    {
        if (_capture.IsBusy)
        {
            return;
        }

        var settingsWindow = _settingsWindow;
        var restoreSettings = settingsWindow?.IsVisible == true;
        settingsWindow?.Hide();
        _ = RunCaptureAsync(scrollCapture, settingsWindow, restoreSettings);
    }

    private async Task RunCaptureAsync(
        bool scrollCapture,
        SettingsWindow? settingsWindow,
        bool restoreSettings)
    {
        try
        {
            await _capture.StartAsync(scrollCapture);
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
        _settings = settings;
        _settingsStore.Save(settings);
        _hotkeys.Update(settings);
        _normalMenuItem.Gesture = ToKeyGesture(settings.CaptureHotkey);
        _scrollMenuItem.Gesture = ToKeyGesture(settings.ScrollHotkey);
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

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _trayIcon.IsVisible = false;
        _trayIcon.Dispose();
        _hotkeys.Dispose();
        _settingsWindow?.Close();
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
