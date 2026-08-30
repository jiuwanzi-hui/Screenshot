using System.IO;

namespace Screenshot.App.Core;

public enum AppTheme
{
    // Legacy values remain readable so existing settings migrate cleanly.
    System,
    Light,
    Dark,
    AuroraMist,
    CoralSky,
    GinkgoPaper,
    ForestNight,
    ObsidianGold,
    NeonDeep,
}

public enum WindowCloseBehavior
{
    MinimizeToBackground,
    ExitApplication,
}

public enum FloatingCaptureClickBehavior
{
    CaptureImmediately,
    ShowSelection,
    RegionCapture,
    VideoRecording,
    ScrollCapture,
    PinCapture,
    CaptureAllScreens,
}

public enum ScrollCaptureMode
{
    Automatic,
    ManualWheel,
}

public enum PngSaveLocationMode
{
    DefaultDirectory,
    AskEveryTime,
}

public enum ArrowStyle
{
    Filled,
    Hollow,
}

public enum ArrowToolMode
{
    Straight,
    Curved,
}

public enum ShapeToolMode
{
    Rectangle,
    Ellipse,
}

public enum AnnotationToolMode
{
    Rectangle,
    Ellipse,
    StraightArrow,
    CurvedArrow,
    Emoji,
    Number,
    Brush,
    Mosaic,
    Text,
}

public enum CaptureToolbarFeature
{
    Shape,
    Arrow,
    Emoji,
    Number,
    Brush,
    Text,
    Mosaic,
    VideoRecording,
    Save,
    ScrollCapture,
    TextRecognition,
    CopyTable,
    CopyRecognizedText,
    Translation,
    PrivacyRedaction,
    PinImage,
    UndoRedo,
}

public enum CaptureToolbarRowCount
{
    One = 1,
    Two = 2,
}

public enum VideoRecordingCodec
{
    H264,
    H265,
}

public enum VideoRecordingOutputFormat
{
    Mp4,
    Gif,
}

public readonly record struct VideoRecordingPreferences(
    VideoRecordingCodec Codec,
    int FrameRate,
    bool RecordSystemAudio,
    bool RecordMicrophone,
    bool ShowKeyboardInput = false,
    bool ShowMouseInput = false,
    bool ShowMouseTrail = false,
    VideoRecordingOutputFormat OutputFormat = VideoRecordingOutputFormat.Mp4,
    bool ShowCamera = false,
    string? MicrophoneDeviceId = null,
    string? CameraDeviceId = null);

public readonly record struct VideoRecordingAnnotationPreferences(
    ShapeToolMode ShapeToolMode,
    ArrowToolMode ArrowToolMode,
    ArrowStyle ArrowStyle,
    string StrokeColor,
    int StrokeWidth);

public readonly record struct AnnotationToolSetting(
    string Tool,
    string Color,
    double StrokeWidth);

public enum TranslationMode
{
    Disabled,
    Online,
    Offline,
    Automatic,
}

public enum TranslationProviderKind
{
    Online,
    Offline,
}

public enum OfflineTranslationQuality
{
    High,
    Ultra,
    Fast,
}

public enum OcrEngineMode
{
    Windows,
    PaddleOcrV6,
}

public enum RecognitionResultPresentationMode
{
    Overlay,
    Popup,
}

public enum OfflineTranslationEngine
{
    Mozilla,
    QwenLargeModel,
}

/// <summary>
/// A named online AI translation configuration. Secrets are kept in the
/// encrypted credential store and referenced by Id rather than serialized here.
/// </summary>
public sealed record AiTranslationProfile
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; init; } = "在线翻译";
    public bool IsEnabled { get; init; } = true;
    public string Provider { get; init; } = "OpenAICompatible";
    public string Endpoint { get; init; } = string.Empty;
    public string Model { get; init; } = string.Empty;

    // Cached UI state only.  The service is still checked in the background
    // on startup; these fields keep the last known result visible until that
    // check completes instead of flashing every profile as unavailable.
    public bool? LastAvailability { get; init; }
    public string LastAvailabilityReason { get; init; } = string.Empty;
}

