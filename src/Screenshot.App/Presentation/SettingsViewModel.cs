using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using Screenshot.App.Core;
using Screenshot.App.Text;
using Screenshot.App.Capture;

namespace Screenshot.App.Presentation;

public sealed record SettingOption(string Value, string Label, string Description = "");

public sealed class AiTranslationProfileItem : INotifyPropertyChanged
{
    private string _name;
    private bool _isEnabled;
    private string _provider;
    private string _endpoint;
    private string _model;
    private string _appId;
    private OnlineModelPurpose _purpose;
    private bool _isAvailable;
    private bool _hasAvailabilityResult;
    private bool _isAvailabilityChecking;
    private string _availabilityReason = "尚未检查可用状态";

    public AiTranslationProfileItem(AiTranslationProfile profile)
    {
        Id = profile.Id;
        _name = profile.Name;
        _isEnabled = profile.IsEnabled;
        _provider = profile.Provider;
        _endpoint = profile.Endpoint;
        _model = profile.Model;
        _appId = profile.AppId;
        _purpose = profile.Purpose;
        _isAvailable = profile.LastAvailability ?? false;
        _hasAvailabilityResult = profile.LastAvailability.HasValue;
        _availabilityReason = string.IsNullOrWhiteSpace(
                profile.LastAvailabilityReason)
            ? "尚未检查可用状态"
            : profile.LastAvailabilityReason;
    }

    public string Id { get; }
    public string Name { get => _name; set => Set(ref _name, value); }
    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (Set(ref _isEnabled, value))
            {
                OnPropertyChanged(nameof(AvailabilityLabel));
            }
        }
    }
    public string Provider
    {
        get => _provider;
        set
        {
            if (Set(ref _provider, value))
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ProviderDisplayName)));
        }
    }
    public string ProviderDisplayName =>
        TranslationProviderFactory.GetDefinition(Provider).DisplayName;
    public string Endpoint { get => _endpoint; set => Set(ref _endpoint, value); }
    public string Model { get => _model; set => Set(ref _model, value); }
    public string AppId { get => _appId; set => Set(ref _appId, value); }
    public OnlineModelPurpose Purpose { get => _purpose; set => Set(ref _purpose, value); }

    public bool IsAvailable
    {
        get => _isAvailable;
        private set
        {
            if (Set(ref _isAvailable, value))
            {
                OnPropertyChanged(nameof(AvailabilityLabel));
            }
        }
    }

    public bool IsAvailabilityChecking
    {
        get => _isAvailabilityChecking;
        private set => Set(ref _isAvailabilityChecking, value);
    }

    public string AvailabilityReason
    {
        get => _availabilityReason;
        private set => Set(ref _availabilityReason, value);
    }

    public string AvailabilityLabel =>
        !IsEnabled ? "未启用" :
        !_hasAvailabilityResult ? "未检测" :
        IsAvailable ? "可用" : "不可用";

    public void SetAvailabilityChecking()
    {
        // Keep the last completed result visible while the next request runs.
        // Changing the status to "checking" here makes a known-good profile
        // flash as unavailable whenever the settings page is opened.
        IsAvailabilityChecking = true;
    }

    public void SetAvailabilityUnverified(string reason)
    {
        IsAvailabilityChecking = false; _hasAvailabilityResult = false; AvailabilityReason = reason;
        OnPropertyChanged(nameof(AvailabilityLabel));
    }

    public void SetAvailability(bool isAvailable, string reason)
    {
        var previousLabel = AvailabilityLabel;
        IsAvailabilityChecking = false;
        // Set() already suppresses notifications when a value is unchanged,
        // so repeated checks with the same result do not redraw the row.
        AvailabilityReason = reason;
        _hasAvailabilityResult = true;
        IsAvailable = isAvailable;
        if (previousLabel != AvailabilityLabel &&
            !IsAvailable)
        {
            // IsAvailable only raises the label notification when its boolean
            // value changes. The first completed false result needs one as
            // well, because it transitions from "未检测" to "不可用".
            OnPropertyChanged(nameof(AvailabilityLabel));
        }
    }

    public AiTranslationProfile ToProfile() => new()
    {
        Id = Id,
        Name = string.IsNullOrWhiteSpace(Name) ? "在线翻译" : Name.Trim(),
        IsEnabled = IsEnabled,
        Provider = Provider?.Trim() ?? string.Empty,
        Endpoint = Endpoint?.Trim() ?? string.Empty,
        Model = Model?.Trim() ?? string.Empty,
        AppId = AppId?.Trim() ?? string.Empty,
        Purpose = Purpose,
        LastAvailability = _hasAvailabilityResult ? IsAvailable : null,
        LastAvailabilityReason = AvailabilityReason ?? string.Empty,
    };

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name!);
        return true;
    }
}

public enum CaptureToolbarFeatureGroup
{
    Annotation,
    Action,
    History,
}

public sealed class CaptureToolbarFeatureItem : INotifyPropertyChanged
{
    private bool _isVisible;
    private string _shortcut = string.Empty;

