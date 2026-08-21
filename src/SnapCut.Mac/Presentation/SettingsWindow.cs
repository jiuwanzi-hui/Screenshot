using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using SnapCut.Mac.App;
using SnapCut.Mac.Capture;
using SnapCut.Mac.Input;
using SnapCut.Mac.Native;
using SnapCut.Mac.Text;
using SnapCut.Mac.Update;
using System.Runtime.InteropServices;

namespace SnapCut.Mac.Presentation;

internal sealed record MacSettingOption(string Id, string Label)
{
    public override string ToString() => Label;
}

internal sealed class SettingsWindow : Window
{
    private static readonly MacHotkeyGesture[] HotkeyChoices =
    [
        MacHotkeyGesture.CaptureDefault,
        MacHotkeyGesture.ScrollDefault,
        new(2, MacHotkeyModifiers.Command | MacHotkeyModifiers.Shift, "⌘⇧D"),
        new(3, MacHotkeyModifiers.Command | MacHotkeyModifiers.Shift, "⌘⇧F"),
        new(7, MacHotkeyModifiers.Command | MacHotkeyModifiers.Shift, "⌘⇧X"),
        MacHotkeyGesture.RecordingDefault,
        MacHotkeyGesture.OcrDefault,
        MacHotkeyGesture.TranslationDefault,
        MacHotkeyGesture.PinDefault,
        MacHotkeyGesture.SettingsDefault,
        new(0, MacHotkeyModifiers.Control | MacHotkeyModifiers.Shift, "⌃⇧A"),
        new(1, MacHotkeyModifiers.Control | MacHotkeyModifiers.Shift, "⌃⇧S"),
        new(0, MacHotkeyModifiers.Command | MacHotkeyModifiers.Option, "⌘⌥A"),
        new(1, MacHotkeyModifiers.Command | MacHotkeyModifiers.Option, "⌘⌥S"),
    ];

    private readonly TextBlock _screenPermission;
    private readonly TextBlock _inputPermission;
    private readonly TextBlock _accessibilityPermission;
    private readonly TextBlock _logText;
    private readonly ComboBox _captureHotkey;
    private readonly ComboBox _scrollHotkey;
    private readonly ComboBox _recordingHotkey;
    private readonly ComboBox _ocrHotkey;
    private readonly ComboBox _translationHotkey;
    private readonly ComboBox _pinHotkey;
    private readonly ComboBox _settingsHotkey;
    private readonly NumericUpDown _historyLimit;
    private readonly TextBox _saveDirectory;
    private readonly TextBox _videoSaveDirectory;
    private readonly CheckBox _keepHistory;
    private readonly CheckBox _persistHistory;
    private readonly CheckBox _showNotificationIcon;
    private readonly CheckBox _showFloatingCaptureButton;
    private readonly ComboBox _floatingCaptureBehavior;
    private readonly ComboBox _closeBehavior;
    private readonly CheckBox _showPreview;
    private readonly StackPanel _historyItems;
    private readonly ContentControl _sectionHost;
    private readonly List<Button> _navigationButtons = [];
    private readonly Control[] _sections;
    private readonly Action<MacSettings> _save;
    private readonly MacOcrModelManager _ocrModels;
    private readonly TextBlock _ocrStatus;
    private readonly Button _ocrInstallButton;
    private readonly TextBox _translationEndpoint;
    private readonly TextBox _translationModel;
    private readonly TextBox _translationTarget;
    private readonly TextBox _translationApiKey;
    private readonly TextBox _offlineTranslationConfig;
    private readonly CheckBox _sendOnlineTranslation;
    private readonly ComboBox _toolbarRows;
    private readonly ListBox _toolbarOrder;
    private readonly Dictionary<string, CheckBox> _toolbarFeatureChecks =
        new(StringComparer.Ordinal);
    private readonly StackPanel _toolbarPreview = new() { Spacing = 4 };
    private readonly ComboBox _videoFormat;
    private readonly ComboBox _videoCodec;
    private readonly ComboBox _videoFrameRate;
    private readonly CheckBox _recordSystemAudio;
    private readonly CheckBox _recordMicrophone;
    private readonly CheckBox _showMouseInput;
    private readonly CheckBox _showKeyboardInput;
    private readonly ComboBox _theme;
    private readonly WrapPanel _themeCards;
    private readonly CheckBox _launchAtStartup;
    private readonly ComboBox _scrollCaptureMode;
    private MacSettings _settings;
    private bool _loading = true;
    private int _toolbarDragIndex = -1;
    private Point _toolbarDragStart;

    private static readonly MacSettingOption[] FloatingCaptureChoices =
    [
        new("CaptureImmediately", "立即截图"),
        new("ShowSelection", "显示选区"),
        new("VideoRecording", "录制视频"),
        new("ScrollCapture", "长截图"),
        new("PinCapture", "钉图"),
        new("CaptureAllScreens", "全部屏幕截图"),
    ];

    private static readonly MacSettingOption[] CloseChoices =
    [
        new("MinimizeToBackground", "关闭后最小化到后台"),
        new("ExitApplication", "关闭并退出应用"),
    ];

