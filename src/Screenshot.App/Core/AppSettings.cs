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

public sealed record AppSettings
{
    public int SettingsVersion { get; init; } = 1;

    public string SaveDirectory { get; init; } = GetDefaultSaveDirectory();

    public bool ShowTaskbarIcon { get; init; }

    public bool ShowNotificationIcon { get; init; } = true;

    public bool LaunchAtStartup { get; init; }

    public WindowCloseBehavior CloseBehavior { get; init; } =
        WindowCloseBehavior.MinimizeToBackground;

    public AppTheme Theme { get; init; } = AppTheme.System;

    public string RegionCaptureHotKey { get; init; } = "Ctrl+Alt+S";

    public string ScrollCaptureHotKey { get; init; } = "Ctrl+Alt+L";

    public string OcrHotKey { get; init; } = "Ctrl+Alt+O";

    public string PinHotKey { get; init; } = "Ctrl+Alt+P";

    public string OpenSettingsHotKey { get; init; } = "Ctrl+Alt+Comma";

    public string DefaultSaveFormat { get; init; } = "Png";

    public int DefaultFontSize { get; init; } = 16;

    public string DefaultStrokeColor { get; init; } = "#007F73";

    public int DefaultStrokeWidth { get; init; } = 3;

    public string OcrLanguageTag { get; init; } = "zh-Hans";

    public string TranslationProvider { get; init; } = "OpenAICompatible";

    public string TranslationEndpoint { get; init; } = string.Empty;

    public string TranslationTargetLanguage { get; init; } = "zh-Hans";

    public string TranslationModel { get; init; } = "gpt-4.1-mini";

    public bool KeepHistory { get; init; }

    public int HistoryLimit { get; init; } = 20;

    public bool SendTextToOnlineTranslation { get; init; }

    public static AppSettings CreateDefault()
    {
        return new AppSettings();
    }

    public AppSettings Normalize()
    {
        var defaults = CreateDefault();

        return this with
        {
            SettingsVersion = Math.Max(SettingsVersion, 1),
            CloseBehavior = Enum.IsDefined(CloseBehavior)
                ? CloseBehavior
                : defaults.CloseBehavior,
            SaveDirectory = string.IsNullOrWhiteSpace(SaveDirectory)
                ? defaults.SaveDirectory
                : SaveDirectory.Trim(),
            RegionCaptureHotKey = RegionCaptureHotKey?.Trim() ?? string.Empty,
            ScrollCaptureHotKey = ScrollCaptureHotKey?.Trim() ?? string.Empty,
            OcrHotKey = OcrHotKey?.Trim() ?? string.Empty,
            PinHotKey = PinHotKey?.Trim() ?? string.Empty,
            OpenSettingsHotKey = OpenSettingsHotKey?.Trim() ?? string.Empty,
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
            HistoryLimit = Math.Clamp(HistoryLimit, 0, 100),
        };
    }

    private static string GetDefaultSaveDirectory()
    {
        return AppMetadata.DefaultCaptureDirectory;
    }
}
