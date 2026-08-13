using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using Screenshot.App.Core;
using Screenshot.App.Text;

namespace Screenshot.App.Presentation;

public sealed record SettingOption(string Value, string Label);

public sealed class CaptureToolbarFeatureItem : INotifyPropertyChanged
{
    private bool _isVisible;

    public CaptureToolbarFeatureItem(
        CaptureToolbarFeature feature,
        string label,
        bool isVisible)
    {
        Feature = feature;
        Label = label;
        _isVisible = isVisible;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public CaptureToolbarFeature Feature { get; }

    public string Label { get; }

    public bool IsVisible
    {
        get => _isVisible;
        set
        {
            if (_isVisible == value)
            {
                return;
            }

            _isVisible = value;
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(IsVisible)));
        }
    }
}

public sealed class TranslationPriorityItem : INotifyPropertyChanged
{
    private bool _isAvailable;
    private string _availabilityReason;

    public TranslationPriorityItem(
        int position,
        TranslationProviderKind provider,
        string label,
        bool canMoveUp,
        bool canMoveDown,
        bool isAvailable = false,
        string availabilityReason = "正在检查可用状态")
    {
        Position = position;
        Provider = provider;
        Label = label;
        CanMoveUp = canMoveUp;
        CanMoveDown = canMoveDown;
        _isAvailable = isAvailable;
        _availabilityReason = availabilityReason;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public int Position { get; }

    public TranslationProviderKind Provider { get; }

    public string Label { get; }

    public bool CanMoveUp { get; }

    public bool CanMoveDown { get; }

    public bool IsAvailable
    {
        get => _isAvailable;
        private set
        {
            if (_isAvailable == value)
            {
                return;
            }

            _isAvailable = value;
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(IsAvailable)));
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(AvailabilityLabel)));
        }
    }

    public string AvailabilityLabel => IsAvailable ? "可用" : "不可用";

    public string AvailabilityReason
    {
        get => _availabilityReason;
        private set
        {
            if (_availabilityReason == value)
            {
                return;
            }

            _availabilityReason = value;
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(AvailabilityReason)));
        }
    }

    public void SetAvailability(bool isAvailable, string reason)
    {
        AvailabilityReason = reason;
        IsAvailable = isAvailable;
    }
}

public sealed class SettingsViewModel : INotifyPropertyChanged
{
    private AppSettings _baseSettings;
    private string _saveDirectory;
    private string _videoSaveDirectory;
    private bool _recordSystemAudio;
    private bool _recordMicrophone;
    private VideoRecordingCodec _videoRecordingCodec;
    private int _videoRecordingFrameRate;
    private bool _showKeyboardInputInRecording;
    private bool _showMouseInputInRecording;
    private bool _showTaskbarIcon;
    private bool _showNotificationIcon;
    private bool _showFloatingCaptureButton;
    private FloatingCaptureClickBehavior _floatingCaptureClickBehavior;
    private ScrollCaptureMode _scrollCaptureMode;
    private ArrowStyle _arrowStyle;
    private string _customStrokeColor;
    private int[] _customColorPalette;
    private bool _launchAtStartup;
    private WindowCloseBehavior _closeBehavior;
    private AppTheme _theme;
    private bool _keepHistory;
    private string _regionCaptureHotKey;
    private string _videoRecordingHotKey;
    private string _scrollCaptureHotKey;
    private string _ocrHotKey;
    private string _textTranslationHotKey;
    private string _pinHotKey;
    private string _openSettingsHotKey;
    private int _mouseLongPressMilliseconds;
    private bool _mouseSideButtonsUseLongPress;
    private string _ocrLanguageTag;
    private OcrEngineMode _ocrEngine;
    private string _translationProvider;
    private string _translationEndpoint;
    private string _translationTargetLanguage;
    private string _translationModel;
    private OfflineTranslationQuality _offlineTranslationQuality;
    private OfflineTranslationEngine _offlineTranslationEngine;
    private string _translationApiKey = string.Empty;
    private string _statusMessage = string.Empty;

