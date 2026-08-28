namespace Screenshot.App.Text;

public interface ITranslationCredentialStore
{
    string? GetApiKey(string providerId);

    void SetApiKey(string providerId, string? apiKey);

    string? GetApiKey(string profileId, string providerId) =>
        GetApiKey($"profile:{profileId}:{providerId}") ?? GetApiKey(providerId);

    void SetApiKey(string profileId, string providerId, string? apiKey) =>
        SetApiKey($"profile:{profileId}:{providerId}", apiKey);
}
