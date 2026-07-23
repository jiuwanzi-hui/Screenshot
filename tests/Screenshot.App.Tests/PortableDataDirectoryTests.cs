using System.IO;
using Screenshot.App.Core;
using Screenshot.App.Infrastructure;
using Screenshot.App.Text;

namespace Screenshot.App.Tests;

public sealed class PortableDataDirectoryTests
{
    [Fact]
    public void UsesAnApplicationLocalDataDirectoryByDefault()
    {
        var expectedDataDirectory = Path.Combine(
            AppContext.BaseDirectory,
            AppMetadata.DataDirectoryName);

        Assert.Equal(expectedDataDirectory, AppMetadata.DataDirectoryPath);
        Assert.Equal(
            Path.Combine(expectedDataDirectory, "settings.json"),
            new SettingsStore().SettingsPath);
        Assert.Equal(
            Path.Combine(expectedDataDirectory, AppMetadata.CapturesDirectoryName),
            AppSettings.CreateDefault().SaveDirectory);
    }

    [Fact]
    public void StoresCredentialsAlongsideTheApplicationData()
    {
        var expectedPath = Path.Combine(
            AppMetadata.DataDirectoryPath,
            "translation-credentials.dat");
        var credentialStore = new DpapiTranslationCredentialStore();

        var pathField = typeof(DpapiTranslationCredentialStore)
            .GetField("_credentialsPath", System.Reflection.BindingFlags.Instance |
                                       System.Reflection.BindingFlags.NonPublic);

        Assert.NotNull(pathField);
        Assert.Equal(expectedPath, pathField.GetValue(credentialStore));
    }

    [Fact]
    public void InstalledBuildsUseTheCurrentUsersLocalApplicationDataDirectory()
    {
        var dataDirectory = AppMetadata.ResolveDataDirectoryPath(
            @"C:\Program Files\Screenshot",
            @"C:\Users\Tester\AppData\Local",
            isInstalled: true);

        Assert.Equal(
            @"C:\Users\Tester\AppData\Local\Screenshot",
            dataDirectory);
    }
}
