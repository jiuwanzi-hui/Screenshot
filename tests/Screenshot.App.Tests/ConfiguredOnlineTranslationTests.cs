using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using Screenshot.App.Infrastructure;
using Screenshot.App.Text;

namespace Screenshot.App.Tests;

public sealed class ConfiguredOnlineTranslationTests
{
    private static readonly JsonSerializerOptions ReportOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    [ConfiguredOnlineTranslationFact]
    public async Task MeasuresTheConfiguredOnlineModelWithoutChangingSettingsOrExposingCredentials()
    {
        var directory = Environment.GetEnvironmentVariable("SNAPCUT_ONLINE_SETTINGS_DIRECTORY")!;
        var settings = new SettingsStore(Path.Combine(directory, "settings.json")).Load().Settings;
        var credentials = new DpapiTranslationCredentialStore(Path.Combine(directory, "translation-credentials.dat"));
        var profile = settings.TranslationProfiles.FirstOrDefault(profile => profile.IsEnabled);
        var providerId = TranslationProviderFactory.ResolveProviderId(profile?.Provider ?? settings.TranslationProvider);
        using var handler = new ResponseRecorder();
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        var model = profile?.Model ?? settings.TranslationModel;
        var provider = new OpenAiCompatibleTranslationProvider(profile?.Endpoint ?? settings.TranslationEndpoint,
            model, profile is null ? credentials.GetApiKey(providerId) : credentials.GetApiKey(profile.Id, providerId), client);
        string[] article =
        [
            "2026年アジア競技大会の中国代表団の選手名簿発表",
            "人民網日本語版　2026年09月04日16:47",
            "第20回アジア競技大会に出場する中国体育代表団が9月4日、北京で結成された。代表団は計1274人からなり、そのうち選手は815人（女子選手404人、男子選手411人）。38競技の369種目に出場する。",
            "出場選手のうち、548人がアジア競技大会初出場で、306人はオリンピックまたはアジア競技大会の出場経験がある。卓球の孫穎莎選手、王曼昱選手、飛び込みの陳芋汐選手、王宗源選手を含む、水泳、アーティスティックスイミング、バドミントン、ボクシングなど13競技から計36人のオリンピック金メダリストが今回のアジア大会に臨む。（編集WB）",
        ];
        var report = new List<object>();
        var failures = new List<string>();
        var watch = Stopwatch.StartNew();
        var result = await provider.TranslateSegmentsAsync(article, "auto", "zh-Hans");
        report.Add(new { model, source = "ja", target = "zh", seconds = watch.Elapsed.TotalSeconds,
            result.IsSuccess, result.ErrorMessage, output = result.Segments });
        if (!result.IsSuccess) failures.Add(result.ErrorMessage!);
        string[] targets = Environment.GetEnvironmentVariable("SNAPCUT_ONLINE_ARTICLE_ONLY") == "1"
            ? [] : ["zh", "en", "fr", "es", "ru", "ar"];
        foreach (var target in targets)
        {
            watch.Restart();
            var translated = await provider.TranslateAsync(target == "en"
                ? "La délégation compte 1274 membres, dont 815 athlètes."
                : "The delegation has 1274 members, including 815 athletes.", target == "en" ? "fr" : "en", target);
            report.Add(new { model, source = target == "en" ? "fr" : "en", target, seconds = watch.Elapsed.TotalSeconds,
                translated.IsSuccess, translated.ErrorMessage, output = translated.Text });
            if (!translated.IsSuccess) failures.Add(translated.ErrorMessage!);
        }
        var reportPath = Environment.GetEnvironmentVariable("SNAPCUT_ONLINE_BENCHMARK_REPORT")!;
        await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report, ReportOptions));
        await File.WriteAllTextAsync(Path.ChangeExtension(reportPath, ".responses.json"),
            JsonSerializer.Serialize(handler.Translations, ReportOptions));
        Assert.Empty(failures);
    }

    private sealed class ResponseRecorder() : DelegatingHandler(new HttpClientHandler())
    {
        internal System.Collections.Concurrent.ConcurrentQueue<string> Translations { get; } = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await base.SendAsync(request, cancellationToken);
            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            using var json = JsonDocument.Parse(text);
            if (json.RootElement.TryGetProperty("choices", out var choices))
                foreach (var choice in choices.EnumerateArray())
                    if (choice.TryGetProperty("message", out var message) && message.TryGetProperty("content", out var content))
                        Translations.Enqueue(content.GetString() ?? string.Empty);
            return response;
        }
    }
}

public sealed class ConfiguredOnlineTranslationFactAttribute : FactAttribute
{
    public ConfiguredOnlineTranslationFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("SNAPCUT_RUN_ONLINE_TRANSLATION_TESTS") != "1")
            Skip = "Explicit opt-in required; calls the configured external AI service and may consume its quota.";
    }
}
