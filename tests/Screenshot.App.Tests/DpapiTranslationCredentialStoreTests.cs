using System.IO;
using Screenshot.App.Text;

namespace Screenshot.App.Tests;

public sealed class DpapiTranslationCredentialStoreTests : IDisposable
{
    private readonly string _testDirectory;
    private readonly string _credentialsPath;

    public DpapiTranslationCredentialStoreTests()
    {
        _testDirectory = Path.Combine(
            Path.GetTempPath(),
            "Screenshot.App.Tests",
            Guid.NewGuid().ToString("N"));
        _credentialsPath = Path.Combine(_testDirectory, "credentials.dat");
        Directory.CreateDirectory(_testDirectory);
    }

    [Fact]
    public void EncryptsAndRetrievesCredentialsForMultipleProviders()
    {
        var store = new DpapiTranslationCredentialStore(_credentialsPath);

        store.SetApiKey("ProviderA", "secret-a");
        store.SetApiKey("ProviderB", "secret-b");

        Assert.Equal("secret-a", store.GetApiKey("ProviderA"));
        Assert.Equal("secret-b", store.GetApiKey("ProviderB"));
        Assert.DoesNotContain("secret-a", File.ReadAllText(_credentialsPath));
        Assert.DoesNotContain("secret-b", File.ReadAllText(_credentialsPath));
    }

    public void Dispose()
    {
        if (File.Exists(_credentialsPath))
        {
            File.Delete(_credentialsPath);
        }

        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory);
        }
    }
}
