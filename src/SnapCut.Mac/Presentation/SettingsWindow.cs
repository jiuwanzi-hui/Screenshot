using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using SnapCut.Mac.App;
using SnapCut.Mac.Capture;
using SnapCut.Mac.Input;
using SnapCut.Mac.Native;

namespace SnapCut.Mac.Presentation;

internal sealed class SettingsWindow : Window
{
    private static readonly MacHotkeyGesture[] HotkeyChoices =
    [
        MacHotkeyGesture.CaptureDefault,
        MacHotkeyGesture.ScrollDefault,
        new(2, MacHotkeyModifiers.Command | MacHotkeyModifiers.Shift, "⌘⇧D"),
        new(3, MacHotkeyModifiers.Command | MacHotkeyModifiers.Shift, "⌘⇧F"),
        new(7, MacHotkeyModifiers.Command | MacHotkeyModifiers.Shift, "⌘⇧X"),
        new(0, MacHotkeyModifiers.Control | MacHotkeyModifiers.Shift, "⌃⇧A"),
        new(1, MacHotkeyModifiers.Control | MacHotkeyModifiers.Shift, "⌃⇧S"),
        new(0, MacHotkeyModifiers.Command | MacHotkeyModifiers.Option, "⌘⌥A"),
        new(1, MacHotkeyModifiers.Command | MacHotkeyModifiers.Option, "⌘⌥S"),
    ];

    private readonly TextBlock _screenPermission;
    private readonly TextBlock _inputPermission;
    private readonly TextBlock _hotkeyStatus;
    private readonly ComboBox _captureHotkey;
    private readonly ComboBox _scrollHotkey;
    private readonly CheckBox _showPreview;
    private readonly StackPanel _historyItems;
    private readonly Action<MacSettings> _save;
    private MacSettings _settings;

    public SettingsWindow(
        MacSettings settings,
        CaptureHistoryStore history,
        bool hotkeysRunning,
        Action normalCapture,
        Action scrollCapture,
        Action<MacSettings> save)
    {
        _settings = settings;
        _save = save;
        Title = "SnapCut 设置";
        Width = 720;
        Height = 680;
        MinWidth = 640;
        MinHeight = 560;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush(MacTheme.WindowBackground);

        var normal = MacTheme.CreateButton("区域截图", primary: true);
        normal.Click += (_, _) => normalCapture();
        var scroll = MacTheme.CreateButton("长截图");
        scroll.Click += (_, _) => scrollCapture();
        var quickActions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children =
            {
                normal,
                scroll,
            },
        };

        _screenPermission = StatusText();
        _inputPermission = StatusText();
        var requestScreen = MacTheme.CreateButton("申请屏幕录制权限");
        requestScreen.Click += (_, _) =>
        {
            MacScreenCaptureService.RequestScreenCaptureAccess();
            RefreshPermissionStatus();
        };
        var requestInput = MacTheme.CreateButton("申请输入监控权限");
        requestInput.Click += (_, _) =>
        {
            MacGlobalHotkeyService.RequestInputMonitoringAccess();
            RefreshPermissionStatus();
        };

        _captureHotkey = CreateHotkeyCombo(settings.CaptureHotkey);
        _scrollHotkey = CreateHotkeyCombo(settings.ScrollHotkey);
        _showPreview = new CheckBox
        {
            Content = "截图后打开预览窗口",
            IsChecked = settings.ShowPreviewAfterCapture,
        };
        _hotkeyStatus = StatusText();
        _hotkeyStatus.Text = hotkeysRunning
            ? "全局快捷键正在监听"
            : "全局快捷键不可用，请授予输入监控权限后重启";
        var saveSettings = MacTheme.CreateButton("保存设置", primary: true);
        saveSettings.Click += (_, _) => SaveSettings();

        _historyItems = new StackPanel { Spacing = 6 };
        RefreshHistory(history);
        var openHistory = MacTheme.CreateButton("打开截图文件夹");
        openHistory.Click += (_, _) =>
        {
            Directory.CreateDirectory(history.HistoryDirectory);
            MacNativeUi.OpenPath(history.HistoryDirectory);
        };

