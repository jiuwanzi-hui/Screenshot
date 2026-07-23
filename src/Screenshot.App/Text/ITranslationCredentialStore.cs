namespace Screenshot.App.Text;

public interface ITranslationCredentialStore
{
    string? GetApiKey(string providerId);

    void SetApiKey(string providerId, string? apiKey);
}