    public SettingsViewModel(AppSettings settings)
    {
        _baseSettings = settings;
        _saveDirectory = settings.SaveDirectory;
        _videoSaveDirectory = settings.VideoSaveDirectory;
        _recordSystemAudio = settings.RecordSystemAudio;
        _recordMicrophone = settings.RecordMicrophone;
        _videoRecordingCodec = settings.VideoRecordingCodec;
        _videoRecordingFrameRate = settings.VideoRecordingFrameRate;
        _showKeyboardInputInRecording = settings.ShowKeyboardInputInRecording;
        _showMouseInputInRecording = settings.ShowMouseInputInRecording;
        _showTaskbarIcon = settings.ShowTaskbarIcon;
        _showNotificationIcon = settings.ShowNotificationIcon;
        _showFloatingCaptureButton = settings.ShowFloatingCaptureButton;
        _floatingCaptureClickBehavior = settings.FloatingCaptureClickBehavior;
        _scrollCaptureMode = settings.ScrollCaptureMode;
        _arrowStyle = settings.ArrowStyle;
        SetCaptureToolbarFeatures(settings.VisibleCaptureToolbarFeatures);
        _customStrokeColor = settings.CustomStrokeColor;
        _customColorPalette = settings.CustomColorPalette.ToArray();
        _launchAtStartup = settings.LaunchAtStartup;
        _closeBehavior = settings.CloseBehavior;
        _theme = settings.Theme;
        _keepHistory = settings.KeepHistory;
        _regionCaptureHotKey = settings.RegionCaptureHotKey;
        _videoRecordingHotKey = settings.VideoRecordingHotKey;
        _scrollCaptureHotKey = settings.ScrollCaptureHotKey;
        _ocrHotKey = settings.OcrHotKey;
        _textTranslationHotKey = settings.TextTranslationHotKey;
        _pinHotKey = settings.PinHotKey;
        _openSettingsHotKey = settings.OpenSettingsHotKey;
        _mouseLongPressMilliseconds = settings.MouseLongPressMilliseconds;
        _mouseSideButtonsUseLongPress = settings.MouseSideButtonsUseLongPress;
        _ocrLanguageTag = settings.OcrLanguageTag;
        _ocrEngine = settings.OcrEngine;
        _translationProvider = TranslationProviderFactory.ResolveProviderId(
            settings.TranslationProvider);
        _translationEndpoint = settings.TranslationEndpoint;
        var providerDefinition = TranslationProviderFactory.GetDefinition(
            _translationProvider);
        if (!string.IsNullOrWhiteSpace(providerDefinition.OfficialEndpoint) &&
            string.IsNullOrWhiteSpace(_translationEndpoint))
        {
            _translationEndpoint = providerDefinition.OfficialEndpoint;
        }
        _translationTargetLanguage = settings.TranslationTargetLanguage;
        _translationModel = TranslationProviderFactory.NormalizeModel(
            settings.TranslationEndpoint,
            settings.TranslationModel);
        _offlineTranslationQuality = settings.OfflineTranslationQuality;
        _offlineTranslationEngine = settings.OfflineTranslationEngine;
        SetTranslationProviderPriority(
            settings.ResolveTranslationProviderPriority());
        OcrLanguageOptions = CreateOcrLanguageOptions(settings.OcrLanguageTag);
        if (!string.IsNullOrWhiteSpace(_translationModel) &&
            !TranslationModelOptions.Contains(
                _translationModel,
                StringComparer.OrdinalIgnoreCase))
        {
            TranslationModelOptions.Insert(0, _translationModel);
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public IReadOnlyList<SettingOption> OcrLanguageOptions { get; }

    public IReadOnlyList<SettingOption> OcrEngineOptions { get; } =
    [
        new(nameof(OcrEngineMode.Windows), "Windows 系统识别（默认）"),
        new(nameof(OcrEngineMode.PaddleOcrV6), "PP-OCRv6 高质量识别"),
    ];

    public IReadOnlyList<SettingOption> TranslationProviderOptions { get; } =
        TranslationProviderFactory.ProviderDefinitions
            .Select(provider => new SettingOption(
                provider.Id,
                provider.DisplayName))
            .ToArray();

    public TranslationProviderFactory.ProviderDefinition SelectedTranslationProvider =>
        TranslationProviderFactory.GetDefinition(TranslationProvider);

    public ObservableCollection<TranslationPriorityItem>
        TranslationPriorityItems { get; } = [];

    public IReadOnlyList<SettingOption> TranslationTargetLanguageOptions { get; } =
        TranslationLanguageCatalog.Languages
            .Select(language => new SettingOption(
                language.Tag,
                $"{language.DisplayName}（{language.Tag}）"))
            .ToArray();

    public IReadOnlyList<SettingOption> OfflineTranslationTargetLanguageOptions { get; } =
        TranslationLanguageCatalog.OfflineTargetLanguages
            .Select(language => new SettingOption(
                language.Tag,
                $"{language.DisplayName}（{language.Tag}）"))
            .ToArray();

    public IReadOnlyList<SettingOption> OfflineTranslationQualityOptions { get; } =
    [
        new(nameof(OfflineTranslationQuality.Fast), "快速 · 最低延迟"),
        new(nameof(OfflineTranslationQuality.High), "高质量 · 推荐"),
        new(nameof(OfflineTranslationQuality.Ultra), "超高质量 · 更慢"),
    ];

    public IReadOnlyList<SettingOption> OfflineTranslationEngineOptions { get; } =
    [
        new(nameof(OfflineTranslationEngine.Mozilla), "Mozilla 轻量翻译（默认）"),
        new(nameof(OfflineTranslationEngine.QwenLargeModel), "Qwen 本机翻译大模型"),
    ];

    public ObservableCollection<string> TranslationModelOptions { get; } = new(
    [
        "deepseek-v4-flash",
        "deepseek-v4-pro",
        "gpt-4.1-mini",
        "gpt-4.1",
        "gpt-4o-mini",
    ]);

    public ObservableCollection<CaptureToolbarFeatureItem>
        CaptureToolbarFeatureItems { get; } = [];

    public string SaveDirectory
    {
        get => _saveDirectory;
        set => SetProperty(ref _saveDirectory, value);
    }

    public string VideoSaveDirectory
    {
        get => _videoSaveDirectory;
        set => SetProperty(ref _videoSaveDirectory, value);
    }

    public bool RecordSystemAudio
    {
        get => _recordSystemAudio;
        set => SetProperty(ref _recordSystemAudio, value);
    }

    public bool RecordMicrophone
    {
        get => _recordMicrophone;
        set => SetProperty(ref _recordMicrophone, value);
    }

    public VideoRecordingCodec VideoRecordingCodec
    {
        get => _videoRecordingCodec;
        set => SetProperty(ref _videoRecordingCodec, value);
    }

    public int VideoRecordingFrameRate
    {
        get => _videoRecordingFrameRate;
        set => SetProperty(ref _videoRecordingFrameRate, value);
    }

    public bool ShowKeyboardInputInRecording
    {
        get => _showKeyboardInputInRecording;
        set => SetProperty(ref _showKeyboardInputInRecording, value);
    }

    public bool ShowMouseInputInRecording
    {
        get => _showMouseInputInRecording;
        set => SetProperty(ref _showMouseInputInRecording, value);
    }

    public bool ShowTaskbarIcon
    {
        get => _showTaskbarIcon;
        set => SetProperty(ref _showTaskbarIcon, value);
    }

    public bool ShowNotificationIcon
    {
        get => _showNotificationIcon;
        set => SetProperty(ref _showNotificationIcon, value);
    }

    public bool ShowFloatingCaptureButton
    {
        get => _showFloatingCaptureButton;
        set => SetProperty(ref _showFloatingCaptureButton, value);
    }

    public FloatingCaptureClickBehavior FloatingCaptureClickBehavior
    {
        get => _floatingCaptureClickBehavior;
        set => SetProperty(ref _floatingCaptureClickBehavior, value);
    }

    public ScrollCaptureMode ScrollCaptureMode
    {
        get => _scrollCaptureMode;
        set => SetProperty(ref _scrollCaptureMode, value);
    }

    public ArrowStyle ArrowStyle
    {
        get => _arrowStyle;
        set => SetProperty(ref _arrowStyle, value);
    }

    public string CustomStrokeColor
    {
        get => _customStrokeColor;
        set => SetProperty(ref _customStrokeColor, value);
    }

    public int[] CustomColorPalette
    {
        get => _customColorPalette;
        set => SetProperty(ref _customColorPalette, value ?? []);
    }

    public bool LaunchAtStartup
    {
        get => _launchAtStartup;
        set => SetProperty(ref _launchAtStartup, value);
    }

    public WindowCloseBehavior CloseBehavior
    {
        get => _closeBehavior;
        set => SetProperty(ref _closeBehavior, value);
    }

    public AppTheme Theme
    {
        get => _theme;
        set => SetProperty(ref _theme, value);
    }

    public bool KeepHistory
    {
        get => _keepHistory;
        set => SetProperty(ref _keepHistory, value);
    }

    public string RegionCaptureHotKey
    {
        get => _regionCaptureHotKey;
        set => SetProperty(ref _regionCaptureHotKey, value);
    }

    public string VideoRecordingHotKey
    {
        get => _videoRecordingHotKey;
        set => SetProperty(ref _videoRecordingHotKey, value);
    }

    public string ScrollCaptureHotKey
    {
        get => _scrollCaptureHotKey;
        set => SetProperty(ref _scrollCaptureHotKey, value);
    }

    public string OcrHotKey
    {
        get => _ocrHotKey;
        set => SetProperty(ref _ocrHotKey, value);
    }

    public string TextTranslationHotKey
    {
        get => _textTranslationHotKey;
        set => SetProperty(ref _textTranslationHotKey, value);
    }

    public string PinHotKey
    {
        get => _pinHotKey;
        set => SetProperty(ref _pinHotKey, value);
    }

    public string OpenSettingsHotKey
    {
        get => _openSettingsHotKey;
        set => SetProperty(ref _openSettingsHotKey, value);
    }

    public int MouseLongPressMilliseconds
    {
        get => _mouseLongPressMilliseconds;
        set => SetProperty(ref _mouseLongPressMilliseconds, value);
    }

    public bool MouseSideButtonsUseLongPress
    {
        get => _mouseSideButtonsUseLongPress;
        set => SetProperty(ref _mouseSideButtonsUseLongPress, value);
    }

    public string OcrLanguageTag
    {
        get => _ocrLanguageTag;
        set => SetProperty(ref _ocrLanguageTag, value);
    }

    public OcrEngineMode OcrEngine
    {
        get => _ocrEngine;
        set => SetProperty(ref _ocrEngine, value);
    }

    public string TranslationProvider
    {
        get => _translationProvider;
        set => SetProperty(ref _translationProvider, value);
    }

    public string TranslationEndpoint
    {
        get => _translationEndpoint;
        set => SetProperty(ref _translationEndpoint, value);
    }

    public string TranslationTargetLanguage
    {
        get => _translationTargetLanguage;
        set => SetProperty(ref _translationTargetLanguage, value);
    }

    public string TranslationModel
    {
        get => _translationModel;
        set => SetProperty(ref _translationModel, value);
    }

    public OfflineTranslationQuality OfflineTranslationQuality
    {
        get => _offlineTranslationQuality;
        set => SetProperty(ref _offlineTranslationQuality, value);
    }

    public OfflineTranslationEngine OfflineTranslationEngine
    {
        get => _offlineTranslationEngine;
        set => SetProperty(ref _offlineTranslationEngine, value);
    }

    public string TranslationApiKey
    {
        get => _translationApiKey;
        set => SetProperty(ref _translationApiKey, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public AppSettings CreateSettings()
    {
        return _baseSettings with
        {
            SaveDirectory = SaveDirectory,
            VideoSaveDirectory = VideoSaveDirectory,
            RecordSystemAudio = RecordSystemAudio,
            RecordMicrophone = RecordMicrophone,
            VideoRecordingCodec = VideoRecordingCodec,
            VideoRecordingFrameRate = VideoRecordingFrameRate,
            ShowKeyboardInputInRecording = ShowKeyboardInputInRecording,
            ShowMouseInputInRecording = ShowMouseInputInRecording,
            ShowTaskbarIcon = ShowTaskbarIcon,
            ShowNotificationIcon = ShowNotificationIcon,
            ShowFloatingCaptureButton = ShowFloatingCaptureButton,
            FloatingCaptureClickBehavior = FloatingCaptureClickBehavior,
            ScrollCaptureMode = ScrollCaptureMode,
            ArrowStyle = ArrowStyle,
            VisibleCaptureToolbarFeatures = CaptureToolbarFeatureItems
                .Where(item => item.IsVisible)
                .Select(item => item.Feature)
                .ToArray(),
            CustomStrokeColor = CustomStrokeColor,
            CustomColorPalette = CustomColorPalette.ToArray(),
            LaunchAtStartup = LaunchAtStartup,
            CloseBehavior = CloseBehavior,
            Theme = Theme,
            KeepHistory = KeepHistory,
            RegionCaptureHotKey = RegionCaptureHotKey,
            VideoRecordingHotKey = VideoRecordingHotKey,
            ScrollCaptureHotKey = ScrollCaptureHotKey,
            OcrHotKey = OcrHotKey,
            TextTranslationHotKey = TextTranslationHotKey,
            PinHotKey = PinHotKey,
            OpenSettingsHotKey = OpenSettingsHotKey,
            MouseLongPressMilliseconds = MouseLongPressMilliseconds,
            MouseSideButtonsUseLongPress = MouseSideButtonsUseLongPress,
            OcrLanguageTag = OcrLanguageTag,
            OcrEngine = OcrEngine,
            TranslationMode = TranslationMode.Automatic,
            SendTextToOnlineTranslation = true,
            TranslationProviderPriority = TranslationPriorityItems
                .Select(item => item.Provider)
                .ToArray(),
            TranslationProvider = TranslationProvider,
            TranslationEndpoint = TranslationEndpoint,
            TranslationTargetLanguage = TranslationTargetLanguage,
            TranslationModel = TranslationModel,
            OfflineTranslationQuality = OfflineTranslationQuality,
            OfflineTranslationEngine = OfflineTranslationEngine,
        };
    }

    public void Apply(AppSettings settings)
    {
        _baseSettings = settings;
        SaveDirectory = settings.SaveDirectory;
        VideoSaveDirectory = settings.VideoSaveDirectory;
        RecordSystemAudio = settings.RecordSystemAudio;
        RecordMicrophone = settings.RecordMicrophone;
        VideoRecordingCodec = settings.VideoRecordingCodec;
        VideoRecordingFrameRate = settings.VideoRecordingFrameRate;
        ShowKeyboardInputInRecording = settings.ShowKeyboardInputInRecording;
        ShowMouseInputInRecording = settings.ShowMouseInputInRecording;
        ShowTaskbarIcon = settings.ShowTaskbarIcon;
        ShowNotificationIcon = settings.ShowNotificationIcon;
        ShowFloatingCaptureButton = settings.ShowFloatingCaptureButton;
        FloatingCaptureClickBehavior = settings.FloatingCaptureClickBehavior;
        ScrollCaptureMode = settings.ScrollCaptureMode;
        ArrowStyle = settings.ArrowStyle;
        SetCaptureToolbarFeatures(settings.VisibleCaptureToolbarFeatures);
        CustomStrokeColor = settings.CustomStrokeColor;
        CustomColorPalette = settings.CustomColorPalette.ToArray();
        LaunchAtStartup = settings.LaunchAtStartup;
        CloseBehavior = settings.CloseBehavior;
        Theme = settings.Theme;
        KeepHistory = settings.KeepHistory;
        RegionCaptureHotKey = settings.RegionCaptureHotKey;
        VideoRecordingHotKey = settings.VideoRecordingHotKey;
        ScrollCaptureHotKey = settings.ScrollCaptureHotKey;
        OcrHotKey = settings.OcrHotKey;
        TextTranslationHotKey = settings.TextTranslationHotKey;
        PinHotKey = settings.PinHotKey;
        OpenSettingsHotKey = settings.OpenSettingsHotKey;
        MouseLongPressMilliseconds = settings.MouseLongPressMilliseconds;
        MouseSideButtonsUseLongPress = settings.MouseSideButtonsUseLongPress;
        OcrLanguageTag = settings.OcrLanguageTag;
        OcrEngine = settings.OcrEngine;
        SetTranslationProviderPriority(
            settings.ResolveTranslationProviderPriority());
        TranslationProvider = TranslationProviderFactory.ResolveProviderId(
            settings.TranslationProvider);
        TranslationEndpoint = settings.TranslationEndpoint;
        TranslationTargetLanguage = settings.TranslationTargetLanguage;
        TranslationModel = TranslationProviderFactory.NormalizeModel(
            settings.TranslationEndpoint,
            settings.TranslationModel);
        OfflineTranslationQuality = settings.OfflineTranslationQuality;
        OfflineTranslationEngine = settings.OfflineTranslationEngine;
    }

    public void SetStatus(string message)
    {
        StatusMessage = message;
    }

    private void SetCaptureToolbarFeatures(
        IEnumerable<CaptureToolbarFeature>? visibleFeatures)
    {
        var visible = (visibleFeatures ?? []).ToHashSet();
        var labels = new Dictionary<CaptureToolbarFeature, string>
        {
            [CaptureToolbarFeature.Shape] = "矩形 / 椭圆",
            [CaptureToolbarFeature.Arrow] = "箭头",
            [CaptureToolbarFeature.Emoji] = "表情",
            [CaptureToolbarFeature.Brush] = "画笔",
            [CaptureToolbarFeature.Text] = "文字",
            [CaptureToolbarFeature.Mosaic] = "马赛克",
            [CaptureToolbarFeature.VideoRecording] = "录屏",
            [CaptureToolbarFeature.Save] = "保存图片",
            [CaptureToolbarFeature.ScrollCapture] = "长截图",
            [CaptureToolbarFeature.TextRecognition] = "文字识别",
            [CaptureToolbarFeature.CopyRecognizedText] = "文字识别并复制",
            [CaptureToolbarFeature.Translation] = "翻译",
            [CaptureToolbarFeature.PinImage] = "钉图",
            [CaptureToolbarFeature.UndoRedo] = "撤销 / 重做",
        };

        if (CaptureToolbarFeatureItems.Count == 0)
        {
            foreach (var feature in Enum.GetValues<CaptureToolbarFeature>())
            {
                CaptureToolbarFeatureItems.Add(new CaptureToolbarFeatureItem(
                    feature,
                    labels[feature],
                    visible.Contains(feature)));
            }

            return;
        }

        foreach (var item in CaptureToolbarFeatureItems)
        {
            item.IsVisible = visible.Contains(item.Feature);
        }
    }

    public void SetTranslationModels(IReadOnlyList<string> models)
    {
        ArgumentNullException.ThrowIfNull(models);
        var values = models
            .Where(model => !string.IsNullOrWhiteSpace(model))
            .Select(model => model.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(model => model, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (!string.IsNullOrWhiteSpace(TranslationModel) &&
            !values.Contains(
                TranslationModel,
                StringComparer.OrdinalIgnoreCase))
        {
            values.Insert(0, TranslationModel);
        }

        TranslationModelOptions.Clear();
        foreach (var model in values)
        {
            TranslationModelOptions.Add(model);
        }
    }

    public void UpdateTranslationProviderAvailability(
        TranslationProviderKind provider,
        bool isAvailable,
        string reason)
    {
        TranslationPriorityItems
            .FirstOrDefault(item => item.Provider == provider)?
            .SetAvailability(isAvailable, reason);
    }

    public bool MoveTranslationProvider(
        TranslationProviderKind provider,
        int offset)
    {
        var providers = TranslationPriorityItems
            .Select(item => item.Provider)
            .ToList();
        var currentIndex = providers.IndexOf(provider);
        var targetIndex = currentIndex + offset;
        if (currentIndex < 0 || targetIndex < 0 || targetIndex >= providers.Count)
        {
            return false;
        }

        (providers[currentIndex], providers[targetIndex]) =
            (providers[targetIndex], providers[currentIndex]);
        SetTranslationProviderPriority(providers);
        return true;
    }

    private void SetTranslationProviderPriority(
        IEnumerable<TranslationProviderKind> providers)
    {
        var availability = TranslationPriorityItems.ToDictionary(
            item => item.Provider,
            item => (item.IsAvailable, item.AvailabilityReason));
        var values = providers
            .Where(Enum.IsDefined)
            .Distinct()
            .ToList();
        foreach (var provider in Enum.GetValues<TranslationProviderKind>())
        {
            if (!values.Contains(provider))
            {
                values.Add(provider);
            }
        }

        TranslationPriorityItems.Clear();
        for (var index = 0; index < values.Count; index++)
        {
            var provider = values[index];
            availability.TryGetValue(provider, out var currentAvailability);
            TranslationPriorityItems.Add(new TranslationPriorityItem(
                index + 1,
                provider,
                provider == TranslationProviderKind.Online
                    ? "在线大模型"
                    : "本机离线模型",
                index > 0,
                index < values.Count - 1,
                currentAvailability.IsAvailable,
                currentAvailability.AvailabilityReason ?? "正在检查可用状态"));
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private void SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        OnPropertyChanged(propertyName);
    }

    private static SettingOption[] CreateOcrLanguageOptions(
        string configuredLanguageTag)
    {
        IReadOnlyList<string> availableLanguageTags;
        try
        {
            availableLanguageTags = OcrService.GetAvailableLanguageTags();
        }
        catch
        {
            availableLanguageTags = [];
        }

        var tags = availableLanguageTags
            .Concat([configuredLanguageTag])
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(tag => tag, StringComparer.OrdinalIgnoreCase);
        return tags
            .Select(tag => new SettingOption(tag, CreateLanguageLabel(tag)))
            .ToArray();
    }

    private static string CreateLanguageLabel(string languageTag)
    {
        try
        {
            var culture = CultureInfo.GetCultureInfo(languageTag);
            return $"{culture.NativeName}（{languageTag}）";
        }
        catch (CultureNotFoundException)
        {
            return languageTag;
        }
    }
}