        var content = new StackPanel
        {
            Margin = new Thickness(28),
            Spacing = 18,
            Children =
            {
                Header("SnapCut for macOS", "菜单栏截图、长截图与截图历史"),
                MacTheme.CreatePanel(new StackPanel
                {
                    Spacing = 12,
                    Children =
                    {
                        SectionTitle("快捷操作"),
                        quickActions,
                    },
                }),
                MacTheme.CreatePanel(new StackPanel
                {
                    Spacing = 10,
                    Children =
                    {
                        SectionTitle("系统权限"),
                        _screenPermission,
                        requestScreen,
                        _inputPermission,
                        requestInput,
                    },
                }),
                MacTheme.CreatePanel(new StackPanel
                {
                    Spacing = 10,
                    Children =
                    {
                        SectionTitle("快捷键与行为"),
                        LabeledControl("区域截图", _captureHotkey),
                        LabeledControl("长截图", _scrollHotkey),
                        _showPreview,
                        _hotkeyStatus,
                        saveSettings,
                    },
                }),
                MacTheme.CreatePanel(new StackPanel
                {
                    Spacing = 10,
                    Children =
                    {
                        SectionTitle("最近截图"),
                        _historyItems,
                        openHistory,
                    },
                }),
            },
        };
        Content = new ScrollViewer
        {
            Content = content,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
        };
        Opened += (_, _) =>
        {
            MacNativeUi.ExcludeFromScreenCapture(this);
            RefreshPermissionStatus();
        };
    }

    private void SaveSettings()
    {
        var capture = (MacHotkeyGesture?)_captureHotkey.SelectedItem
            ?? MacHotkeyGesture.CaptureDefault;
        var scroll = (MacHotkeyGesture?)_scrollHotkey.SelectedItem
            ?? MacHotkeyGesture.ScrollDefault;
        if (capture == scroll)
        {
            _hotkeyStatus.Text = "两个功能不能使用同一个快捷键";
            return;
        }

        _settings.CaptureHotkey = capture;
        _settings.ScrollHotkey = scroll;
        _settings.ShowPreviewAfterCapture = _showPreview.IsChecked != false;
        _save(_settings);
        _hotkeyStatus.Text = "设置已保存并立即生效";
    }

    private void RefreshPermissionStatus()
    {
        _screenPermission.Text = MacScreenCaptureService.HasScreenCaptureAccess()
            ? "屏幕录制：可用"
            : "屏幕录制：未授权，无法读取屏幕内容";
        _inputPermission.Text = MacGlobalHotkeyService.HasInputMonitoringAccess()
            ? "输入监控：可用"
            : "输入监控：未授权，全局快捷键和滚轮方向提示不可用";
    }

    private void RefreshHistory(CaptureHistoryStore history)
    {
        _historyItems.Children.Clear();
        foreach (var path in history.GetRecent(8))
        {
            var button = MacTheme.CreateButton(Path.GetFileName(path));
            button.HorizontalContentAlignment = HorizontalAlignment.Left;
            button.HorizontalAlignment = HorizontalAlignment.Stretch;
            button.Click += (_, _) => MacNativeUi.OpenPath(path);
            _historyItems.Children.Add(button);
        }

        if (_historyItems.Children.Count == 0)
        {
            _historyItems.Children.Add(new TextBlock
            {
                Text = "还没有截图记录",
                Foreground = new SolidColorBrush(MacTheme.SecondaryText),
            });
        }
    }

    private static ComboBox CreateHotkeyCombo(MacHotkeyGesture selected)
    {
        return new ComboBox
        {
            ItemsSource = HotkeyChoices,
            SelectedItem = HotkeyChoices.FirstOrDefault(choice => choice == selected)
                ?? selected,
            MinWidth = 140,
        };
    }

    private static Grid LabeledControl(string label, Control control)
    {
        return new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Children =
            {
                new TextBlock
                {
                    Text = label,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = new SolidColorBrush(MacTheme.PrimaryText),
                },
                control,
            },
        }.Also(grid => Grid.SetColumn(control, 1));
    }

    private static TextBlock StatusText()
    {
        return new TextBlock
        {
            Foreground = new SolidColorBrush(MacTheme.SecondaryText),
            TextWrapping = TextWrapping.Wrap,
        };
    }

    private static TextBlock SectionTitle(string text)
    {
        return new TextBlock
        {
            Text = text,
            FontSize = 15,
            FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(MacTheme.PrimaryText),
        };
    }

    private static StackPanel Header(string title, string subtitle)
    {
        return new StackPanel
        {
            Spacing = 4,
            Children =
            {
                new TextBlock
                {
                    Text = title,
                    FontSize = 26,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = new SolidColorBrush(MacTheme.PrimaryText),
                },
                new TextBlock
                {
                    Text = subtitle,
                    Foreground = new SolidColorBrush(MacTheme.SecondaryText),
                },
            },
        };
    }
}

internal static class ControlExtensions
{
    public static T Also<T>(this T value, Action<T> action)
    {
        action(value);
        return value;
    }
}
