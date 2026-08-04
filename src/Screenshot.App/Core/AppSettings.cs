using System.IO;

namespace Screenshot.App.Core;

public enum AppTheme
{
    System,
    Light,
    Dark,
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

public enum VideoRecordingCodec
{
    H264,
    H265,
}

public readonly record struct VideoRecordingPreferences(
    VideoRecordingCodec Codec,
    int FrameRate,
    bool RecordSystemAudio,
    bool RecordMicrophone,
    bool ShowKeyboardInput = false,
    bool ShowMouseInput = false);

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

public sealed record AppSettings
{
    public int SettingsVersion { get; init; } = 3;

    public string SaveDirectory { get; init; } = GetDefaultSaveDirectory();

    public string VideoSaveDirectory { get; init; } = GetDefaultVideoSaveDirectory();

    public bool RecordSystemAudio { get; init; } = true;

    public bool RecordMicrophone { get; init; }

    public VideoRecordingCodec VideoRecordingCodec { get; init; } =
        VideoRecordingCodec.H264;

    public int VideoRecordingFrameRate { get; init; } = 30;

    public bool ShowKeyboardInputInRecording { get; init; }

    public bool ShowMouseInputInRecording { get; init; }

    public bool ShowTaskbarIcon { get; init; } = true;

    public bool ShowNotificationIcon { get; init; } = true;

    public bool ShowFloatingCaptureButton { get; init; }

    public FloatingCaptureClickBehavior FloatingCaptureClickBehavior { get; init; } =
        FloatingCaptureClickBehavior.ShowSelection;

    public bool LaunchAtStartup { get; init; }

    public WindowCloseBehavior CloseBehavior { get; init; } =
        WindowCloseBehavior.MinimizeToBackground;

    public AppTheme Theme { get; init; } = AppTheme.System;

    public string RegionCaptureHotKey { get; init; } = "Ctrl+Alt+S";

    public string VideoRecordingHotKey { get; init; } = string.Empty;

    public string ScrollCaptureHotKey { get; init; } = string.Empty;

    public string OcrHotKey { get; init; } = "Ctrl+Alt+O";

    public string PinHotKey { get; init; } = "Ctrl+Alt+P";

    public string OpenSettingsHotKey { get; init; } = "Ctrl+Alt+Comma";

    public int MouseLongPressMilliseconds { get; init; } = 700;

    public bool MouseSideButtonsUseLongPress { get; init; }

    public string DefaultSaveFormat { get; init; } = "Png";

    public int DefaultFontSize { get; init; } = 16;

    public string DefaultStrokeColor { get; init; } = "#007F73";

    public int DefaultStrokeWidth { get; init; } = 3;

    public string OcrLanguageTag { get; init; } = "zh-Hans";

    public string TranslationProvider { get; init; } = "OpenAICompatible";

    public string TranslationEndpoint { get; init; } = string.Empty;

    public string TranslationTargetLanguage { get; init; } = "zh-Hans";

    public string TranslationModel { get; init; } = "gpt-4.1-mini";

    public TranslationMode TranslationMode { get; init; } =
        TranslationMode.Automatic;

    public TranslationProviderKind[] TranslationProviderPriority { get; init; } =
    [
        TranslationProviderKind.Online,
        TranslationProviderKind.Offline,
    ];

    public bool KeepHistory { get; init; }

    public int HistoryLimit { get; init; } = 20;

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
        // Translation is invoked explicitly by the user. Keep the legacy mode
        // only as a migration hint for which provider should be tried first.
        var translationProviderPriority = ResolveTranslationProviderPriority();

        return this with
        {
            SettingsVersion = Math.Max(SettingsVersion, 3),
            CloseBehavior = Enum.IsDefined(CloseBehavior)
                ? CloseBehavior
                : defaults.CloseBehavior,
            FloatingCaptureClickBehavior = Enum.IsDefined(FloatingCaptureClickBehavior)
                ? FloatingCaptureClickBehavior
                : defaults.FloatingCaptureClickBehavior,
            VideoRecordingCodec = Enum.IsDefined(VideoRecordingCodec)
                ? VideoRecordingCodec
                : defaults.VideoRecordingCodec,
            VideoRecordingFrameRate = VideoRecordingFrameRate is 24 or 30 or 60
                ? VideoRecordingFrameRate
                : defaults.VideoRecordingFrameRate,
            SaveDirectory = string.IsNullOrWhiteSpace(SaveDirectory)
                ? defaults.SaveDirectory
                : SaveDirectory.Trim(),
            VideoSaveDirectory = string.IsNullOrWhiteSpace(VideoSaveDirectory)
                ? defaults.VideoSaveDirectory
                : VideoSaveDirectory.Trim(),
            RegionCaptureHotKey = RegionCaptureHotKey?.Trim() ?? string.Empty,
            VideoRecordingHotKey = VideoRecordingHotKey?.Trim() ?? string.Empty,
            ScrollCaptureHotKey = ScrollCaptureHotKey?.Trim() ?? string.Empty,
            OcrHotKey = OcrHotKey?.Trim() ?? string.Empty,
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
            DefaultStrokeWidth = Math.Clamp(DefaultStrokeWidth, 1, 24),
            OcrLanguageTag = string.IsNullOrWhiteSpace(OcrLanguageTag)
                ? defaults.OcrLanguageTag
                : OcrLanguageTag.Trim(),
            TranslationProvider = TranslationProvider?.Trim() ?? string.Empty,
            TranslationEndpoint = TranslationEndpoint?.Trim() ?? string.Empty,
            TranslationTargetLanguage = string.IsNullOrWhiteSpace(TranslationTargetLanguage)
                ? defaults.TranslationTargetLanguage
                : TranslationTargetLanguage.Trim(),
            TranslationModel = string.IsNullOrWhiteSpace(TranslationModel)
                ? defaults.TranslationModel
                : TranslationModel.Trim(),
            TranslationMode = TranslationMode.Automatic,
            TranslationProviderPriority = translationProviderPriority.ToArray(),
            SendTextToOnlineTranslation = true,
            HistoryLimit = Math.Clamp(HistoryLimit, 0, 100),
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