    public CaptureToolbarFeatureItem(
        CaptureToolbarFeature feature,
        string label,
        string glyph,
        CaptureToolbarFeatureGroup group,
        bool isVisible)
    {
        Feature = feature;
        Label = label;
        Glyph = glyph;
        Group = group;
        _isVisible = isVisible;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Shortcut
    {
        get => _shortcut;
        set
        {
            var normalized = value?.Trim() ?? string.Empty;
            normalized = CaptureToolbarShortcut.TryNormalize(normalized, out var formatted)
                ? formatted
                : string.Empty;
            if (_shortcut == normalized) return;
            _shortcut = normalized;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Shortcut)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasShortcut)));
        }
    }

    /// <summary>Whether this feature has a configured toolbar shortcut.</summary>
    public bool HasShortcut => !string.IsNullOrWhiteSpace(Shortcut);

    public CaptureToolbarFeature Feature { get; }

    public string Label { get; }

    public string Glyph { get; }

    public CaptureToolbarFeatureGroup Group { get; }

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

    public int Position { get; private set; }

    public TranslationProviderKind Provider { get; }

    public string Label { get; }

    public bool CanMoveUp { get; private set; }

    public bool CanMoveDown { get; private set; }

    internal void UpdatePosition(int position, int count)
    {
        Position = position;
        CanMoveUp = position > 1;
        CanMoveDown = position < count;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Position)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanMoveUp)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanMoveDown)));
    }

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
    private PngSaveLocationMode _pngSaveLocationMode;
    private bool _autoSaveOnComplete;
    private int _screenshotScalePercent;
    private CaptureAspectRatio _captureAspectRatio;
    private bool _recordSystemAudio;
    private bool _recordMicrophone;
    private string? _microphoneDeviceId;
    private string? _cameraDeviceId;
    private VideoRecordingCodec _videoRecordingCodec;
    private int _videoRecordingFrameRate;
    private VideoRecordingOutputFormat _recordingOutputFormat;
    private double _captureToolbarPositionXRatio;
    private double _captureToolbarPositionYRatio;
    private bool _showKeyboardInputInRecording;
    private bool _showMouseInputInRecording;
    private bool _showMouseTrailInRecording;
    private bool _showCameraInRecording;
    private bool _showTaskbarIcon;
    private bool _showNotificationIcon;
    private bool _showFloatingCaptureButton;
    private FloatingCaptureClickBehavior _floatingCaptureClickBehavior;
    private ScrollCaptureMode _scrollCaptureMode;
    private ArrowStyle _arrowStyle;
    private ArrowToolMode _arrowToolMode;
    private ShapeToolMode _shapeToolMode;
    private AnnotationToolMode _lastAnnotationTool;
    private CaptureToolbarRowCount _captureToolbarRows;
    private double _toolbarScalePercent;
    private string _customStrokeColor;
    private int[] _customColorPalette;
    private AnnotationToolSetting[] _annotationToolSettings;
    private int _defaultStrokeWidth;
    private bool _launchAtStartup;
    private bool _openSettingsOnStartup;
    private bool _requestAdministratorPrivileges;
    private WindowCloseBehavior _closeBehavior;
    private AppTheme _theme;
    private bool _keepAllScreenshotHistory;
    private bool _keepAllVideoHistory;
    private int _screenshotHistoryRetentionDaysBeforeKeepAll;
    private int _videoHistoryRetentionDaysBeforeKeepAll;
    private string _screenshotHistoryRetentionDaysText;
    private string _videoHistoryRetentionDaysText;
    private string _screenshotHistoryLimitText;
    private string _videoHistoryLimitText;
    private string _regionCaptureHotKey;
    private string _completeCaptureHotKey;
    private string _videoRecordingHotKey;
    private string _endVideoRecordingHotKey;
    private string _scrollCaptureHotKey;
    private string _ocrHotKey;
    private string _textTranslationHotKey;
    private string _realtimeTranslationHotKey;
    private RealtimeTranslationSettings _realtimeTranslation;
    private string _pinHotKey;
    private string _openSettingsHotKey;
    private int _mouseLongPressMilliseconds;
    private bool _mouseSideButtonsUseLongPress;
    private string _ocrLanguageTag;
    private OcrEngineMode _ocrEngine;
    private bool _autoOcrAfterCapture;
    private bool _enableImageQuestion;
    private string _imageQuestionHotKey;
    private RecognitionResultPresentationMode _recognitionResultPresentation;
    private string _translationProvider;
    private string _translationEndpoint;
    private string _translationTargetLanguage;
    private string _interfaceLanguage = "zh-Hans";
    private string _translationModel;
    private string _selectedTranslationProfileId = string.Empty;
    private OfflineTranslationQuality _offlineTranslationQuality;
    private OfflineTranslationEngine _offlineTranslationEngine;
    private string _translationApiKey = string.Empty;
    private string _statusMessage = string.Empty;

    public SettingsViewModel(AppSettings settings)
    {
        PublicTranslationProfiles = new(_publicTranslationProfiles);
        OnlineTranslationProfiles = new(_onlineTranslationProfiles);
        TranslationProfiles.CollectionChanged += (_, _) => RefreshTranslationProfileViews();
        _baseSettings = settings;
        _saveDirectory = settings.SaveDirectory;
        _videoSaveDirectory = settings.VideoSaveDirectory;
        _pngSaveLocationMode = settings.PngSaveLocationMode;
        _autoSaveOnComplete = settings.AutoSaveOnComplete;
        _screenshotScalePercent = settings.ScreenshotScalePercent;
        _captureAspectRatio = settings.CaptureAspectRatio;
        _recordSystemAudio = settings.RecordSystemAudio;
        _recordMicrophone = settings.RecordMicrophone;
        _microphoneDeviceId = settings.MicrophoneDeviceId;
        _cameraDeviceId = settings.CameraDeviceId;
        _videoRecordingCodec = settings.VideoRecordingCodec;
        _videoRecordingFrameRate = settings.VideoRecordingFrameRate;
        _recordingOutputFormat = settings.RecordingOutputFormat;
        _captureToolbarPositionXRatio = settings.CaptureToolbarPositionXRatio;
        _captureToolbarPositionYRatio = settings.CaptureToolbarPositionYRatio;
        _showKeyboardInputInRecording = settings.ShowKeyboardInputInRecording;
        _showMouseInputInRecording = settings.ShowMouseInputInRecording;
        _showMouseTrailInRecording = settings.ShowMouseTrailInRecording;
        _showCameraInRecording = settings.ShowCameraInRecording;
        _showTaskbarIcon = settings.ShowTaskbarIcon;
        _showNotificationIcon = settings.ShowNotificationIcon;
        _showFloatingCaptureButton = settings.ShowFloatingCaptureButton;
        _floatingCaptureClickBehavior = settings.FloatingCaptureClickBehavior;
        _scrollCaptureMode = settings.ScrollCaptureMode;
        _arrowStyle = settings.ArrowStyle;
        _arrowToolMode = settings.ArrowToolMode;
        _shapeToolMode = settings.ShapeToolMode;
        _lastAnnotationTool = settings.LastAnnotationTool;
        _captureToolbarRows = settings.CaptureToolbarRows;
        _toolbarScalePercent = settings.ToolbarScalePercent;
        SetCaptureToolbarFeatures(
            settings.VisibleCaptureToolbarFeatures,
            settings.CaptureToolbarFeatureOrder,
            settings.CaptureToolbarFeatureShortcuts);
        _customStrokeColor = settings.CustomStrokeColor;
        _customColorPalette = settings.CustomColorPalette.ToArray();
        _annotationToolSettings = settings.AnnotationToolSettings.ToArray();
        _defaultStrokeWidth = settings.DefaultStrokeWidth;
        _launchAtStartup = settings.LaunchAtStartup;
        _openSettingsOnStartup = settings.OpenSettingsOnStartup;
        _requestAdministratorPrivileges = settings.RequestAdministratorPrivileges;
        _closeBehavior = settings.CloseBehavior;
        _theme = settings.Theme;
        _screenshotHistoryRetentionDaysBeforeKeepAll = NormalizeDisplayedRetentionDays(
            settings.ScreenshotHistoryRetentionDays,
            AppSettings.CreateDefault().ScreenshotHistoryRetentionDays);
        _videoHistoryRetentionDaysBeforeKeepAll = NormalizeDisplayedRetentionDays(
            settings.VideoHistoryRetentionDays,
            AppSettings.CreateDefault().VideoHistoryRetentionDays);
        _keepAllScreenshotHistory = settings.ScreenshotHistoryRetentionDays == 0;
        _keepAllVideoHistory = settings.VideoHistoryRetentionDays == 0;
        _screenshotHistoryRetentionDaysText = _screenshotHistoryRetentionDaysBeforeKeepAll
            .ToString(CultureInfo.InvariantCulture);
        _videoHistoryRetentionDaysText = _videoHistoryRetentionDaysBeforeKeepAll
            .ToString(CultureInfo.InvariantCulture);
        _screenshotHistoryLimitText = settings.HistoryLimit.ToString(
            CultureInfo.InvariantCulture);
        _videoHistoryLimitText = settings.VideoHistoryLimit.ToString(
            CultureInfo.InvariantCulture);
        _regionCaptureHotKey = settings.RegionCaptureHotKey;
        _completeCaptureHotKey = settings.CompleteCaptureHotKey;
        _videoRecordingHotKey = settings.VideoRecordingHotKey;
        _endVideoRecordingHotKey = settings.EndVideoRecordingHotKey;
        _scrollCaptureHotKey = settings.ScrollCaptureHotKey;
        _ocrHotKey = settings.OcrHotKey;
        _textTranslationHotKey = settings.TextTranslationHotKey;
        _imageQuestionHotKey = settings.ImageQuestionHotKey;
        _realtimeTranslationHotKey = settings.RealtimeTranslationHotKey;
        _realtimeTranslation = settings.RealtimeTranslation;
        _pinHotKey = settings.PinHotKey;
        _openSettingsHotKey = settings.OpenSettingsHotKey;
        _mouseLongPressMilliseconds = settings.MouseLongPressMilliseconds;
        _mouseSideButtonsUseLongPress = settings.MouseSideButtonsUseLongPress;
        _ocrLanguageTag = settings.OcrLanguageTag;
        _ocrEngine = settings.OcrEngine;
        _autoOcrAfterCapture = settings.AutoOcrAfterCapture;
        _enableImageQuestion = settings.EnableImageQuestion;
        Watermark = settings.Watermark;
        _recognitionResultPresentation = settings.RecognitionResultPresentation;
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
        _interfaceLanguage = settings.InterfaceLanguage;
        _translationModel = TranslationProviderFactory.NormalizeModel(
            settings.TranslationEndpoint,
            settings.TranslationModel);
        SetTranslationProfiles(settings);
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
        new(nameof(OcrEngineMode.QwenVisualOcr), "Qwen3-VL 4B 视觉识别"),
    ];

    public IReadOnlyList<SettingOption> RecognitionResultPresentationOptions { get; } =
    [
        new(nameof(RecognitionResultPresentationMode.Overlay), "覆盖原图（当前方式）"),
        new(nameof(RecognitionResultPresentationMode.Popup), "独立弹窗（右下角）"),
    ];

    public IReadOnlyList<SettingOption> PngSaveLocationModeOptions { get; } =
    [
        new(nameof(PngSaveLocationMode.DefaultDirectory), "默认位置（截图保存目录）"),
        new(nameof(PngSaveLocationMode.AskEveryTime), "每次保存时选择目录"),
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

    public ObservableCollection<AiTranslationProfileItem>
        TranslationProfiles { get; } = [];

    public string SelectedTranslationProfileId
    {
        get => _selectedTranslationProfileId;
        set => SetProperty(ref _selectedTranslationProfileId, value);
    }

    public AiTranslationProfileItem? SelectedTranslationProfile =>
        TranslationProfiles.FirstOrDefault(profile =>
            string.Equals(
                profile.Id,
                SelectedTranslationProfileId,
                StringComparison.OrdinalIgnoreCase));

    public void UpdateSelectedTranslationProfileConnection(
        string provider,
        string endpoint,
        string model)
    {
        var profile = SelectedTranslationProfile;
        if (profile is null)
        {
            return;
        }

        profile.Provider = provider?.Trim() ?? string.Empty;
        profile.Endpoint = endpoint?.Trim() ?? string.Empty;
        profile.Model = model?.Trim() ?? string.Empty;
    }

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
        new(nameof(OfflineTranslationEngine.QwenLargeModel), "TranslateGemma 离线多语言模型"),
        new(nameof(OfflineTranslationEngine.HunyuanLightweight), "Hy-MT2 1.8B · 轻量多语言模型"),
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

    // Shortcut assignment is intentionally limited to annotation tools. The
    // action/history buttons below the toolbar remain configurable internally
    // but are not exposed as individual shortcut fields in this page.
    public IEnumerable<CaptureToolbarFeatureItem> CaptureToolbarAnnotationFeatureItems =>
        CaptureToolbarFeatureItems.Where(item => item.Group == CaptureToolbarFeatureGroup.Annotation);

    public CaptureToolbarRowCount CaptureToolbarRows
    {
        get => _captureToolbarRows;
        set => SetProperty(ref _captureToolbarRows, value);
    }

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

    public PngSaveLocationMode PngSaveLocationMode
    {
        get => _pngSaveLocationMode;
        set => SetProperty(ref _pngSaveLocationMode, value);
    }

    public bool AutoSaveOnComplete
    {
        get => _autoSaveOnComplete;
        set => SetProperty(ref _autoSaveOnComplete, value);
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

    public string? MicrophoneDeviceId
    {
        get => _microphoneDeviceId;
        set => SetProperty(ref _microphoneDeviceId, value);
    }

    public string? CameraDeviceId
    {
        get => _cameraDeviceId;
        set => SetProperty(ref _cameraDeviceId, value);
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

    public VideoRecordingOutputFormat RecordingOutputFormat
    {
        get => _recordingOutputFormat;
        set => SetProperty(ref _recordingOutputFormat, value);
    }

    public double CaptureToolbarPositionXRatio
    {
        get => _captureToolbarPositionXRatio;
        set => SetProperty(ref _captureToolbarPositionXRatio, value);
    }

    public double CaptureToolbarPositionYRatio
    {
        get => _captureToolbarPositionYRatio;
        set => SetProperty(ref _captureToolbarPositionYRatio, value);
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

    public bool ShowMouseTrailInRecording
    {
        get => _showMouseTrailInRecording;
        set => SetProperty(ref _showMouseTrailInRecording, value);
    }

    public bool ShowCameraInRecording
    {
        get => _showCameraInRecording;
        set => SetProperty(ref _showCameraInRecording, value);
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

    public ArrowToolMode ArrowToolMode
    {
        get => _arrowToolMode;
        set => SetProperty(ref _arrowToolMode, value);
    }

    public ShapeToolMode ShapeToolMode
    {
        get => _shapeToolMode;
        set => SetProperty(ref _shapeToolMode, value);
    }

    public AnnotationToolMode LastAnnotationTool
    {
        get => _lastAnnotationTool;
        set => SetProperty(ref _lastAnnotationTool, value);
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

    public int DefaultStrokeWidth
    {
        get => _defaultStrokeWidth;
        set => SetProperty(ref _defaultStrokeWidth, Math.Clamp(value, 1, 24));
    }

    public bool LaunchAtStartup
    {
        get => _launchAtStartup;
        set => SetProperty(ref _launchAtStartup, value);
    }

    public bool RequestAdministratorPrivileges
    {
        get => _requestAdministratorPrivileges;
        set => SetProperty(ref _requestAdministratorPrivileges, value);
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

    public string InterfaceLanguage
    {
        get => _interfaceLanguage;
        set => SetProperty(ref _interfaceLanguage, UiLocalization.Normalize(value));
    }

    public bool KeepAllScreenshotHistory
    {
        get => _keepAllScreenshotHistory;
        set
        {
            if (_keepAllScreenshotHistory == value)
            {
                return;
            }

            if (value)
            {
                _screenshotHistoryRetentionDaysBeforeKeepAll = ParseDisplayedRetentionDays(
                    ScreenshotHistoryRetentionDaysText,
                    _screenshotHistoryRetentionDaysBeforeKeepAll);
            }
            else
            {
                ScreenshotHistoryRetentionDaysText = _screenshotHistoryRetentionDaysBeforeKeepAll
                    .ToString(CultureInfo.InvariantCulture);
            }

            SetProperty(ref _keepAllScreenshotHistory, value);
        }
    }

    public bool KeepAllVideoHistory
    {
        get => _keepAllVideoHistory;
        set
        {
            if (_keepAllVideoHistory == value)
            {
                return;
            }

            if (value)
            {
                _videoHistoryRetentionDaysBeforeKeepAll = ParseDisplayedRetentionDays(
                    VideoHistoryRetentionDaysText,
                    _videoHistoryRetentionDaysBeforeKeepAll);
            }
            else
            {
                VideoHistoryRetentionDaysText = _videoHistoryRetentionDaysBeforeKeepAll
                    .ToString(CultureInfo.InvariantCulture);
            }

            SetProperty(ref _keepAllVideoHistory, value);
        }
    }

    public string ScreenshotHistoryRetentionDaysText
    {
        get => _screenshotHistoryRetentionDaysText;
        set => SetProperty(ref _screenshotHistoryRetentionDaysText, value);
    }

    public string VideoHistoryRetentionDaysText
    {
        get => _videoHistoryRetentionDaysText;
        set => SetProperty(ref _videoHistoryRetentionDaysText, value);
    }

    public string ScreenshotHistoryLimitText
    {
        get => _screenshotHistoryLimitText;
        set => SetProperty(ref _screenshotHistoryLimitText, value);
    }

    public string VideoHistoryLimitText
    {
        get => _videoHistoryLimitText;
        set => SetProperty(ref _videoHistoryLimitText, value);
    }

    public string RegionCaptureHotKey
    {
        get => _regionCaptureHotKey;
        set => SetProperty(ref _regionCaptureHotKey, value);
    }

    public string CompleteCaptureHotKey
    {
        get => _completeCaptureHotKey;
        set => SetProperty(ref _completeCaptureHotKey, value);
    }

    public string VideoRecordingHotKey
    {
        get => _videoRecordingHotKey;
        set => SetProperty(ref _videoRecordingHotKey, value);
    }

    public int ScreenshotScalePercent
    {
        get => _screenshotScalePercent;
        set => SetProperty(ref _screenshotScalePercent, Math.Clamp(value, 25, 200));
    }

    public CaptureAspectRatio CaptureAspectRatio
    {
        get => _captureAspectRatio;
        set => SetProperty(ref _captureAspectRatio, value);
    }

    public IReadOnlyList<SettingOption> CaptureAspectRatioOptions { get; } =
        Enum.GetValues<CaptureAspectRatio>()
            .Select(ratio => new SettingOption(ratio.ToString(), CaptureAspectRatioHelper.GetLabel(ratio)))
            .ToArray();

    public IReadOnlyList<SettingOption> ThemeOptions { get; } =
    [
        new(nameof(AppTheme.AuroraMist), "青瓷月桂", "浅色 · 青瓷绿与月桂米"),
        new(nameof(AppTheme.CoralSky), "晚桃晴山", "浅色 · 晚桃粉与暮山蓝"),
        new(nameof(AppTheme.GinkgoPaper), "雾紫燕麦", "浅色 · 哑光灰紫与暖燕麦"),
        new(nameof(AppTheme.ForestNight), "墨青豆沙", "深色 · 墨青灰与豆沙粉"),
        new(nameof(AppTheme.ObsidianGold), "暮山晚桃", "深色 · 暮山蓝与晚桃粉"),
        new(nameof(AppTheme.NeonDeep), "雾紫夜幕", "深色 · 哑光烟紫与暖灰"),
        new(nameof(AppTheme.GlassMist), "清透琉璃", "清透玻璃 · 真实透底、中性色泽与边缘反光"),
        new(nameof(AppTheme.GlassInk), "墨湾流光", "深色 · 自绘蓝紫柔光、玻璃弧面与指针高光；比普通主题有额外图形开销"),
    ];

    public AnnotationToolSetting[] AnnotationToolSettings
    {
        get => _annotationToolSettings;
        set => SetProperty(ref _annotationToolSettings, value?.ToArray() ?? []);
    }

    public double ToolbarScalePercent
    {
        get => _toolbarScalePercent;
        set => SetProperty(ref _toolbarScalePercent, value);
    }

    public bool OpenSettingsOnStartup
    {
        get => _openSettingsOnStartup;
        set => SetProperty(ref _openSettingsOnStartup, value);
    }

    public string EndVideoRecordingHotKey
    {
        get => _endVideoRecordingHotKey;
        set => SetProperty(ref _endVideoRecordingHotKey, value);
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

    public string ImageQuestionHotKey
    {
        get => _imageQuestionHotKey;
        set => SetProperty(ref _imageQuestionHotKey, value);
    }

    public string TextTranslationHotKey
    {
        get => _textTranslationHotKey;
        set => SetProperty(ref _textTranslationHotKey, value);
    }

    public string RealtimeTranslationHotKey
    {
        get => _realtimeTranslationHotKey;
        set => SetProperty(ref _realtimeTranslationHotKey, value);
    }

    public RealtimeTranslationSettings RealtimeTranslation
    {
        get => _realtimeTranslation;
        set => SetProperty(ref _realtimeTranslation, value);
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

    public bool AutoOcrAfterCapture
    {
        get => _autoOcrAfterCapture;
        set => SetProperty(ref _autoOcrAfterCapture, value);
    }

    public bool EnableImageQuestion
    {
        get => _enableImageQuestion;
        set => SetProperty(ref _enableImageQuestion, value);
    }

    public RecognitionResultPresentationMode RecognitionResultPresentation
    {
        get => _recognitionResultPresentation;
        set => SetProperty(ref _recognitionResultPresentation, value);
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

    public WatermarkSettings Watermark { get; set; } = new();

    public AppSettings CreateSettings()
    {
        var profiles = TranslationProfiles.Select(item => item.ToProfile()).ToArray();
        var selected = profiles.FirstOrDefault(profile =>
            string.Equals(profile.Id, SelectedTranslationProfileId, StringComparison.OrdinalIgnoreCase))
            ?? profiles.FirstOrDefault(profile => profile.IsEnabled)
            ?? profiles.FirstOrDefault();
        return _baseSettings with
        {
            Watermark = Watermark,
            SaveDirectory = SaveDirectory,
            VideoSaveDirectory = VideoSaveDirectory,
            PngSaveLocationMode = PngSaveLocationMode,
            AutoSaveOnComplete = AutoSaveOnComplete,
            ScreenshotScalePercent = ScreenshotScalePercent,
            CaptureAspectRatio = CaptureAspectRatio,
            RecordSystemAudio = RecordSystemAudio,
            RecordMicrophone = RecordMicrophone,
            MicrophoneDeviceId = MicrophoneDeviceId,
            CameraDeviceId = CameraDeviceId,
            VideoRecordingCodec = VideoRecordingCodec,
            VideoRecordingFrameRate = VideoRecordingFrameRate,
            RecordingOutputFormat = RecordingOutputFormat,
            CaptureToolbarPositionXRatio = CaptureToolbarPositionXRatio,
            CaptureToolbarPositionYRatio = CaptureToolbarPositionYRatio,
            ShowKeyboardInputInRecording = ShowKeyboardInputInRecording,
            ShowMouseInputInRecording = ShowMouseInputInRecording,
            ShowMouseTrailInRecording = ShowMouseTrailInRecording,
            ShowCameraInRecording = ShowCameraInRecording,
            ShowTaskbarIcon = ShowTaskbarIcon,
            ShowNotificationIcon = ShowNotificationIcon,
            ShowFloatingCaptureButton = ShowFloatingCaptureButton,
            FloatingCaptureClickBehavior = FloatingCaptureClickBehavior,
            ScrollCaptureMode = ScrollCaptureMode,
            ArrowStyle = ArrowStyle,
            ArrowToolMode = ArrowToolMode,
            ShapeToolMode = ShapeToolMode,
            LastAnnotationTool = LastAnnotationTool,
            VisibleCaptureToolbarFeatures = CaptureToolbarFeatureItems
                .Where(item => item.IsVisible)
                .Select(item => item.Feature)
                .ToArray(),
            CaptureToolbarFeatureOrder = CaptureToolbarFeatureItems
                .Select(item => item.Feature)
                .ToArray(),
            CaptureToolbarFeatureShortcuts = CaptureToolbarFeatureItems
                .Where(item => !string.IsNullOrWhiteSpace(item.Shortcut))
                .ToDictionary(item => item.Feature, item => item.Shortcut),
            CaptureToolbarRows = CaptureToolbarRows,
            ToolbarScalePercent = ToolbarScalePercent,
            CustomStrokeColor = CustomStrokeColor,
            CustomColorPalette = CustomColorPalette.ToArray(),
            AnnotationToolSettings = AnnotationToolSettings.ToArray(),
            DefaultStrokeWidth = DefaultStrokeWidth,
            LaunchAtStartup = LaunchAtStartup,
            OpenSettingsOnStartup = OpenSettingsOnStartup,
            RequestAdministratorPrivileges = RequestAdministratorPrivileges,
            CloseBehavior = CloseBehavior,
            Theme = Theme,
            InterfaceLanguage = InterfaceLanguage,
            HistoryLimit = ParseHistoryLimit(
                ScreenshotHistoryLimitText,
                _baseSettings.HistoryLimit),
            VideoHistoryLimit = ParseHistoryLimit(
                VideoHistoryLimitText,
                _baseSettings.VideoHistoryLimit),
            ScreenshotHistoryRetentionDays = KeepAllScreenshotHistory
                ? 0
                : ParseRetentionDays(
                    ScreenshotHistoryRetentionDaysText,
                    _baseSettings.ScreenshotHistoryRetentionDays),
            VideoHistoryRetentionDays = KeepAllVideoHistory
                ? 0
                : ParseRetentionDays(
                    VideoHistoryRetentionDaysText,
                    _baseSettings.VideoHistoryRetentionDays),
            RegionCaptureHotKey = RegionCaptureHotKey,
            CompleteCaptureHotKey = CompleteCaptureHotKey,
            VideoRecordingHotKey = VideoRecordingHotKey,
            EndVideoRecordingHotKey = EndVideoRecordingHotKey,
            ScrollCaptureHotKey = ScrollCaptureHotKey,
            OcrHotKey = OcrHotKey,
            TextTranslationHotKey = TextTranslationHotKey,
            ImageQuestionHotKey = ImageQuestionHotKey,
            RealtimeTranslationHotKey = RealtimeTranslationHotKey,
            RealtimeTranslation = RealtimeTranslation,
            PinHotKey = PinHotKey,
            OpenSettingsHotKey = OpenSettingsHotKey,
            MouseLongPressMilliseconds = MouseLongPressMilliseconds,
            MouseSideButtonsUseLongPress = MouseSideButtonsUseLongPress,
            OcrLanguageTag = OcrLanguageTag,
            OcrEngine = OcrEngine,
            AutoOcrAfterCapture = AutoOcrAfterCapture,
            EnableImageQuestion = EnableImageQuestion,
            RecognitionResultPresentation = RecognitionResultPresentation,
            TranslationMode = TranslationMode.Automatic,
            SendTextToOnlineTranslation = true,
            TranslationProviderPriority = TranslationPriorityItems
                .Select(item => item.Provider)
                .ToArray(),
            TranslationProvider = selected?.Provider ?? TranslationProvider,
            TranslationEndpoint = selected?.Endpoint ?? TranslationEndpoint,
            TranslationTargetLanguage = TranslationTargetLanguage,
            TranslationModel = selected?.Model ?? TranslationModel,
            TranslationProfiles = profiles,
            OfflineTranslationQuality = OfflineTranslationQuality,
            OfflineTranslationEngine = OfflineTranslationEngine,
        };
    }

    public void Apply(AppSettings settings)
    {
        _baseSettings = settings;
        SaveDirectory = settings.SaveDirectory;
        VideoSaveDirectory = settings.VideoSaveDirectory;
        PngSaveLocationMode = settings.PngSaveLocationMode;
        AutoSaveOnComplete = settings.AutoSaveOnComplete;
        ScreenshotScalePercent = settings.ScreenshotScalePercent;
        CaptureAspectRatio = settings.CaptureAspectRatio;
        RecordSystemAudio = settings.RecordSystemAudio;
        RecordMicrophone = settings.RecordMicrophone;
        MicrophoneDeviceId = settings.MicrophoneDeviceId;
        CameraDeviceId = settings.CameraDeviceId;
        VideoRecordingCodec = settings.VideoRecordingCodec;
        VideoRecordingFrameRate = settings.VideoRecordingFrameRate;
        RecordingOutputFormat = settings.RecordingOutputFormat;
        CaptureToolbarPositionXRatio = settings.CaptureToolbarPositionXRatio;
        CaptureToolbarPositionYRatio = settings.CaptureToolbarPositionYRatio;
        ShowKeyboardInputInRecording = settings.ShowKeyboardInputInRecording;
        ShowMouseInputInRecording = settings.ShowMouseInputInRecording;
        ShowMouseTrailInRecording = settings.ShowMouseTrailInRecording;
        ShowCameraInRecording = settings.ShowCameraInRecording;
        ShowTaskbarIcon = settings.ShowTaskbarIcon;
        ShowNotificationIcon = settings.ShowNotificationIcon;
        ShowFloatingCaptureButton = settings.ShowFloatingCaptureButton;
        FloatingCaptureClickBehavior = settings.FloatingCaptureClickBehavior;
        ScrollCaptureMode = settings.ScrollCaptureMode;
        ArrowStyle = settings.ArrowStyle;
        ArrowToolMode = settings.ArrowToolMode;
        ShapeToolMode = settings.ShapeToolMode;
        LastAnnotationTool = settings.LastAnnotationTool;
        CaptureToolbarRows = settings.CaptureToolbarRows;
        ToolbarScalePercent = settings.ToolbarScalePercent;
        SetCaptureToolbarFeatures(
            settings.VisibleCaptureToolbarFeatures,
            settings.CaptureToolbarFeatureOrder,
            settings.CaptureToolbarFeatureShortcuts);
        CustomStrokeColor = settings.CustomStrokeColor;
        CustomColorPalette = settings.CustomColorPalette.ToArray();
        AnnotationToolSettings = settings.AnnotationToolSettings.ToArray();
        DefaultStrokeWidth = settings.DefaultStrokeWidth;
        LaunchAtStartup = settings.LaunchAtStartup;
        OpenSettingsOnStartup = settings.OpenSettingsOnStartup;
        RequestAdministratorPrivileges = settings.RequestAdministratorPrivileges;
        CloseBehavior = settings.CloseBehavior;
        Theme = settings.Theme;
        InterfaceLanguage = settings.InterfaceLanguage;
        if (settings.ScreenshotHistoryRetentionDays > 0)
        {
            _screenshotHistoryRetentionDaysBeforeKeepAll = settings.ScreenshotHistoryRetentionDays;
            ScreenshotHistoryRetentionDaysText = settings.ScreenshotHistoryRetentionDays
                .ToString(CultureInfo.InvariantCulture);
        }
        KeepAllScreenshotHistory = settings.ScreenshotHistoryRetentionDays == 0;
        if (settings.VideoHistoryRetentionDays > 0)
        {
            _videoHistoryRetentionDaysBeforeKeepAll = settings.VideoHistoryRetentionDays;
            VideoHistoryRetentionDaysText = settings.VideoHistoryRetentionDays
                .ToString(CultureInfo.InvariantCulture);
        }
        KeepAllVideoHistory = settings.VideoHistoryRetentionDays == 0;
        ScreenshotHistoryLimitText = settings.HistoryLimit.ToString(
            CultureInfo.InvariantCulture);
        VideoHistoryLimitText = settings.VideoHistoryLimit.ToString(
            CultureInfo.InvariantCulture);
        RegionCaptureHotKey = settings.RegionCaptureHotKey;
        CompleteCaptureHotKey = settings.CompleteCaptureHotKey;
        VideoRecordingHotKey = settings.VideoRecordingHotKey;
        EndVideoRecordingHotKey = settings.EndVideoRecordingHotKey;
        ScrollCaptureHotKey = settings.ScrollCaptureHotKey;
        OcrHotKey = settings.OcrHotKey;
        TextTranslationHotKey = settings.TextTranslationHotKey;
        ImageQuestionHotKey = settings.ImageQuestionHotKey;
        RealtimeTranslationHotKey = settings.RealtimeTranslationHotKey;
        RealtimeTranslation = settings.RealtimeTranslation;
        PinHotKey = settings.PinHotKey;
        OpenSettingsHotKey = settings.OpenSettingsHotKey;
        MouseLongPressMilliseconds = settings.MouseLongPressMilliseconds;
        MouseSideButtonsUseLongPress = settings.MouseSideButtonsUseLongPress;
        OcrLanguageTag = settings.OcrLanguageTag;
        OcrEngine = settings.OcrEngine;
        AutoOcrAfterCapture = settings.AutoOcrAfterCapture;
        EnableImageQuestion = settings.EnableImageQuestion;
        Watermark = settings.Watermark;
        RecognitionResultPresentation = settings.RecognitionResultPresentation;
        SetTranslationProviderPriority(
            settings.ResolveTranslationProviderPriority());
        TranslationProvider = TranslationProviderFactory.ResolveProviderId(
            settings.TranslationProvider);
        TranslationEndpoint = settings.TranslationEndpoint;
        TranslationTargetLanguage = settings.TranslationTargetLanguage;
        TranslationModel = TranslationProviderFactory.NormalizeModel(
            settings.TranslationEndpoint,
            settings.TranslationModel);
        SetTranslationProfiles(settings);
        OfflineTranslationQuality = settings.OfflineTranslationQuality;
        OfflineTranslationEngine = settings.OfflineTranslationEngine;
    }

    public void SetStatus(string message)
    {
        StatusMessage = message;
    }

    public bool MoveCaptureToolbarFeature(
        CaptureToolbarFeature source,
        CaptureToolbarFeature target,
        bool insertAfter = false)
    {
        var sourceItem = CaptureToolbarFeatureItems.FirstOrDefault(
            item => item.Feature == source);
        var targetItem = CaptureToolbarFeatureItems.FirstOrDefault(
            item => item.Feature == target);
        if (sourceItem is null || targetItem is null ||
            sourceItem.Group != targetItem.Group ||
            ReferenceEquals(sourceItem, targetItem))
        {
            return false;
        }

        var sourceIndex = CaptureToolbarFeatureItems.IndexOf(sourceItem);
        var targetIndex = CaptureToolbarFeatureItems.IndexOf(targetItem);
        var insertionIndex = targetIndex + (insertAfter ? 1 : 0);
        if (sourceIndex < insertionIndex)
        {
            insertionIndex--;
        }

        if (sourceIndex == insertionIndex)
        {
            return false;
        }

        CaptureToolbarFeatureItems.Move(sourceIndex, insertionIndex);
        return true;
    }

    private void SetCaptureToolbarFeatures(
        IEnumerable<CaptureToolbarFeature>? visibleFeatures,
        IEnumerable<CaptureToolbarFeature>? orderedFeatures,
        IReadOnlyDictionary<CaptureToolbarFeature, string>? shortcuts = null)
    {
        var visible = (visibleFeatures ?? []).ToHashSet();
        var metadata = new Dictionary<
            CaptureToolbarFeature,
            (string Label, string Glyph, CaptureToolbarFeatureGroup Group)>
        {
            [CaptureToolbarFeature.Shape] = ("矩形 / 椭圆", "□", CaptureToolbarFeatureGroup.Annotation),
            [CaptureToolbarFeature.DoubleArrow] = ("双向箭头", "⇌", CaptureToolbarFeatureGroup.Annotation),
            [CaptureToolbarFeature.Arrow] = ("箭头", "→", CaptureToolbarFeatureGroup.Annotation),
            [CaptureToolbarFeature.Emoji] = ("表情", "😊", CaptureToolbarFeatureGroup.Annotation),
            [CaptureToolbarFeature.Number] = ("序号", "1", CaptureToolbarFeatureGroup.Annotation),
            [CaptureToolbarFeature.Line] = ("直线/波浪线", "／", CaptureToolbarFeatureGroup.Annotation),
            [CaptureToolbarFeature.Brush] = ("画笔", "✎", CaptureToolbarFeatureGroup.Annotation),
            [CaptureToolbarFeature.Text] = ("文字", "T", CaptureToolbarFeatureGroup.Annotation),
            [CaptureToolbarFeature.Mosaic] = ("马赛克", "▦", CaptureToolbarFeatureGroup.Annotation),
            [CaptureToolbarFeature.VideoRecording] = ("录屏", "●", CaptureToolbarFeatureGroup.Action),
            [CaptureToolbarFeature.AspectRatio] = ("普通截图比例", "▭", CaptureToolbarFeatureGroup.Action),
            [CaptureToolbarFeature.Save] = ("保存图片", "▣", CaptureToolbarFeatureGroup.Action),
            [CaptureToolbarFeature.ScrollCapture] = ("长截图", "↕", CaptureToolbarFeatureGroup.Action),
            [CaptureToolbarFeature.TextRecognition] = ("文字识别", "文", CaptureToolbarFeatureGroup.Action),
            [CaptureToolbarFeature.CopyTable] = ("表格复制", "表", CaptureToolbarFeatureGroup.Action),
            [CaptureToolbarFeature.CopyRecognizedText] = ("文字识别并复制", "取", CaptureToolbarFeatureGroup.Action),
            [CaptureToolbarFeature.Translation] = ("翻译", "译", CaptureToolbarFeatureGroup.Action),
            [CaptureToolbarFeature.PrivacyRedaction] = ("一键隐私打码", "隐", CaptureToolbarFeatureGroup.Action),
            [CaptureToolbarFeature.PinImage] = ("钉图", "⌖", CaptureToolbarFeatureGroup.Action),
            [CaptureToolbarFeature.Watermark] = ("水印", "印", CaptureToolbarFeatureGroup.Action),
            [CaptureToolbarFeature.UndoRedo] = ("撤销 / 重做", "↶", CaptureToolbarFeatureGroup.History),
        };

        var order = (orderedFeatures ?? Enum.GetValues<CaptureToolbarFeature>())
            .Where(Enum.IsDefined)
            .Distinct()
            .ToList();
        foreach (var feature in Enum.GetValues<CaptureToolbarFeature>())
        {
            if (!order.Contains(feature))
            {
                order.Add(feature);
            }
        }

        if (CaptureToolbarFeatureItems.Count == 0)
        {
            foreach (var feature in order)
            {
                var (label, glyph, group) = metadata[feature];
                var item = new CaptureToolbarFeatureItem(
                    feature,
                    label,
                    glyph,
                    group,
                    visible.Contains(feature));
                if (shortcuts is not null && shortcuts.TryGetValue(feature, out var shortcut))
                {
                    item.Shortcut = shortcut;
                }
                CaptureToolbarFeatureItems.Add(item);
            }

            return;
        }

        foreach (var item in CaptureToolbarFeatureItems)
        {
            item.IsVisible = visible.Contains(item.Feature);
            if (shortcuts is not null && shortcuts.TryGetValue(item.Feature, out var shortcut))
            {
                item.Shortcut = shortcut;
            }
        }

        for (var targetIndex = 0; targetIndex < order.Count; targetIndex++)
        {
            var item = CaptureToolbarFeatureItems.First(
                candidate => candidate.Feature == order[targetIndex]);
            var currentIndex = CaptureToolbarFeatureItems.IndexOf(item);
            if (currentIndex != targetIndex)
            {
                CaptureToolbarFeatureItems.Move(currentIndex, targetIndex);
            }
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

    private readonly ObservableCollection<AiTranslationProfileItem> _publicTranslationProfiles = [];
    private readonly ObservableCollection<AiTranslationProfileItem> _onlineTranslationProfiles = [];
    public ReadOnlyObservableCollection<AiTranslationProfileItem> PublicTranslationProfiles { get; }
    public ReadOnlyObservableCollection<AiTranslationProfileItem> OnlineTranslationProfiles { get; }
    internal void RefreshTranslationProfileViews()
    {
        SynchronizeProfileView(_publicTranslationProfiles, isPublic: true);
        SynchronizeProfileView(_onlineTranslationProfiles, isPublic: false);
    }

    private void SynchronizeProfileView(ObservableCollection<AiTranslationProfileItem> view, bool isPublic)
    {
        var expected = TranslationProfiles.Where(item =>
            TranslationProviderFactory.IsPublicProvider(item.Provider) == isPublic).ToArray();
        for (var index = view.Count - 1; index >= 0; index--)
            if (!expected.Contains(view[index])) view.RemoveAt(index);
        for (var index = 0; index < expected.Length; index++)
        {
            var current = view.IndexOf(expected[index]);
            if (current < 0) view.Insert(index, expected[index]);
            else if (current != index) view.Move(current, index);
        }
    }

    private void SetTranslationProfiles(AppSettings settings)
    {
        var existingItems = TranslationProfiles.ToDictionary(
            item => item.Id,
            StringComparer.OrdinalIgnoreCase);
        var profiles = (settings.TranslationProfiles ?? [])
            .Select(profile => existingItems.TryGetValue(profile.Id, out var existing) &&
                               Equals(existing.ToProfile(), profile)
                ? existing
                : new AiTranslationProfileItem(profile))
            .ToArray();
        if (profiles.Length == 0)
        {
            profiles =
            [
                new AiTranslationProfileItem(new AiTranslationProfile
                {
                    Name = "在线翻译",
                    Provider = TranslationProvider,
                    Endpoint = TranslationEndpoint,
                    Model = TranslationModel,
                }),
            ];
        }
        if (!TranslationProfiles.SequenceEqual(profiles))
        {
            TranslationProfiles.Clear();
            foreach (var profile in profiles) TranslationProfiles.Add(profile);
        }
        if (!profiles.Any(profile => string.Equals(
                profile.Id,
                SelectedTranslationProfileId,
                StringComparison.OrdinalIgnoreCase)))
        {
            SelectedTranslationProfileId = profiles[0].Id;
        }
    }

    public AiTranslationProfileItem AddTranslationProfile()
    {
        var item = CreateTranslationProfileDraft();
        CommitTranslationProfile(item);
        return item;
    }

    internal AiTranslationProfileItem CreateTranslationProfileDraft() =>
        new(new AiTranslationProfile
        {
            Name = $"翻译配置 {TranslationProfiles.Count + 1}",
            Provider = TranslationProviderFactory.OpenAiCompatibleProviderId,
        });

    internal void CommitTranslationProfile(AiTranslationProfileItem item)
    {
        if (TranslationProfiles.Contains(item)) return;
        TranslationProfiles.Add(item);
        SelectedTranslationProfileId = item.Id;
    }

    public bool RemoveTranslationProfile(AiTranslationProfileItem item)
    {
        if (TranslationProfiles.Count <= 1 || !TranslationProfiles.Remove(item)) return false;
        if (string.Equals(SelectedTranslationProfileId, item.Id, StringComparison.OrdinalIgnoreCase))
            SelectedTranslationProfileId = TranslationProfiles[0].Id;
        return true;
    }

    public bool MoveTranslationProfile(AiTranslationProfileItem item, int offset)
    {
        var index = TranslationProfiles.IndexOf(item);
        var target = index + offset;
        if (index < 0 || target < 0 || target >= TranslationProfiles.Count) return false;
        TranslationProfiles.Move(index, target);
        return true;
    }

    public bool MoveTranslationProfileTo(AiTranslationProfileItem item, int target)
    {
        var current = TranslationProfiles.IndexOf(item);
        if (current < 0 || target < 0 || target >= TranslationProfiles.Count ||
            current == target)
        {
            return false;
        }

        TranslationProfiles.Move(current, target);
        return true;
    }

    public bool MoveTranslationProfileToInsertionIndex(
        AiTranslationProfileItem item,
        int insertionIndex)
    {
        var current = TranslationProfiles.IndexOf(item);
        if (current < 0 || insertionIndex < 0 ||
            insertionIndex > TranslationProfiles.Count)
        {
            return false;
        }

        // The drop position is calculated before the dragged row is removed.
        // Moving a row downward therefore shifts the final index by one.
        var target = insertionIndex > current
            ? insertionIndex - 1
            : insertionIndex;
        if (target == current)
        {
            return false;
        }

        TranslationProfiles.Move(current, target);
        return true;
    }

    public void ClearTranslationModels()
    {
        TranslationModelOptions.Clear();
        TranslationModel = string.Empty;
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

    public bool MoveTranslationProviderToInsertionIndex(TranslationPriorityItem item, int insertionIndex)
    {
        var current = TranslationPriorityItems.IndexOf(item);
        if (current < 0 || insertionIndex < 0 || insertionIndex > TranslationPriorityItems.Count) return false;
        var target = insertionIndex > current ? insertionIndex - 1 : insertionIndex;
        if (target == current) return false;
        TranslationPriorityItems.Move(current, target);
        for (var index = 0; index < TranslationPriorityItems.Count; index++)
            TranslationPriorityItems[index].UpdatePosition(index + 1, TranslationPriorityItems.Count);
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

        if (TranslationPriorityItems.Select(item => item.Provider).SequenceEqual(values)) return;
        TranslationPriorityItems.Clear();
        for (var index = 0; index < values.Count; index++)
        {
            var provider = values[index];
            availability.TryGetValue(provider, out var currentAvailability);
            TranslationPriorityItems.Add(new TranslationPriorityItem(
                index + 1,
                provider,
                provider switch { TranslationProviderKind.Online => "在线大模型", TranslationProviderKind.Public => "公共翻译", _ => "本机离线模型" },
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

    private static int ParseRetentionDays(string text, int fallback)
    {
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? Math.Clamp(value, 1, 3650)
            : Math.Max(1, fallback);
    }

    private static int NormalizeDisplayedRetentionDays(int days, int fallback)
    {
        return days > 0 ? days : Math.Max(1, fallback);
    }

    private static int ParseDisplayedRetentionDays(string text, int fallback)
    {
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? Math.Clamp(value, 1, 3650)
            : Math.Clamp(fallback, 1, 3650);
    }

    private static int ParseHistoryLimit(string text, int fallback)
    {
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? Math.Clamp(value, 1, AppSettings.MaximumHistoryItems)
            : Math.Clamp(fallback, 1, AppSettings.MaximumHistoryItems);
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
