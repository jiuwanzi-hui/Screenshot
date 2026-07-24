namespace Screenshot.App.Text;

public static class OpenAiCompatibleEndpointResolver
{
    public static string NormalizeChatCompletionsEndpoint(string? endpoint)
    {
        var value = endpoint?.Trim() ?? string.Empty;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return value;
        }

        var path = uri.AbsolutePath.TrimEnd('/');
        if (path.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
        {
            return new UriBuilder(uri) { Path = path }.Uri.AbsoluteUri;
        }

        if (path.EndsWith("/responses", StringComparison.OrdinalIgnoreCase))
        {
            path = path[..^"/responses".Length] + "/chat/completions";
        }
        else if (string.IsNullOrEmpty(path))
        {
            path = uri.Host.Equals(
                "api.deepseek.com",
                StringComparison.OrdinalIgnoreCase)
                ? "/chat/completions"
                : "/v1/chat/completions";
        }
        else if (path.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
        {
            path += "/chat/completions";
        }

        return new UriBuilder(uri)
        {
            Path = path,
            Query = string.Empty,
            Fragment = string.Empty,
        }.Uri.AbsoluteUri;
    }

    public static Uri? CreateModelsEndpoint(string? endpoint)
    {
        var chatEndpoint = NormalizeChatCompletionsEndpoint(endpoint);
        if (!Uri.TryCreate(chatEndpoint, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps)
        {
            return null;
        }

        var path = uri.AbsolutePath.TrimEnd('/');
        if (path.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
        {
            path = path[..^"/chat/completions".Length] + "/models";
        }
        else if (!path.EndsWith("/models", StringComparison.OrdinalIgnoreCase))
        {
            path += "/models";
        }

        return new UriBuilder(uri)
        {
            Path = path,
            Query = string.Empty,
            Fragment = string.Empty,
        }.Uri;
    }
}
