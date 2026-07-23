using System.IO;

namespace Screenshot.App.Core;

public static class AppMetadata
{
    public const string ApplicationName = "Screenshot";
    public const string DataDirectoryName = "ScreenshotData";
    public const string InstalledDataDirectoryName = "Screenshot";
    public const string InstalledMarkerFileName = "installed.marker";
    public const string CapturesDirectoryName = "Captures";
    public const string StartupRegistrationValueName = "Screenshot.App";

    public static string DataDirectoryPath => ResolveDataDirectoryPath(
        AppContext.BaseDirectory,
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        File.Exists(Path.Combine(AppContext.BaseDirectory, InstalledMarkerFileName)));

    public static string SettingsPath => Path.Combine(
        DataDirectoryPath,
        "settings.json");

    public static string TranslationCredentialsPath => Path.Combine(
        DataDirectoryPath,
        "translation-credentials.dat");

    public static string DefaultCaptureDirectory => Path.Combine(
        DataDirectoryPath,
        CapturesDirectoryName);

    internal static string ResolveDataDirectoryPath(
        string applicationDirectory,
        string localApplicationDataDirectory,
        bool isInstalled)
    {
        return isInstalled
            ? Path.Combine(localApplicationDataDirectory, InstalledDataDirectoryName)
            : Path.Combine(applicationDirectory, DataDirectoryName);
    }
}