public sealed record AppSettings
{
    public const string DefaultCompleteCaptureHotKey = "Ctrl+C";
    public const string DefaultEndVideoRecordingHotKey = "Alt+2";

    public const int MaximumHistoryItems = 100;

    // Version 14 changes the default end-recording shortcut. Previous
    // configurations that still contain the old generated default are
    // migrated, while an explicitly customized shortcut is preserved.
    // Version 13 adds a durable "last annotation tool" preference. Previous
    // configurations already remember arrow/shape variants, so they can be
    // upgraded without resetting the toolbar to its default rectangle.
    public int SettingsVersion { get; init; } = 15;

    public string SaveDirectory { get; init; } = GetDefaultSaveDirectory();

    public string VideoSaveDirectory { get; init; } = GetDefaultVideoSaveDirectory();

    public PngSaveLocationMode PngSaveLocationMode { get; init; } =
        PngSaveLocationMode.DefaultDirectory;

    /// <summary>
    /// Output scale for saved/copied screenshots. 100% keeps the physical
    /// screen pixels; lower values reduce output dimensions and file size.
    /// </summary>
    public int ScreenshotScalePercent { get; init; } = 100;

    public bool RecordSystemAudio { get; init; } = true;

    public bool RecordMicrophone { get; init; }

    public string? MicrophoneDeviceId { get; init; }

    public string? CameraDeviceId { get; init; }

    public VideoRecordingCodec VideoRecordingCodec { get; init; } =
        VideoRecordingCodec.H264;

    public int VideoRecordingFrameRate { get; init; } = 30;

    public VideoRecordingOutputFormat RecordingOutputFormat { get; init; } =
        VideoRecordingOutputFormat.Mp4;

    public double CaptureToolbarPositionXRatio { get; init; } = -1;

    public double CaptureToolbarPositionYRatio { get; init; } = -1;

    public bool ShowKeyboardInputInRecording { get; init; }

    public bool ShowMouseInputInRecording { get; init; }

    public bool ShowMouseTrailInRecording { get; init; }

    public bool ShowCameraInRecording { get; init; }

    public bool ShowTaskbarIcon { get; init; } = true;

    public bool ShowNotificationIcon { get; init; } = true;

    public bool ShowFloatingCaptureButton { get; init; }

    public FloatingCaptureClickBehavior FloatingCaptureClickBehavior { get; init; } =
        FloatingCaptureClickBehavior.ShowSelection;

    public ScrollCaptureMode ScrollCaptureMode { get; init; } =
        ScrollCaptureMode.Automatic;

    public ArrowStyle ArrowStyle { get; init; } = ArrowStyle.Filled;

    public ArrowToolMode ArrowToolMode { get; init; } = ArrowToolMode.Straight;

    public ShapeToolMode ShapeToolMode { get; init; } = ShapeToolMode.Rectangle;

    public AnnotationToolMode LastAnnotationTool { get; init; } =
        AnnotationToolMode.Rectangle;

    public CaptureToolbarFeature[] VisibleCaptureToolbarFeatures { get; init; } =
        Enum.GetValues<CaptureToolbarFeature>();

    public CaptureToolbarFeature[] CaptureToolbarFeatureOrder { get; init; } =
        Enum.GetValues<CaptureToolbarFeature>();

    public CaptureToolbarRowCount CaptureToolbarRows { get; init; } =
        CaptureToolbarRowCount.One;

    public double ToolbarScalePercent { get; init; } = 100;

    public bool LaunchAtStartup { get; init; }

    /// <summary>
    /// When disabled, every normal launch stays in the tray and the settings
    /// window can be opened from the tray menu or its hotkey.
    /// </summary>
    public bool OpenSettingsOnStartup { get; init; }

