using System.IO;
using System.Reflection;

namespace Screenshot.App.Core;

public static class AppMetadata
{
    public const string ApplicationName = "Screenshot";
    // Brand name shown to the user. Kept separate from ApplicationName so
    // renaming the product never disturbs mutexes, registrations or paths
    // that existing installations depend on.
    public const string DisplayName = "SnapCut";
    public const string DataDirectoryName = "ScreenshotData";
    public const string LegacyInstalledDataDirectoryName = "Screenshot";
    public const string InstalledMarkerFileName = "installed.marker";
    public const string CapturesDirectoryName = "Captures";
    public const string DiagnosticsDirectoryName = "Diagnostics";
    public const string HistoryCacheDirectoryName = "HistoryCache";
    public const string UpdatesDirectoryName = "Updates";
    public const string TranslationModelsDirectoryName = "TranslationModels";
    public const string StartupRegistrationValueName = "Screenshot.App";

    public static bool IsInstalled => File.Exists(Path.Combine(
        AppContext.BaseDirectory,
        InstalledMarkerFileName));

    public static string DataDirectoryPath => ResolveDataDirectoryPath(
        AppContext.BaseDirectory);

    public static string LegacyInstalledDataDirectoryPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        LegacyInstalledDataDirectoryName);

    public static string SettingsPath => Path.Combine(
        DataDirectoryPath,
        "settings.json");

    public static string WindowPlacementsPath => Path.Combine(
        DataDirectoryPath,
        "window-placements.json");

    public static string TranslationCredentialsPath => Path.Combine(
        DataDirectoryPath,
        "translation-credentials.dat");

    public static string DefaultCaptureDirectory => Path.Combine(
        DataDirectoryPath,
        CapturesDirectoryName);

    public static string DiagnosticsDirectoryPath => Path.Combine(
        DataDirectoryPath,
        DiagnosticsDirectoryName);

    public static string HistoryCacheDirectoryPath => Path.Combine(
        DataDirectoryPath,
        HistoryCacheDirectoryName);

    public static string UpdatesDirectoryPath => Path.Combine(
        DataDirectoryPath,
        UpdatesDirectoryName);

    public static string TranslationModelsDirectoryPath => Path.Combine(
        AppContext.BaseDirectory,
        TranslationModelsDirectoryName);

    public static Version CurrentVersion =>
        Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0);

    public static string DisplayVersion => string.Join(
        '.',
        CurrentVersion.Major,
        CurrentVersion.Minor,
        Math.Max(0, CurrentVersion.Build));

    public static string FormatUpdatedVersionStatus(string updatedVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(updatedVersion);
        return $"已更新到 {DisplayName} {updatedVersion.Trim()}。";
    }

    internal static string ResolveDataDirectoryPath(string applicationDirectory)
    {
        return Path.Combine(applicationDirectory, DataDirectoryName);
    }
}
