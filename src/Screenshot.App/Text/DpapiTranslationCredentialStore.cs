using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Screenshot.App.Core;

namespace Screenshot.App.Text;

public sealed class DpapiTranslationCredentialStore : ITranslationCredentialStore
{
    private const string CredentialFileName = "translation-credentials.dat";
    private static readonly byte[] Entropy =
        Encoding.UTF8.GetBytes("Screenshot.App.TranslationCredentials.v1");
    private readonly string _credentialsPath;

    public DpapiTranslationCredentialStore(string? credentialsPath = null)
    {
        _credentialsPath = credentialsPath ?? AppMetadata.TranslationCredentialsPath;
    }

    public string? GetApiKey(string providerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);

        return LoadCredentials().TryGetValue(providerId, out var apiKey)
            ? apiKey
            : null;
    }

    public void SetApiKey(string providerId, string? apiKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        var credentials = LoadCredentials();

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            credentials.Remove(providerId);
        }
        else
        {
            credentials[providerId] = apiKey.Trim();
        }

        SaveCredentials(credentials);
    }

    private Dictionary<string, string> LoadCredentials()
    {
        if (!File.Exists(_credentialsPath))
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        try
        {
            var encryptedCredentials = File.ReadAllBytes(_credentialsPath);
            var decryptedCredentials = ProtectedData.Unprotect(
                encryptedCredentials,
                Entropy,
                DataProtectionScope.CurrentUser);

            return JsonSerializer.Deserialize<Dictionary<string, string>>(
                decryptedCredentials)
                ?? new Dictionary<string, string>(StringComparer.Ordinal);
        }
        catch (CryptographicException)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
        catch (IOException)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    private void SaveCredentials(IReadOnlyDictionary<string, string> credentials)
    {
        var credentialsDirectory = Path.GetDirectoryName(_credentialsPath)
            ?? throw new InvalidOperationException("无法确定凭据文件目录。");
        Directory.CreateDirectory(credentialsDirectory);

        if (credentials.Count == 0)
        {
            if (File.Exists(_credentialsPath))
            {
                File.Delete(_credentialsPath);
            }

            return;
        }

        var plaintext = JsonSerializer.SerializeToUtf8Bytes(credentials);
        var encryptedCredentials = ProtectedData.Protect(
            plaintext,
            Entropy,
            DataProtectionScope.CurrentUser);
        var temporaryPath = Path.Combine(
            credentialsDirectory,
            $".{CredentialFileName}.{Guid.NewGuid():N}.tmp");

        try
        {
            File.WriteAllBytes(temporaryPath, encryptedCredentials);
            File.Move(temporaryPath, _credentialsPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}
