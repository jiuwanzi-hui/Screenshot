using System.IO;

namespace Screenshot.App.Core;

public static class AppMetadata
{
    public const string ApplicationName = "Screenshot";
    public const string DataDirectoryName = "ScreenshotData";
    public const string LegacyInstalledDataDirectoryName = "Screenshot";
    public const string InstalledMarkerFileName = "installed.marker";
    public const string CapturesDirectoryName = "Captures";
    public const string DiagnosticsDirectoryName = "Diagnostics";
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

    public static string TranslationCredentialsPath => Path.Combine(
        DataDirectoryPath,
        "translation-credentials.dat");

    public static string DefaultCaptureDirectory => Path.Combine(
        DataDirectoryPath,
        CapturesDirectoryName);

    public static string DiagnosticsDirectoryPath => Path.Combine(
        DataDirectoryPath,
        DiagnosticsDirectoryName);

    internal static string ResolveDataDirectoryPath(string applicationDirectory)
    {
        return Path.Combine(applicationDirectory, DataDirectoryName);
    }
}