    private static readonly Dictionary<string, string> ToolbarLabels =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Shape"] = "矩形 / 椭圆",
            ["Arrow"] = "箭头",
            ["Emoji"] = "表情",
            ["Number"] = "序号",
            ["Brush"] = "画笔",
            ["Text"] = "文字",
            ["Mosaic"] = "马赛克",
            ["VideoRecording"] = "录屏",
            ["Save"] = "保存图片",
            ["ScrollCapture"] = "长截图",
            ["RecognizeText"] = "文字识别",
            ["CopyRecognizedText"] = "识别并复制",
            ["Translation"] = "翻译",
            ["PrivacyRedaction"] = "隐私打码",
            ["PinImage"] = "钉图",
            ["UndoRedo"] = "撤销 / 重做",
            ["QrRecognition"] = "二维码识别",
        };

    public SettingsWindow(
        MacSettings settings,
        CaptureHistoryStore history,
        bool hotkeysRunning,
        Action normalCapture,
        Action scrollCapture,
        MacOcrModelManager ocrModels,
        MacKeychainCredentialStore credentials,
        Action<MacSettings> save)
    {
        _ocrModels = ocrModels;
        _settings = settings;
        _save = save;
        Title = "SnapCut";
        Width = 780;
        Height = 560;
        MinWidth = 680;
        MinHeight = 480;
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
            Children = { normal, scroll },
        };

        _screenPermission = StatusText();
        _inputPermission = StatusText();
        _accessibilityPermission = StatusText();
        var requestScreen = MacTheme.CreateButton("申请屏幕录制权限");
        requestScreen.Click += (_, _) =>
        {
            if (!MacScreenCaptureService.RequestScreenCaptureAccess())
            {
                MacNativeUi.OpenPrivacySettings("Privacy_ScreenCapture");
            }
            RefreshPermissionStatus();
        };
        var requestInput = MacTheme.CreateButton("申请输入监控权限");
        requestInput.Click += (_, _) =>
        {
            if (!MacGlobalHotkeyService.RequestInputMonitoringAccess())
            {
                MacNativeUi.OpenPrivacySettings("Privacy_ListenEvent");
            }
            RefreshPermissionStatus();
        };
        var requestAccessibility = MacTheme.CreateButton("申请辅助功能权限");
        requestAccessibility.Click += (_, _) =>
        {
            MacNativeUi.OpenPrivacySettings("Privacy_Accessibility");
            RefreshPermissionStatus();
        };

        _captureHotkey = CreateHotkeyCombo(settings.CaptureHotkey);
        _scrollHotkey = CreateHotkeyCombo(settings.ScrollHotkey);
        _recordingHotkey = CreateHotkeyCombo(settings.RecordingHotkey);
        _ocrHotkey = CreateHotkeyCombo(settings.OcrHotkey);
        _translationHotkey = CreateHotkeyCombo(settings.TranslationHotkey);
        _pinHotkey = CreateHotkeyCombo(settings.PinHotkey);
        _settingsHotkey = CreateHotkeyCombo(settings.SettingsHotkey);
        _historyLimit = new NumericUpDown
        {
            Minimum = 1,
            Maximum = 100,
            Increment = 10,
            Value = settings.HistoryLimit,
            MinWidth = 140,
        };
        _saveDirectory = new TextBox
        {
            Text = settings.SaveDirectory,
            MinWidth = 300,
            Watermark = "截图保存目录",
        };
        _videoSaveDirectory = new TextBox
        {
            Text = settings.VideoSaveDirectory,
            MinWidth = 300,
            Watermark = "视频保存目录",
        };
        _keepHistory = new CheckBox
        {
            Content = "保留截图历史",
            IsChecked = settings.KeepHistory,
        };
        _persistHistory = new CheckBox
        {
            Content = "重启后恢复截图历史（最多 100 张）",
            IsChecked = settings.PersistHistoryAcrossRestarts,
            IsEnabled = settings.KeepHistory,
        };
        _showNotificationIcon = new CheckBox
        {
            Content = "在菜单栏显示 SnapCut 图标",
            IsChecked = settings.ShowNotificationIcon,
        };
        _showFloatingCaptureButton = new CheckBox
        {
            Content = "显示截图悬浮按钮",
            IsChecked = settings.ShowFloatingCaptureButton,
        };
        _floatingCaptureBehavior = new ComboBox
        {
            ItemsSource = FloatingCaptureChoices,
            SelectedItem = FloatingCaptureChoices.FirstOrDefault(option =>
                option.Id == settings.FloatingCaptureClickBehavior),
            MinWidth = 180,
        };
        _closeBehavior = new ComboBox
        {
            ItemsSource = CloseChoices,
            SelectedItem = CloseChoices.FirstOrDefault(option =>
                option.Id == settings.CloseBehavior),
            MinWidth = 180,
        };
        _showPreview = new CheckBox
        {
            Content = "截图后打开预览窗口",
            IsChecked = settings.ShowPreviewAfterCapture,
        };
        _toolbarRows = new ComboBox
        {
            ItemsSource = new[] { "一行", "两行" },
            SelectedIndex = Math.Clamp(settings.ToolbarRows, 1, 2) - 1,
            MinWidth = 120,
        };
        _toolbarOrder = new ListBox
        {
            Height = 86,
            SelectionMode = SelectionMode.Single,
            ItemsSource = settings.ToolbarFeatureOrder
                .Select(CreateToolbarOption)
                .ToArray(),
            SelectedIndex = 0,
        };
        _videoFormat = new ComboBox
        {
            ItemsSource = new[] { "Mp4", "Gif" },
            SelectedItem = settings.VideoOutputFormat,
            MinWidth = 120,
        };
        _videoCodec = new ComboBox
        {
            ItemsSource = new[] { "H264", "H265" },
            SelectedItem = settings.VideoCodec,
            MinWidth = 120,
        };
        _videoFrameRate = new ComboBox
        {
            ItemsSource = new[] { 15, 24, 30, 60 },
            SelectedItem = settings.VideoFrameRate,
            MinWidth = 120,
        };
        _recordSystemAudio = new CheckBox
        {
            Content = "录制系统声音",
            IsChecked = settings.RecordSystemAudio,
        };
        _recordMicrophone = new CheckBox
        {
            Content = "录制麦克风",
            IsChecked = settings.RecordMicrophone,
        };
        _showMouseInput = new CheckBox
        {
            Content = "显示鼠标点击",
            IsChecked = settings.ShowMouseInputInRecording,
        };
        _showKeyboardInput = new CheckBox
        {
            Content = "显示键盘输入",
            IsChecked = settings.ShowKeyboardInputInRecording,
        };
        _theme = new ComboBox
        {
            ItemsSource = new[]
            {
                "System", "AuroraMist", "CoralSky", "GinkgoPaper",
                "ForestNight", "ObsidianGold", "NeonDeep",
            },
            SelectedItem = settings.Theme,
            MinWidth = 140,
        };
        _themeCards = CreateThemeCards();
        _launchAtStartup = new CheckBox
        {
            Content = "登录 macOS 后自动启动 SnapCut",
            IsChecked = settings.LaunchAtStartup,
        };
        _scrollCaptureMode = new ComboBox
        {
            ItemsSource = new[] { "Automatic", "Manual" },
            SelectedItem = settings.ScrollCaptureMode,
            MinWidth = 140,
        };
        _ocrStatus = StatusText();
        _ocrInstallButton = MacTheme.CreateButton("下载高质量模型", primary: true);
        _ocrInstallButton.Click += async (_, _) => await InstallOcrModelsAsync();
        RefreshOcrStatus();
        _translationEndpoint = new TextBox
        {
            Text = settings.TranslationEndpoint,
            Watermark = "HTTPS 翻译接口",
        };
        _translationModel = new TextBox
        {
            Text = settings.TranslationModel,
            Watermark = "模型名称",
        };
        _translationTarget = new TextBox
        {
            Text = settings.TranslationTargetLanguage,
            Watermark = "目标语言，例如 zh-Hans",
        };
        _translationApiKey = new TextBox
        {
            Text = MacKeychainCredentialStore.Load() is null
                ? string.Empty
                : "已保存（留空不修改）",
            Watermark = "API Key（写入 macOS Keychain）",
            PasswordChar = '•',
        };
        _offlineTranslationConfig = new TextBox
        {
            Text = settings.OfflineTranslationConfigPath,
            Watermark = "离线模型 config.yml 路径",
        };
        _sendOnlineTranslation = new CheckBox
        {
            Content = "允许把识别文字发送到在线翻译服务",
            IsChecked = settings.SendTextToOnlineTranslation,
        };
        _logText = new TextBlock
        {
            Text = hotkeysRunning
                ? "全局快捷键正在监听"
                : "请授予输入监控权限后重启，以启用全局快捷键",
            FontSize = 12,
            Foreground = new SolidColorBrush(MacTheme.SecondaryText),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };

        _historyItems = new StackPanel { Spacing = 6 };
        RefreshHistory(history);
        var openHistory = MacTheme.CreateButton("打开截图文件夹");
        openHistory.Click += (_, _) =>
        {
            Directory.CreateDirectory(history.HistoryDirectory);
            MacNativeUi.OpenPath(history.HistoryDirectory);
        };

        _sections =
        [
            CreateGeneralSection(
                quickActions,
                openHistory,
                requestScreen,
                requestInput,
                requestAccessibility),
            CreateHotkeySection(),
            CreateRecognitionSection(),
            CreateTranslationSection(),
            CreateUpdateSection(),
            CreateDonateSection(),
        ];
        _sectionHost = new ContentControl();

        var logBar = CreateLogBar();
        var contentGrid = new Grid
        {
            RowDefinitions = new RowDefinitions("*,Auto"),
            Margin = new Thickness(34, 28, 34, 26),
            Children = { _sectionHost, logBar },
        };
        Grid.SetRow(logBar, 1);

        var sidebar = CreateSidebar();
        var shell = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("178,*"),
            Background = new SolidColorBrush(MacTheme.WindowBackground),
            Children = { sidebar, contentGrid },
        };
        Grid.SetColumn(contentGrid, 1);
        Content = new Border
        {
            Margin = new Thickness(8),
            Background = new SolidColorBrush(MacTheme.WindowBackground),
            BorderBrush = new SolidColorBrush(MacTheme.Border),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(18),
            ClipToBounds = true,
            Child = shell,
        };

        _captureHotkey.SelectionChanged += (_, _) => SaveSettings();
        _scrollHotkey.SelectionChanged += (_, _) => SaveSettings();
        _recordingHotkey.SelectionChanged += (_, _) => SaveSettings();
        _ocrHotkey.SelectionChanged += (_, _) => SaveSettings();
        _translationHotkey.SelectionChanged += (_, _) => SaveSettings();
        _pinHotkey.SelectionChanged += (_, _) => SaveSettings();
        _settingsHotkey.SelectionChanged += (_, _) => SaveSettings();
        _historyLimit.ValueChanged += (_, _) => SaveSettings();
        _saveDirectory.TextChanged += (_, _) => SaveSettings();
        _videoSaveDirectory.TextChanged += (_, _) => SaveSettings();
        _keepHistory.IsCheckedChanged += (_, _) =>
        {
            _persistHistory.IsEnabled = _keepHistory.IsChecked == true;
            SaveSettings();
        };
        _persistHistory.IsCheckedChanged += (_, _) => SaveSettings();
        _showNotificationIcon.IsCheckedChanged += (_, _) => SaveSettings();
        _showFloatingCaptureButton.IsCheckedChanged += (_, _) => SaveSettings();
        _floatingCaptureBehavior.SelectionChanged += (_, _) => SaveSettings();
        _closeBehavior.SelectionChanged += (_, _) => SaveSettings();
        _showPreview.IsCheckedChanged += (_, _) => SaveSettings();
        _toolbarRows.SelectionChanged += (_, _) => SaveSettings();
        _videoFormat.SelectionChanged += (_, _) => SaveSettings();
        _videoCodec.SelectionChanged += (_, _) => SaveSettings();
        _videoFrameRate.SelectionChanged += (_, _) => SaveSettings();
        _recordSystemAudio.IsCheckedChanged += (_, _) => SaveSettings();
        _recordMicrophone.IsCheckedChanged += (_, _) => SaveSettings();
        _showMouseInput.IsCheckedChanged += (_, _) => SaveSettings();
        _showKeyboardInput.IsCheckedChanged += (_, _) => SaveSettings();
        _theme.SelectionChanged += (_, _) =>
        {
            UpdateThemeCardSelection();
            SaveSettings();
        };
        _launchAtStartup.IsCheckedChanged += (_, _) => SaveSettings();
        _scrollCaptureMode.SelectionChanged += (_, _) => SaveSettings();
        foreach (var checkBox in _toolbarFeatureChecks.Values)
        {
            checkBox.IsCheckedChanged += (_, _) => SaveSettings();
        }
        _toolbarOrder.SelectionChanged += (_, _) => UpdateToolbarPreview();
        _toolbarOrder.PointerPressed += OnToolbarOrderPointerPressed;
        _toolbarOrder.PointerMoved += OnToolbarOrderPointerMoved;
        _toolbarOrder.PointerReleased += OnToolbarOrderPointerReleased;
        _loading = false;
        ShowSection(0);

        Opened += (_, _) =>
        {
            MacNativeUi.ExcludeFromScreenCapture(this);
            RefreshPermissionStatus();
        };
        Activated += (_, _) => RefreshPermissionStatus();
    }

    private Border CreateSidebar()
    {
        var navigation = new StackPanel { Spacing = 4 };
        var items = new (string Icon, string Text)[]
        {
            ("⚙", "常规设置"),
            ("⌘", "快捷键"),
            ("◎", "内容识别"),
            ("文", "翻译"),
            ("↻", "版本更新"),
            ("♡", "打赏支持"),
        };
        for (var index = 0; index < items.Length; index++)
        {
            var selectedIndex = index;
            var button = CreateNavigationButton(items[index].Icon, items[index].Text);
            button.Click += (_, _) => ShowSection(selectedIndex);
            _navigationButtons.Add(button);
            navigation.Children.Add(button);
        }

        var sidebarGrid = new Grid
        {
            Margin = new Thickness(24, 26, 20, 24),
            RowDefinitions = new RowDefinitions("Auto,*"),
            Children =
            {
                new StackPanel
                {
                    Children =
                    {
                        new TextBlock
                        {
                            Text = "SnapCut",
                            FontSize = 22,
                            FontWeight = FontWeight.SemiBold,
                            Foreground = new SolidColorBrush(MacTheme.PrimaryText),
                        },
                        new Border
                        {
                            Height = 1,
                            Margin = new Thickness(0, 24, 0, 18),
                            Background = new SolidColorBrush(MacTheme.Separator),
                        },
                    },
                },
                navigation,
            },
        };
        Grid.SetRow(navigation, 1);
        return new Border
        {
            Background = new SolidColorBrush(MacTheme.SidebarBackground),
            BorderBrush = new SolidColorBrush(MacTheme.SubtleBorder),
            BorderThickness = new Thickness(0, 0, 1, 0),
            Child = sidebarGrid,
        };
    }

    private ScrollViewer CreateGeneralSection(
        Control quickActions,
        Control openHistory,
        Control requestScreen,
        Control requestInput,
        Control requestAccessibility)
    {
        return Section(
            "常规",
            "保存、外观和运行方式",
            Group(
                "截图与保存",
                "截图行为与本地历史记录",
                quickActions,
                _showPreview,
                LabeledControl("截图保存目录", _saveDirectory),
                LabeledControl("视频保存目录", _videoSaveDirectory),
                LabeledControl("保留最近截图", _historyLimit),
                _keepHistory,
                _persistHistory,
                LabeledControl("长截图模式", _scrollCaptureMode),
                openHistory,
                _historyItems),
            Group(
                "视频录制",
                "区域录屏、音频、输入提示和导出格式",
                LabeledControl("格式", _videoFormat),
                LabeledControl("编码", _videoCodec),
                LabeledControl("帧率", _videoFrameRate),
                _recordSystemAudio,
                _recordMicrophone,
                _showMouseInput,
                _showKeyboardInput,
                Hint("键盘输入提示需要输入监控权限；录屏工具栏支持拖动和双击居中。")),
            Group(
                "系统权限",
                "macOS 需要以下权限才能完成全局截图",
                _screenPermission,
                requestScreen,
                _inputPermission,
                requestInput,
                _accessibilityPermission,
                requestAccessibility,
                Hint("授权后请完全退出 SnapCut（菜单栏图标 → 退出），再从 SnapCut.app 重新打开；重新编译的未签名副本会被 macOS 视为新应用。")),
            Group(
                "外观与启动",
                "选择主题后当前设置窗口会立即刷新。",
                _themeCards,
                _showNotificationIcon,
                _launchAtStartup,
                LabeledControl("点击关闭按钮时", _closeBehavior)),
            Group(
                "悬浮截图",
                "桌面悬浮按钮及单击行为",
                _showFloatingCaptureButton,
                LabeledControl("单击悬浮按钮时", _floatingCaptureBehavior)),
            CreateToolbarConfiguration());
    }

    private StackPanel CreateToolbarConfiguration()
    {
        var visible = _settings.VisibleToolbarFeatures.ToHashSet(StringComparer.Ordinal);
        var choices = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            ItemWidth = 126,
            ItemHeight = 30,
        };
        foreach (var feature in MacSettings.DefaultToolbarFeatures)
        {
            var checkBox = new CheckBox
            {
                Content = ToolbarLabels[feature],
                IsChecked = visible.Contains(feature),
            };
            _toolbarFeatureChecks.Add(feature, checkBox);
            choices.Children.Add(checkBox);
        }

        UpdateToolbarPreview();
        return Group(
            "截图工具栏",
            "点击选择显示功能，支持一行或两行布局；下方为实时预览。",
            LabeledControl("布局", _toolbarRows),
            choices,
            new TextBlock
            {
                Text = "功能顺序（选中后使用上移/下移）",
                Foreground = new SolidColorBrush(MacTheme.SecondaryText),
                FontSize = 12,
            },
            new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Children =
                {
                    _toolbarOrder,
                    CreateToolbarOrderButton("上移", -1),
                    CreateToolbarOrderButton("下移", 1),
                },
            },
            new Border
            {
                Padding = new Thickness(8),
                Background = new SolidColorBrush(Color.Parse("#E8212A38")),
                BorderBrush = new SolidColorBrush(MacTheme.Border),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Child = _toolbarPreview,
            });
    }

    private void UpdateToolbarPreview()
    {
        _toolbarPreview.Children.Clear();
        var enabled = _toolbarFeatureChecks.Count == 0
            ? _settings.VisibleToolbarFeatures.ToHashSet(StringComparer.Ordinal)
            : _toolbarFeatureChecks
                .Where(pair => pair.Value.IsChecked == true)
                .Select(pair => pair.Key)
                .ToHashSet(StringComparer.Ordinal);
        var rows = Math.Clamp(_toolbarRows.SelectedIndex + 1, 1, 2);
        var rowControls = Enumerable.Range(0, rows)
            .Select(_ => new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4,
            })
            .ToArray();
        var icons = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Shape"] = "□", ["Arrow"] = "↗", ["Emoji"] = "☺",
            ["Number"] = "①", ["Brush"] = "✎", ["Text"] = "T",
            ["Mosaic"] = "▦", ["UndoRedo"] = "↶↷", ["RecognizeText"] = "OCR",
            ["Save"] = "▣", ["ScrollCapture"] = "↕",
            ["CopyRecognizedText"] = "复制文", ["PrivacyRedaction"] = "隐私",
            ["Translation"] = "翻译", ["PinImage"] = "钉图",
            ["VideoRecording"] = "录屏",
            ["QrRecognition"] = "码",
            ["CaptureAllScreens"] = "全屏",
        };
        var index = 0;
        var order = (_toolbarOrder.ItemsSource as IEnumerable<MacSettingOption>)?
            .Select(option => option.Id)
            ?? MacSettings.DefaultToolbarFeatures;
        foreach (var feature in order.Where(enabled.Contains))
        {
            rowControls[index % rows].Children.Add(new Border
            {
                MinWidth = 34,
                Height = 28,
                Padding = new Thickness(6, 0),
                CornerRadius = new CornerRadius(4),
                Background = new SolidColorBrush(Color.Parse("#33425263")),
                Child = new TextBlock
                {
                    Text = icons[feature],
                    Foreground = Brushes.White,
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    FontSize = 11,
                },
            });
            index++;
        }

        foreach (var row in rowControls)
        {
            _toolbarPreview.Children.Add(row);
        }
    }

    private Button CreateToolbarOrderButton(string text, int delta)
    {
        var button = MacTheme.CreateButton(text);
        button.Click += (_, _) =>
        {
            var items = (_toolbarOrder.ItemsSource as IEnumerable<MacSettingOption>)?.ToList()
                ?? MacSettings.DefaultToolbarFeatures.Select(CreateToolbarOption).ToList();
            var index = _toolbarOrder.SelectedIndex;
            var target = index + delta;
            if (index < 0 || target < 0 || target >= items.Count)
            {
                return;
            }

            (items[index], items[target]) = (items[target], items[index]);
            _toolbarOrder.ItemsSource = items;
            _toolbarOrder.SelectedIndex = target;
            SaveSettings();
            UpdateToolbarPreview();
        };
        return button;
    }

    private static MacSettingOption CreateToolbarOption(string feature) =>
        new(feature, ToolbarLabels.TryGetValue(feature, out var label) ? label : feature);

    private WrapPanel CreateThemeCards()
    {
        var themes = new (string Id, string Name, string Description, string Start, string End)[]
        {
            ("AuroraMist", "极光晨雾", "浅色 · 雾白与靛蓝", "#F7F8FB", "#7E91D5"),
            ("CoralSky", "珊瑚晴空", "浅色 · 珊瑚与天青", "#F7D8D4", "#BFDCE6"),
            ("GinkgoPaper", "银杏纸白", "浅色 · 纸白与银杏", "#FAFAF7", "#D3AF5B"),
            ("ForestNight", "松林夜雨", "深色 · 石墨与松绿", "#15181C", "#4E8B74"),
            ("ObsidianGold", "熔金曜石", "深色 · 曜石与暖金", "#141516", "#D3A343"),
            ("NeonDeep", "霓虹深海", "深色 · 深海与玫红", "#10151E", "#D65E76"),
        };
        var panel = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            ItemWidth = 190,
            ItemHeight = 62,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        foreach (var theme in themes)
        {
            var button = new Button
            {
                Tag = theme.Id,
                Width = 180,
                Height = 54,
                Margin = new Thickness(0, 0, 10, 10),
                Padding = new Thickness(10),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Background = new LinearGradientBrush
                {
                    StartPoint = new RelativePoint(0, 0.5, RelativeUnit.Relative),
                    EndPoint = new RelativePoint(1, 0.5, RelativeUnit.Relative),
                    GradientStops =
                    {
                        new GradientStop(Color.Parse(theme.Start), 0),
                        new GradientStop(Color.Parse(theme.End), 1),
                    },
                },
                BorderBrush = new SolidColorBrush(MacTheme.Border),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Content = new StackPanel
                {
                    Children =
                    {
                        new TextBlock
                        {
                            Text = theme.Name,
                            FontWeight = FontWeight.SemiBold,
                            Foreground = theme.Id is "GinkgoPaper" or "CoralSky"
                                ? Brushes.Black
                                : Brushes.White,
                        },
                        new TextBlock
                        {
                            Text = theme.Description,
                            FontSize = 11,
                            Foreground = theme.Id is "GinkgoPaper" or "CoralSky"
                                ? new SolidColorBrush(Color.Parse("#30343B"))
                                : new SolidColorBrush(Color.Parse("#E7EDF5")),
                        },
                    },
                },
            };
            button.Click += (_, _) =>
            {
                _theme.SelectedItem = theme.Id;
                UpdateThemeCardSelection();
                SaveSettings();
            };
            panel.Children.Add(button);
        }

        UpdateThemeCardSelection(panel);
        return panel;
    }

    private void UpdateThemeCardSelection() => UpdateThemeCardSelection(_themeCards);

    private void UpdateThemeCardSelection(WrapPanel panel)
    {
        foreach (var button in panel.Children.OfType<Button>())
        {
            var selected = string.Equals(
                button.Tag as string,
                _theme.SelectedItem?.ToString(),
                StringComparison.Ordinal);
            button.BorderBrush = selected
                ? MacTheme.AccentBrush
                : new SolidColorBrush(MacTheme.Border);
            button.BorderThickness = selected
                ? new Thickness(2)
                : new Thickness(1);
        }
    }

    private void OnToolbarOrderPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(_toolbarOrder).Properties.IsLeftButtonPressed)
        {
            return;
        }

        _toolbarDragStart = e.GetPosition(_toolbarOrder);
        _toolbarDragIndex = Math.Clamp(
            (int)(_toolbarDragStart.Y / 30),
            0,
            Math.Max(0, _toolbarOrder.ItemCount - 1));
        e.Pointer.Capture(_toolbarOrder);
    }

    private void OnToolbarOrderPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_toolbarDragIndex < 0 ||
            !e.GetCurrentPoint(_toolbarOrder).Properties.IsLeftButtonPressed)
        {
            return;
        }

        var point = e.GetPosition(_toolbarOrder);
        if (Math.Abs(point.Y - _toolbarDragStart.Y) < 6)
        {
            return;
        }

        var items = (_toolbarOrder.ItemsSource as IEnumerable<MacSettingOption>)?.ToList();
        if (items is null || items.Count == 0)
        {
            return;
        }

        var target = Math.Clamp((int)(point.Y / 30), 0, items.Count - 1);
        if (target == _toolbarDragIndex)
        {
            return;
        }

        var moved = items[_toolbarDragIndex];
        items.RemoveAt(_toolbarDragIndex);
        items.Insert(target, moved);
        _toolbarOrder.ItemsSource = items;
        _toolbarOrder.SelectedIndex = target;
        _toolbarDragIndex = target;
        _toolbarDragStart = point;
        SaveSettings();
        UpdateToolbarPreview();
    }

    private void OnToolbarOrderPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _toolbarDragIndex = -1;
        e.Pointer.Capture(null);
    }

    private ScrollViewer CreateHotkeySection()
    {
        return Section(
            "快捷键",
            "配置触发方式；修改后立即生效",
            Group(
                "截图快捷键",
                "Command 键在 VMware 的 Windows 键盘中对应 Win 键",
                LabeledControl("区域截图", _captureHotkey),
                LabeledControl("长截图", _scrollHotkey),
                LabeledControl("视频录制", _recordingHotkey),
                LabeledControl("文字识别", _ocrHotkey),
                LabeledControl("翻译", _translationHotkey),
                LabeledControl("钉图", _pinHotkey),
                LabeledControl("打开设置", _settingsHotkey),
                Hint("⌘⇧A = Win + Shift + A    ·    ⌘⇧S = Win + Shift + S")));
    }

    private ScrollViewer CreateRecordingSection()
    {
        return Section(
            "视频录制",
            "区域录屏、音频、输入提示和导出格式",
            Group(
                "录制格式",
                "录制完成后可直接保存，或进入轻量编辑裁剪并导出。",
                LabeledControl("格式", _videoFormat),
                LabeledControl("编码", _videoCodec),
                LabeledControl("帧率", _videoFrameRate)),
            Group(
                "声音与输入",
                "macOS 会按系统权限提供可用的音频源。",
                _recordSystemAudio,
                _recordMicrophone,
                _showMouseInput,
                _showKeyboardInput,
                Hint("键盘输入提示需要输入监控权限；录屏工具栏支持拖动和双击居中。")));
    }

    private ScrollViewer CreateRecognitionSection()
    {
        return Section(
            "内容识别",
            "文字识别、复制与隐私信息处理",
            Group(
                "PP-OCRv6 高质量引擎",
                "与 Windows 高质量模式使用相同模型；识别时由独立辅助进程运行。",
                _ocrStatus,
                _ocrInstallButton,
                Hint("模型约 31 MB，保存在 ~/Library/Application Support/SnapCut/Models。")),
            Group(
                "隐私识别",
                "识别手机号、邮箱、身份证号、API Key 和 IP 地址，确认后批量打码。",
                Hint("不会自动覆盖原图；只有用户确认的候选项会被处理。")));
    }

    private async Task InstallOcrModelsAsync()
    {
        _ocrInstallButton.IsEnabled = false;
        _logText.Text = "正在准备下载 OCR 模型…";
        var progress = new Progress<MacModelDownloadProgress>(value =>
        {
            var percent = value.TotalBytes <= 0
                ? 0
                : value.DownloadedBytes * 100d / value.TotalBytes;
            _ocrStatus.Text = $"{value.FileName}  {percent:F0}%";
        });
        var result = await _ocrModels.InstallAsync(progress, CancellationToken.None);
        _logText.Text = result.IsSuccess
            ? "OCR 高质量模型安装完成"
            : result.ErrorMessage ?? "OCR 模型安装失败";
        _ocrInstallButton.IsEnabled = true;
        RefreshOcrStatus();
    }

    private void RefreshOcrStatus()
    {
        if (RuntimeInformation.ProcessArchitecture == Architecture.X64)
        {
            _ocrStatus.Text = "macOS Vision 高质量 OCR 已启用（Intel 原生）";
            _ocrInstallButton.IsVisible = false;
            return;
        }

        var status = _ocrModels.GetStatus();
        _ocrStatus.Text = status.IsInstalled
            ? $"模型已安装 · {status.InstalledSize / 1024d / 1024d:F1} MB"
            : $"模型未安装 · 需要下载 {status.DownloadSize / 1024d / 1024d:F1} MB";
        _ocrInstallButton.Content = status.IsInstalled
            ? "校验并修复模型"
            : "下载高质量模型";
    }

    private ScrollViewer CreateTranslationSection()
    {
        return Section(
            "翻译",
            "在线与本地翻译配置",
            Group(
                "在线翻译",
                "OpenAI 兼容接口；API Key 仅保存到 macOS Keychain。",
                LabeledControl("接口地址", _translationEndpoint),
                LabeledControl("模型", _translationModel),
                LabeledControl("目标语言", _translationTarget),
                LabeledControl("API Key", _translationApiKey),
                _sendOnlineTranslation,
                Hint("保存设置后，截图工具栏中的“翻译”按钮才会执行在线请求。")),
            Group(
                "离线翻译",
                "使用 Bergamot 模型；可填写已下载语言包中的 config.yml。",
                LabeledControl("模型配置", _offlineTranslationConfig),
                Hint("离线模型目录不会上传文本，也不需要 API Key。")));
    }

    private static ScrollViewer CreateUpdateSection()
    {
        var version = typeof(SettingsWindow).Assembly.GetName().Version?.ToString(3)
            ?? "0.2.0";
        var github = MacTheme.CreateButton("GitHub 发布页");
        github.Click += (_, _) => MacNativeUi.OpenUrl(
            "https://github.com/wwangyunhui/screenshot/releases");
        var gitee = MacTheme.CreateButton("Gitee 发布页");
        gitee.Click += (_, _) => MacNativeUi.OpenUrl(
            "https://gitee.com/wwangyunhui/screenshot/releases");
        var status = Hint("更新源会按公网 IP 所在国家选择，并在失败时自动回退。");
        var check = MacTheme.CreateButton("检查并下载更新", primary: true);
        check.Click += async (_, _) => await CheckForUpdatesAsync(status, check);
        return Section(
            "版本更新",
            "查看 GitHub / Gitee 正式版本与下载页面",
            Group(
                "SnapCut",
                $"当前 macOS 版本：{version}",
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Children = { github, gitee },
                },
                check,
                status));
    }

    private static async Task CheckForUpdatesAsync(TextBlock status, Button button)
    {
        button.IsEnabled = false;
        status.Text = "正在检测更新…";
        try
        {
            using var service = new MacApplicationUpdateService();
            var current = typeof(SettingsWindow).Assembly.GetName().Version
                ?? new Version(0, 0);
            var update = await service.CheckAsync(current);
            if (update is null)
            {
                status.Text = "当前已经是最新版，或两个更新源暂时不可用。";
                return;
            }

            status.Text = $"发现 {update.Tag}，正在从 {update.Source} 下载…";
            var path = await service.DownloadAsync(update);
            status.Text = $"已下载：{path}";
            MacNativeUi.OpenPath(Path.GetDirectoryName(path) ?? path);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or HttpRequestException)
        {
            status.Text = "更新下载失败：" + exception.Message;
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    private static ScrollViewer CreateDonateSection()
    {
        var profile = MacTheme.CreateButton("访问 B 站主页");
        profile.Click += (_, _) => MacNativeUi.OpenUrl("https://b23.tv/ZzD0zPS");
        var avatar = AssetImage("avares://snapcut/Assets/CreatorAvatar.jpg", 64, 64);
        var qr = AssetImage("avares://snapcut/Assets/DonateQr.jpg", 220, 220);
        var creator = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("72,*,Auto"),
            Children =
            {
                avatar,
                new StackPanel
                {
                    Margin = new Thickness(14, 0, 18, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = "九万字__",
                            FontSize = 17,
                            FontWeight = FontWeight.SemiBold,
                            Foreground = new SolidColorBrush(MacTheme.PrimaryText),
                        },
                        Hint("SnapCut 作者 · 哔哩哔哩个人空间"),
                    },
                },
                profile,
            },
        };
        Grid.SetColumn(creator.Children[1], 1);
        Grid.SetColumn(profile, 2);
        return Section(
            "打赏支持",
            "源码公开与长期维护不易，感谢每一份支持。",
            new Border
            {
                Margin = new Thickness(0, 22, 0, 0),
                Padding = new Thickness(18),
                BorderBrush = MacTheme.AccentBrush,
                BorderThickness = new Thickness(2),
                CornerRadius = new CornerRadius(10),
                Background = new SolidColorBrush(MacTheme.PanelBackground),
                Child = creator,
            },
            new StackPanel
            {
                Margin = new Thickness(0, 24, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
                Children =
                {
                    qr,
                    new TextBlock
                    {
                        Text = "微信扫码打赏",
                        Margin = new Thickness(0, 10, 0, 0),
                        HorizontalAlignment = HorizontalAlignment.Center,
                        FontWeight = FontWeight.SemiBold,
                    },
                },
            });
    }

    private Border CreateLogBar()
    {
        var icon = new TextBlock
        {
            Text = "ⓘ",
            Margin = new Thickness(11, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(MacTheme.AccentStart),
        };
        var label = new TextBlock
        {
            Text = "日志输出",
            Margin = new Thickness(7, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 12,
            FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(MacTheme.PrimaryText),
        };
        var grid = new Grid
        {
            MinHeight = 38,
            ColumnDefinitions = new ColumnDefinitions("3,Auto,Auto,*"),
            Children =
            {
                new Border
                {
                    Background = MacTheme.AccentBrush,
                    CornerRadius = new CornerRadius(5, 0, 0, 5),
                },
                icon,
                label,
                _logText,
            },
        };
        Grid.SetColumn(icon, 1);
        Grid.SetColumn(label, 2);
        Grid.SetColumn(_logText, 3);
        _logText.Margin = new Thickness(12, 0);
        return new Border
        {
            Margin = new Thickness(0, 12, 16, 0),
            Background = new SolidColorBrush(MacTheme.PanelBackground),
            BorderBrush = new SolidColorBrush(MacTheme.Border),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Child = grid,
        };
    }

    private void ShowSection(int index)
    {
        _sectionHost.Content = _sections[index];
        for (var buttonIndex = 0; buttonIndex < _navigationButtons.Count; buttonIndex++)
        {
            var selected = buttonIndex == index;
            _navigationButtons[buttonIndex].Background = selected
                ? new SolidColorBrush(MacTheme.AccentMuted)
                : Brushes.Transparent;
            _navigationButtons[buttonIndex].Foreground = new SolidColorBrush(
                selected ? MacTheme.AccentStart : MacTheme.SecondaryText);
            _navigationButtons[buttonIndex].FontWeight = selected
                ? FontWeight.SemiBold
                : FontWeight.Normal;
        }
    }

    private void SaveSettings()
    {
        if (_loading)
        {
            return;
        }

        var capture = (MacHotkeyGesture?)_captureHotkey.SelectedItem
            ?? MacHotkeyGesture.CaptureDefault;
        var scroll = (MacHotkeyGesture?)_scrollHotkey.SelectedItem
            ?? MacHotkeyGesture.ScrollDefault;
        var recording = (MacHotkeyGesture?)_recordingHotkey.SelectedItem
            ?? MacHotkeyGesture.RecordingDefault;
        var ocr = (MacHotkeyGesture?)_ocrHotkey.SelectedItem
            ?? MacHotkeyGesture.OcrDefault;
        var translation = (MacHotkeyGesture?)_translationHotkey.SelectedItem
            ?? MacHotkeyGesture.TranslationDefault;
        var pin = (MacHotkeyGesture?)_pinHotkey.SelectedItem
            ?? MacHotkeyGesture.PinDefault;
        var openSettings = (MacHotkeyGesture?)_settingsHotkey.SelectedItem
            ?? MacHotkeyGesture.SettingsDefault;
        if (new[] { capture, scroll, recording, ocr, translation, pin, openSettings }
            .Distinct()
            .Count() != 7)
        {
            _logText.Text = "不同功能不能使用同一个快捷键";
            return;
        }

        _settings.CaptureHotkey = capture;
        _settings.ScrollHotkey = scroll;
        _settings.RecordingHotkey = recording;
        _settings.OcrHotkey = ocr;
        _settings.TranslationHotkey = translation;
        _settings.PinHotkey = pin;
        _settings.SettingsHotkey = openSettings;
        _settings.HistoryLimit = Math.Clamp(
            (int)(_historyLimit.Value ?? 100),
            1,
            100);
        _settings.SaveDirectory = _saveDirectory.Text?.Trim() ?? string.Empty;
        _settings.VideoSaveDirectory = _videoSaveDirectory.Text?.Trim() ?? string.Empty;
        _settings.KeepHistory = _keepHistory.IsChecked == true;
        _settings.PersistHistoryAcrossRestarts =
            _settings.KeepHistory && _persistHistory.IsChecked == true;
        _settings.ShowNotificationIcon = _showNotificationIcon.IsChecked == true;
        _settings.ShowFloatingCaptureButton = _showFloatingCaptureButton.IsChecked == true;
        _settings.FloatingCaptureClickBehavior = (_floatingCaptureBehavior.SelectedItem as MacSettingOption)?.Id
            ?? "ShowSelection";
        _settings.CloseBehavior = (_closeBehavior.SelectedItem as MacSettingOption)?.Id
            ?? "MinimizeToBackground";
        _settings.ShowPreviewAfterCapture = _showPreview.IsChecked != false;
        _settings.TranslationEndpoint = _translationEndpoint.Text?.Trim() ?? string.Empty;
        _settings.TranslationModel = _translationModel.Text?.Trim() ?? string.Empty;
        _settings.TranslationTargetLanguage = _translationTarget.Text?.Trim() ?? "zh-Hans";
        _settings.SendTextToOnlineTranslation = _sendOnlineTranslation.IsChecked == true;
        _settings.OfflineTranslationConfigPath = _offlineTranslationConfig.Text?.Trim() ?? string.Empty;
        _settings.ToolbarRows = Math.Clamp(_toolbarRows.SelectedIndex + 1, 1, 2);
        _settings.VisibleToolbarFeatures = _toolbarFeatureChecks
            .Where(pair => pair.Value.IsChecked == true)
            .Select(pair => pair.Key)
            .ToArray();
        _settings.ToolbarFeatureOrder = ((_toolbarOrder.ItemsSource as IEnumerable<MacSettingOption>)?
            .Select(option => option.Id)
            ?? MacSettings.DefaultToolbarFeatures)
            .ToArray();
        _settings.VideoOutputFormat = _videoFormat.SelectedItem?.ToString() ?? "Mp4";
        _settings.VideoCodec = _videoCodec.SelectedItem?.ToString() ?? "H264";
        _settings.VideoFrameRate = _videoFrameRate.SelectedItem is int frameRate
            ? frameRate
            : 30;
        _settings.RecordSystemAudio = _recordSystemAudio.IsChecked == true;
        _settings.RecordMicrophone = _recordMicrophone.IsChecked == true;
        _settings.ShowMouseInputInRecording = _showMouseInput.IsChecked == true;
        _settings.ShowKeyboardInputInRecording = _showKeyboardInput.IsChecked == true;
        _settings.Theme = _theme.SelectedItem?.ToString() ?? "AuroraMist";
        _settings.LaunchAtStartup = _launchAtStartup.IsChecked == true;
        _settings.ScrollCaptureMode = _scrollCaptureMode.SelectedItem?.ToString()
            ?? "Automatic";
        var key = _translationApiKey.Text?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(key) &&
            !key.StartsWith("已保存", StringComparison.Ordinal))
        {
            MacKeychainCredentialStore.Save(key);
            _translationApiKey.Text = "已保存（留空不修改）";
        }
        _save(_settings);
        UpdateToolbarPreview();
        _logText.Text = "设置已保存并立即生效";
    }

    private void RefreshPermissionStatus()
    {
        _screenPermission.Text = MacScreenCaptureService.HasScreenCaptureAccess()
            ? "屏幕录制：可用"
            : "屏幕录制：系统开关关闭，或此构建尚未重新授权";
        _inputPermission.Text = MacGlobalHotkeyService.HasInputMonitoringAccess()
            ? "输入监控：可用"
            : "输入监控：未授权，或授权尚未对当前 SnapCut.app 生效";
        _accessibilityPermission.Text = MacAccessibility.IsTrusted()
            ? "辅助功能：可用"
            : "辅助功能：未授权（窗口吸附和部分输入监听可能不可用）";
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
            _historyItems.Children.Add(Hint("还没有截图记录"));
        }
    }

    private static Button CreateNavigationButton(string icon, string text)
    {
        var label = new TextBlock
        {
            Text = text,
            Margin = new Thickness(10, 0, 0, 0),
            FontSize = 16,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(label, 1);
        return new Button
        {
            Height = 48,
            Padding = new Thickness(12, 0),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Content = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("24,*"),
                Children =
                {
                    new TextBlock
                    {
                        Text = icon,
                        FontSize = 17,
                        TextAlignment = TextAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                    },
                    label,
                },
            },
        };
    }

    private static ScrollViewer Section(
        string title,
        string subtitle,
        params Control[] groups)
    {
        var content = new StackPanel
        {
            Margin = new Thickness(0, 0, 16, 24),
            Children =
            {
                new TextBlock
                {
                    Text = title,
                    FontSize = 22,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = new SolidColorBrush(MacTheme.PrimaryText),
                },
                new TextBlock
                {
                    Text = subtitle,
                    Margin = new Thickness(0, 7, 0, 0),
                    FontSize = 13,
                    Foreground = new SolidColorBrush(MacTheme.SecondaryText),
                },
            },
        };
        foreach (var group in groups)
        {
            content.Children.Add(group);
        }

        return new ScrollViewer
        {
            Content = content,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
        };
    }

    private static StackPanel Group(
        string title,
        string description,
        params Control[] controls)
    {
        var panel = new StackPanel
        {
            Margin = new Thickness(0, 28, 0, 0),
            Spacing = 10,
            Children =
            {
                new TextBlock
                {
                    Text = title,
                    FontSize = 16,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = new SolidColorBrush(MacTheme.PrimaryText),
                },
                new TextBlock
                {
                    Text = description,
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = new SolidColorBrush(MacTheme.SecondaryText),
                },
            },
        };
        foreach (var control in controls)
        {
            panel.Children.Add(control);
        }

        return panel;
    }

    private static Grid LabeledControl(string label, Control control)
    {
        var grid = new Grid
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
        };
        Grid.SetColumn(control, 1);
        return grid;
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

    private static TextBlock StatusText() => Hint(string.Empty);

    private static TextBlock Hint(string text)
    {
        return new TextBlock
        {
            Text = text,
            FontSize = 12,
            Foreground = new SolidColorBrush(MacTheme.SecondaryText),
            TextWrapping = TextWrapping.Wrap,
        };
    }

    private static Image AssetImage(string uri, double width, double height)
    {
        using var stream = AssetLoader.Open(new Uri(uri));
        return new Image
        {
            Source = new Bitmap(stream),
            Width = width,
            Height = height,
            Stretch = Stretch.UniformToFill,
        };
    }
}