    /// <summary>
    /// Requests UAC at the next app launch, including a Windows startup launch.
    /// A declined request deliberately falls back to the normal process token.
    /// </summary>
    // New installations and configurations written by older versions request
    // elevation by default; an explicit user opt-out remains false.
    public bool RequestAdministratorPrivileges { get; init; } = true;

    public WindowCloseBehavior CloseBehavior { get; init; } =
        WindowCloseBehavior.MinimizeToBackground;

    public AppTheme Theme { get; init; } = AppTheme.AuroraMist;

    public string RegionCaptureHotKey { get; init; } = "Ctrl+Alt+S";

    /// <summary>
    /// Optional shortcut that confirms an active region capture after a valid
    /// selection has been made. It intentionally has no default binding.
    /// </summary>
    public string CompleteCaptureHotKey { get; init; } = DefaultCompleteCaptureHotKey;

    public string VideoRecordingHotKey { get; init; } = string.Empty;

    public string EndVideoRecordingHotKey { get; init; } =
        DefaultEndVideoRecordingHotKey;

    public string ScrollCaptureHotKey { get; init; } = string.Empty;

    public string OcrHotKey { get; init; } = "Ctrl+Alt+O";

    public string TextTranslationHotKey { get; init; } = string.Empty;

    public string PinHotKey { get; init; } = "Ctrl+Alt+P";

    public string OpenSettingsHotKey { get; init; } = "Ctrl+Alt+Comma";

    public int MouseLongPressMilliseconds { get; init; } = 700;

    public bool MouseSideButtonsUseLongPress { get; init; }

    public string DefaultSaveFormat { get; init; } = "Png";

    public int DefaultFontSize { get; init; } = 16;

    public string DefaultStrokeColor { get; init; } = "#007F73";

    /// <summary>
    /// The last color picked through the "custom color" swatch in the capture
    /// toolbar or the editor. Empty until the user picks one; kept across
    /// sessions so the swatch does not reset on every new capture.
    /// </summary>
    public string CustomStrokeColor { get; init; } = string.Empty;

    /// <summary>
    /// Win32 COLORREF values shown in the custom-color slots of the system
    /// color picker. Stored separately from the active custom swatch.
    /// </summary>
    public int[] CustomColorPalette { get; init; } = [];

    public int DefaultStrokeWidth { get; init; } = 3;

    /// <summary>
    /// Last color and size selected for each annotation tool. The tool name is
    /// stored as text so the settings file remains independent of editor UI
    /// types and older files can omit the property safely.
    /// </summary>
    public AnnotationToolSetting[] AnnotationToolSettings { get; init; } = [];

    public string OcrLanguageTag { get; init; } = "zh-Hans";

    public OcrEngineMode OcrEngine { get; init; } = OcrEngineMode.Windows;

    public RecognitionResultPresentationMode RecognitionResultPresentation { get; init; } =
        RecognitionResultPresentationMode.Overlay;

    public string TranslationProvider { get; init; } = "OpenAICompatible";

    public string TranslationEndpoint { get; init; } = string.Empty;

    public string TranslationTargetLanguage { get; init; } = "zh-Hans";

    public string TranslationModel { get; init; } = "gpt-4.1-mini";

    /// <summary>Named online configurations, ordered by preference.</summary>
    public AiTranslationProfile[] TranslationProfiles { get; init; } = [];

    public TranslationMode TranslationMode { get; init; } =
        TranslationMode.Automatic;

    public TranslationProviderKind[] TranslationProviderPriority { get; init; } =
    [
        TranslationProviderKind.Online,
        TranslationProviderKind.Offline,
    ];

    public OfflineTranslationQuality OfflineTranslationQuality { get; init; } =
        OfflineTranslationQuality.High;

    public OfflineTranslationEngine OfflineTranslationEngine { get; init; } =
        OfflineTranslationEngine.Mozilla;

    public int HistoryLimit { get; init; } = 50;

    public int VideoHistoryLimit { get; init; } = 50;

