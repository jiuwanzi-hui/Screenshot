using System.Runtime.InteropServices;
using System.Text.Json;

namespace SnapCut.Mac.Update;

internal sealed record MacUpdateInfo(
    Version Version,
    string Tag,
    string Source,
    string DownloadUrl,
    string AssetName);

internal sealed class MacApplicationUpdateService : IDisposable
{
    private readonly HttpClient _httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(15),
    };

    public MacApplicationUpdateService()
    {
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("SnapCut-macOS");
    }

    public async Task<MacUpdateInfo?> CheckAsync(
        Version currentVersion,
        CancellationToken cancellationToken = default)
    {
        var country = await DetectCountryAsync(cancellationToken);
        var sources = string.Equals(country, "CN", StringComparison.OrdinalIgnoreCase)
            ? new[] { "Gitee", "GitHub" }
            : new[] { "GitHub", "Gitee" };
        foreach (var source in sources)
        {
            var update = await ReadLatestAsync(source, cancellationToken);
            if (update is not null && update.Version > currentVersion)
            {
                return update;
            }
        }

        return null;
    }

    public async Task<string> DownloadAsync(
        MacUpdateInfo update,
        CancellationToken cancellationToken = default)
    {
        var downloads = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Downloads");
        Directory.CreateDirectory(downloads);
        var destination = Path.Combine(downloads, update.AssetName);
        await using var source = await _httpClient.GetStreamAsync(
            update.DownloadUrl,
            cancellationToken);
        await using var target = new FileStream(
            destination,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            128 * 1024,
            useAsync: true);
        await source.CopyToAsync(target, cancellationToken);
        return destination;
    }

    private async Task<string?> DetectCountryAsync(CancellationToken cancellationToken)
    {
        try
        {
            return (await _httpClient.GetStringAsync(
                "https://ipapi.co/country/",
                cancellationToken)).Trim();
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (TaskCanceledException)
        {
            return null;
        }
    }

    private async Task<MacUpdateInfo?> ReadLatestAsync(
        string source,
        CancellationToken cancellationToken)
    {
        var endpoint = source == "Gitee"
            ? "https://gitee.com/api/v5/repos/wwangyunhui/screenshot/releases/latest"
            : "https://api.github.com/repos/wwangyunhui/screenshot/releases/latest";
        try
        {
            using var document = JsonDocument.Parse(
                await _httpClient.GetStreamAsync(endpoint, cancellationToken));
            var root = document.RootElement;
            var tag = root.GetProperty("tag_name").GetString() ?? string.Empty;
            if (!Version.TryParse(tag.TrimStart('v', 'V'), out var version))
            {
                return null;
            }

            var architecture = RuntimeInformation.ProcessArchitecture == Architecture.Arm64
                ? "osx-arm64"
                : "osx-x64";
            foreach (var asset in root.GetProperty("assets").EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString() ?? string.Empty;
                if (!name.Contains(architecture, StringComparison.OrdinalIgnoreCase) ||
                    !(name.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) ||
                      name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                var url = asset.TryGetProperty("browser_download_url", out var browserUrl)
                    ? browserUrl.GetString()
                    : asset.TryGetProperty("url", out var urlValue)
                        ? urlValue.GetString()
                        : null;
                if (!string.IsNullOrWhiteSpace(url))
                {
                    return new MacUpdateInfo(version, tag, source, url, name);
                }
            }
        }
        catch (Exception exception) when (
            exception is HttpRequestException or JsonException or TaskCanceledException)
        {
        }

        return null;
    }

    public void Dispose() => _httpClient.Dispose();
}