    /// <summary>
    /// Number of days to keep persisted screenshot history. Zero disables
    /// age-based deletion; the configured item limit still applies.
    /// </summary>
    public int ScreenshotHistoryRetentionDays { get; init; } = 7;

    /// <summary>
    /// Number of days to keep recorded videos. Zero disables age-based
    /// deletion; the configured item limit still applies.
    /// </summary>
    public int VideoHistoryRetentionDays { get; init; } = 7;

    public bool SendTextToOnlineTranslation { get; init; }

    public static AppSettings CreateDefault()
    {
        return new AppSettings();
    }

    public IReadOnlyList<TranslationProviderKind> ResolveTranslationProviderPriority()
    {
        if (SettingsVersion < 3)
        {
            return TranslationMode == TranslationMode.Offline
                ? [TranslationProviderKind.Offline, TranslationProviderKind.Online]
                : [TranslationProviderKind.Online, TranslationProviderKind.Offline];
        }

        var providers = (TranslationProviderPriority ?? [])
            .Where(Enum.IsDefined)
            .Distinct()
            .ToList();
        foreach (var provider in Enum.GetValues<TranslationProviderKind>())
        {
            if (!providers.Contains(provider))
            {
                providers.Add(provider);
            }
        }

        return providers;
    }

    public AppSettings Normalize()
    {
        var defaults = CreateDefault();
        var requiresHistoryRetentionMigration = SettingsVersion < 11;
        var hasCaptureToolbarPosition =
            double.IsFinite(CaptureToolbarPositionXRatio) &&
            double.IsFinite(CaptureToolbarPositionYRatio) &&
            CaptureToolbarPositionXRatio is >= 0 and <= 1 &&
            CaptureToolbarPositionYRatio is >= 0 and <= 1;
        // Translation is invoked explicitly by the user. Keep the legacy mode
        // only as a migration hint for which provider should be tried first.
        var translationProviderPriority = ResolveTranslationProviderPriority();

        var visibleCaptureToolbarFeatures =
            (VisibleCaptureToolbarFeatures ??
                defaults.VisibleCaptureToolbarFeatures)
            .Where(Enum.IsDefined)
            .Distinct()
            .ToList();
        if (SettingsVersion < 6 &&
            visibleCaptureToolbarFeatures.Contains(
                CaptureToolbarFeature.TextRecognition) &&
            !visibleCaptureToolbarFeatures.Contains(
                CaptureToolbarFeature.CopyRecognizedText))
        {
            visibleCaptureToolbarFeatures.Add(
                CaptureToolbarFeature.CopyRecognizedText);
        }
        if (SettingsVersion < 7 &&
            !visibleCaptureToolbarFeatures.Contains(CaptureToolbarFeature.Number))
        {
            visibleCaptureToolbarFeatures.Add(CaptureToolbarFeature.Number);
        }
        if (SettingsVersion < 8 &&
            !visibleCaptureToolbarFeatures.Contains(
                CaptureToolbarFeature.PrivacyRedaction))
        {
            visibleCaptureToolbarFeatures.Add(
                CaptureToolbarFeature.PrivacyRedaction);
        }
        if (SettingsVersion < 15 &&
            visibleCaptureToolbarFeatures.Contains(
                CaptureToolbarFeature.TextRecognition) &&
            !visibleCaptureToolbarFeatures.Contains(CaptureToolbarFeature.CopyTable))
        {
            visibleCaptureToolbarFeatures.Add(CaptureToolbarFeature.CopyTable);
        }

        var captureToolbarFeatureOrder =
            (CaptureToolbarFeatureOrder ?? defaults.CaptureToolbarFeatureOrder)
            .Where(Enum.IsDefined)
            .Distinct()
            .ToList();
        if (SettingsVersion < 15 &&
            !captureToolbarFeatureOrder.Contains(CaptureToolbarFeature.CopyTable))
        {
            var textRecognitionIndex = captureToolbarFeatureOrder.IndexOf(
                CaptureToolbarFeature.TextRecognition);
            captureToolbarFeatureOrder.Insert(
                textRecognitionIndex >= 0
                    ? textRecognitionIndex + 1
                    : captureToolbarFeatureOrder.Count,
                CaptureToolbarFeature.CopyTable);
        }
        foreach (var feature in Enum.GetValues<CaptureToolbarFeature>())
        {
            if (!captureToolbarFeatureOrder.Contains(feature))
            {
                captureToolbarFeatureOrder.Add(feature);
            }
        }

        var migrateLegacyEndRecordingShortcut = SettingsVersion < 14 &&
            string.Equals(
                EndVideoRecordingHotKey,
                "Ctrl+Alt+E",
                StringComparison.OrdinalIgnoreCase);

        var profiles = (TranslationProfiles ?? [])
            .Where(profile => profile is not null)
            .Select(profile => profile with
            {
                Id = string.IsNullOrWhiteSpace(profile.Id)
                    ? Guid.NewGuid().ToString("N")
                    : profile.Id.Trim(),
                Name = string.IsNullOrWhiteSpace(profile.Name)
                    ? "在线翻译"
                    : profile.Name.Trim(),
                Provider = string.IsNullOrWhiteSpace(profile.Provider)
                    ? defaults.TranslationProvider
                    : profile.Provider.Trim(),
                Endpoint = profile.Endpoint?.Trim() ?? string.Empty,
                Model = string.IsNullOrWhiteSpace(profile.Model)
                    ? string.Empty
                    : profile.Model.Trim(),
            })
            .GroupBy(profile => profile.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
        if (profiles.Length == 0)
        {
            profiles =
            [
                new AiTranslationProfile
                {
                    Name = "在线翻译",
                    Provider = string.IsNullOrWhiteSpace(TranslationProvider)
                        ? defaults.TranslationProvider
                        : TranslationProvider.Trim(),
                    Endpoint = TranslationEndpoint?.Trim() ?? string.Empty,
                    Model = TranslationModel?.Trim() ?? string.Empty,
                },
            ];
        }

        return this with
        {
            SettingsVersion = Math.Max(SettingsVersion, 15),
            Theme = NormalizeTheme(Theme),
            CloseBehavior = Enum.IsDefined(CloseBehavior)
                ? CloseBehavior
                : defaults.CloseBehavior,
            FloatingCaptureClickBehavior = Enum.IsDefined(FloatingCaptureClickBehavior)
                ? FloatingCaptureClickBehavior
                : defaults.FloatingCaptureClickBehavior,
            ScrollCaptureMode = Enum.IsDefined(ScrollCaptureMode)
                ? ScrollCaptureMode
                : defaults.ScrollCaptureMode,
            ArrowStyle = Enum.IsDefined(ArrowStyle)
                ? ArrowStyle
                : defaults.ArrowStyle,
            ArrowToolMode = Enum.IsDefined(ArrowToolMode)
                ? ArrowToolMode
                : defaults.ArrowToolMode,
            ShapeToolMode = Enum.IsDefined(ShapeToolMode)
                ? ShapeToolMode
                : defaults.ShapeToolMode,
            LastAnnotationTool = SettingsVersion < 12
                ? ArrowToolMode == ArrowToolMode.Curved
                    ? AnnotationToolMode.CurvedArrow
                    : ShapeToolMode == ShapeToolMode.Ellipse
                        ? AnnotationToolMode.Ellipse
                        : AnnotationToolMode.Rectangle
                : Enum.IsDefined(LastAnnotationTool)
                    ? LastAnnotationTool
                    : defaults.LastAnnotationTool,
            VisibleCaptureToolbarFeatures =
                visibleCaptureToolbarFeatures.ToArray(),
            CaptureToolbarFeatureOrder = captureToolbarFeatureOrder.ToArray(),
            CaptureToolbarRows = Enum.IsDefined(CaptureToolbarRows)
                ? CaptureToolbarRows
                : defaults.CaptureToolbarRows,
            ToolbarScalePercent = double.IsFinite(ToolbarScalePercent)
                ? Math.Clamp(ToolbarScalePercent, 50, 150)
                : defaults.ToolbarScalePercent,
            VideoRecordingCodec = Enum.IsDefined(VideoRecordingCodec)
                ? VideoRecordingCodec
                : defaults.VideoRecordingCodec,
            RecordingOutputFormat = Enum.IsDefined(RecordingOutputFormat)
                ? RecordingOutputFormat
                : defaults.RecordingOutputFormat,
            CaptureToolbarPositionXRatio = hasCaptureToolbarPosition
                ? CaptureToolbarPositionXRatio
                : -1,
            CaptureToolbarPositionYRatio = hasCaptureToolbarPosition
                ? CaptureToolbarPositionYRatio
                : -1,
            VideoRecordingFrameRate = VideoRecordingFrameRate is 24 or 30 or 60
                ? VideoRecordingFrameRate
                : defaults.VideoRecordingFrameRate,
            SaveDirectory = string.IsNullOrWhiteSpace(SaveDirectory)
                ? defaults.SaveDirectory
                : SaveDirectory.Trim(),
            VideoSaveDirectory = string.IsNullOrWhiteSpace(VideoSaveDirectory)
                ? defaults.VideoSaveDirectory
                : VideoSaveDirectory.Trim(),
            PngSaveLocationMode = Enum.IsDefined(PngSaveLocationMode)
                ? PngSaveLocationMode
                : defaults.PngSaveLocationMode,
            ScreenshotScalePercent = Math.Clamp(
                ScreenshotScalePercent,
                25,
                200),
            RegionCaptureHotKey = RegionCaptureHotKey?.Trim() ?? string.Empty,
            CompleteCaptureHotKey = string.IsNullOrWhiteSpace(CompleteCaptureHotKey)
                ? defaults.CompleteCaptureHotKey
                : CompleteCaptureHotKey.Trim(),
            VideoRecordingHotKey = VideoRecordingHotKey?.Trim() ?? string.Empty,
            // Keep an explicitly cleared shortcut disabled. New settings get
            // the default from the property initializer; only a missing/null
            // value from an older file falls back to the default.
            EndVideoRecordingHotKey = migrateLegacyEndRecordingShortcut
                ? defaults.EndVideoRecordingHotKey
                : EndVideoRecordingHotKey is null
                    ? defaults.EndVideoRecordingHotKey
                    : EndVideoRecordingHotKey.Trim(),
            ScrollCaptureHotKey = ScrollCaptureHotKey?.Trim() ?? string.Empty,
            OcrHotKey = OcrHotKey?.Trim() ?? string.Empty,
            TextTranslationHotKey = TextTranslationHotKey?.Trim() ?? string.Empty,
            PinHotKey = PinHotKey?.Trim() ?? string.Empty,
            OpenSettingsHotKey = OpenSettingsHotKey?.Trim() ?? string.Empty,
            MouseLongPressMilliseconds = Math.Clamp(
                MouseLongPressMilliseconds,
                300,
                2000),
            DefaultSaveFormat = string.IsNullOrWhiteSpace(DefaultSaveFormat)
                ? defaults.DefaultSaveFormat
                : DefaultSaveFormat.Trim(),
            DefaultFontSize = Math.Clamp(DefaultFontSize, 8, 96),
            DefaultStrokeColor = string.IsNullOrWhiteSpace(DefaultStrokeColor)
                ? defaults.DefaultStrokeColor
                : DefaultStrokeColor.Trim(),
            CustomStrokeColor = CustomStrokeColor?.Trim() ?? string.Empty,
            CustomColorPalette = (CustomColorPalette ?? [])
                .Where(color => color is >= 0 and <= 0xFFFFFF)
                .Take(16)
                .ToArray(),
            DefaultStrokeWidth = Math.Clamp(DefaultStrokeWidth, 1, 24),
            AnnotationToolSettings = (AnnotationToolSettings ?? [])
                .Where(setting => !string.IsNullOrWhiteSpace(setting.Tool))
                .Select(setting => setting with
                {
                    Tool = setting.Tool.Trim(),
                    Color = setting.Color?.Trim() ?? string.Empty,
                    StrokeWidth = double.IsFinite(setting.StrokeWidth)
                        ? Math.Clamp(setting.StrokeWidth, 1, 24)
                        : defaults.DefaultStrokeWidth,
                })
                .GroupBy(setting => setting.Tool, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.Last())
                .Take(16)
                .ToArray(),
            OcrLanguageTag = string.IsNullOrWhiteSpace(OcrLanguageTag)
                ? defaults.OcrLanguageTag
                : OcrLanguageTag.Trim(),
            OcrEngine = Enum.IsDefined(OcrEngine)
                ? OcrEngine
                : defaults.OcrEngine,
            RecognitionResultPresentation = Enum.IsDefined(RecognitionResultPresentation)
                ? RecognitionResultPresentation
                : defaults.RecognitionResultPresentation,
            TranslationProvider = TranslationProvider?.Trim() ?? string.Empty,
            TranslationEndpoint = TranslationEndpoint?.Trim() ?? string.Empty,
            TranslationTargetLanguage = string.IsNullOrWhiteSpace(TranslationTargetLanguage)
                ? defaults.TranslationTargetLanguage
                : TranslationTargetLanguage.Trim(),
            TranslationModel = string.IsNullOrWhiteSpace(TranslationModel)
                ? defaults.TranslationModel
                : TranslationModel.Trim(),
            TranslationProfiles = profiles,
            TranslationMode = TranslationMode.Automatic,
            TranslationProviderPriority = translationProviderPriority.ToArray(),
            OfflineTranslationQuality = Enum.IsDefined(OfflineTranslationQuality)
                ? OfflineTranslationQuality
                : defaults.OfflineTranslationQuality,
            OfflineTranslationEngine = Enum.IsDefined(OfflineTranslationEngine)
                ? OfflineTranslationEngine
                : defaults.OfflineTranslationEngine,
            SendTextToOnlineTranslation = true,
            HistoryLimit = Math.Clamp(
                requiresHistoryRetentionMigration
                    ? defaults.HistoryLimit
                    : HistoryLimit,
                1,
                MaximumHistoryItems),
            VideoHistoryLimit = Math.Clamp(
                requiresHistoryRetentionMigration
                    ? defaults.VideoHistoryLimit
                    : VideoHistoryLimit,
                1,
                MaximumHistoryItems),
            ScreenshotHistoryRetentionDays = NormalizeRetentionDays(
                requiresHistoryRetentionMigration
                    ? defaults.ScreenshotHistoryRetentionDays
                    : ScreenshotHistoryRetentionDays),
            VideoHistoryRetentionDays = NormalizeRetentionDays(
                requiresHistoryRetentionMigration
                    ? defaults.VideoHistoryRetentionDays
                    : VideoHistoryRetentionDays),
        };
    }

    private static int NormalizeRetentionDays(int days)
    {
        return Math.Clamp(days, 0, 3650);
    }

    internal static AppTheme NormalizeTheme(AppTheme theme)
    {
        return theme switch
        {
            AppTheme.System or AppTheme.Light => AppTheme.AuroraMist,
            AppTheme.Dark => AppTheme.ForestNight,
            AppTheme.AuroraMist or
            AppTheme.CoralSky or
            AppTheme.GinkgoPaper or
            AppTheme.ForestNight or
            AppTheme.ObsidianGold or
            AppTheme.NeonDeep => theme,
            _ => AppTheme.AuroraMist,
        };
    }

    private static string GetDefaultSaveDirectory()
    {
        return AppMetadata.DefaultCaptureDirectory;
    }

    private static string GetDefaultVideoSaveDirectory()
    {
        return AppMetadata.DefaultVideoDirectory;
    }
}
