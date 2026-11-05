using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Screenshot.App.Text;

namespace Screenshot.App.Tests;

public sealed class OnlineTranslationQualityTests
{
    [Theory]
    [InlineData("en", "La délégation comprend tous les athlètes qui participeront aux compétitions internationales.")]
    [InlineData("fr", "The delegation includes all the athletes who will participate in the international competitions.")]
    [InlineData("es", "The delegation includes all the athletes who will participate in the international competitions.")]
    [InlineData("ru", "The delegation includes all the athletes who will participate in the international competitions.")]
    [InlineData("ar", "The delegation includes all the athletes who will participate in the international competitions.")]
    public async Task WrongUnitedNationsTargetLanguageProducesModelQualityNotice(string target, string output)
    {
        var response = JsonSerializer.Serialize(new { choices = new[] { new { message = new { content = output } } } });
        using var client = new HttpClient(new FixedHandler(response));
        var provider = new OpenAiCompatibleTranslationProvider("https://translation.example/v1", "wrong-language-model", "test-key", client);
        var result = await provider.TranslateAsync("代表团包括所有将参加国际比赛的运动员。", "zh-Hans", target);
        Assert.False(result.IsSuccess);
        Assert.Contains("wrong-language-model", result.ErrorMessage);
        Assert.Contains("译文质量不达标", result.ErrorMessage);
        Assert.Contains("目标语言", result.ErrorMessage);
    }

    [Theory]
    [InlineData("{\"choices\":[]}", "缺少译文")]
    [InlineData("{\"choices\":[{\"message\":{\"content\":null}}]}", "有效的文字")]
    [InlineData("{\"choices\":[{\"finish_reason\":\"length\",\"message\":{\"content\":\"中国代表团\"}}]}", "截断")]
    [InlineData("{\"choices\":[{\"message\":{\"content\":\"代表团共有9274人。\"}}]}", "数字")]
    [InlineData("{\"choices\":[{\"message\":{\"content\":\"The delegation includes all athletes.\"}}]}", "其他语言")]
    public async Task InvalidModelOutputExplainsQualityFailureAndIdentifiesModel(string response, string reason)
    {
        using var client = new HttpClient(new FixedHandler(response));
        var provider = new OpenAiCompatibleTranslationProvider("https://translation.example/v1", "test-model", "test-key", client);
        var result = await provider.TranslateAsync("The delegation has 1274 members.", "en", "zh-Hans");
        Assert.False(result.IsSuccess);
        Assert.Contains("译文质量不达标", result.ErrorMessage);
        Assert.Contains("test-model", result.ErrorMessage);
        Assert.Contains(reason, result.ErrorMessage);
    }

    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 1)]
    [InlineData(true, 4)]
    [InlineData(false, 4)]
    public async Task ConfiguredOnlineOrOfflinePriorityIsRespectedForSmallAndLargeCaptures(bool onlineFirst, int count)
    {
        var translations = Enumerable.Repeat("代表团共有1274人。", count).ToArray();
        var body = JsonSerializer.Serialize(new { choices = new[] { new { message = new
            { content = count == 1 ? translations[0] : JsonSerializer.Serialize(new { translations }) } } } });
        using var handler = new FixedHandler(body);
        using var client = new HttpClient(handler);
        var online = new OpenAiCompatibleTranslationProvider("https://translation.example/v1", "test-model", "test-key", client);
        var offline = new FixedOfflineProvider(translations);
        var provider = new OrderedTranslationProvider(onlineFirst ? [online, offline] : [offline, online]);
        var result = await provider.TranslateSegmentsAsync(Enumerable.Repeat("The delegation has 1274 members.", count).ToArray(), "en", "zh-Hans");
        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.Equal(onlineFirst ? 1 : 0, handler.Calls);
        Assert.Equal(onlineFirst ? 0 : 1, offline.Calls);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public async Task QualityFailureFallsThroughInOrderAndRemainsVisibleWithTheSuccessfulTranslation(int count)
    {
        var wrong = Enumerable.Repeat("代表团共有9274人。", count).ToArray();
        var body = JsonSerializer.Serialize(new { choices = new[] { new { message = new
            { content = wrong[0] } } } });
        using var client = new HttpClient(new FixedHandler(body));
        var online = new OpenAiCompatibleTranslationProvider("https://translation.example/v1", "bad-model", "test-key", client);
        var offline = new FixedOfflineProvider(Enumerable.Repeat("代表团共有1274人。", count).ToArray());
        var result = await new OrderedTranslationProvider([online, offline]).TranslateSegmentsAsync(
            Enumerable.Repeat("The delegation has 1274 members.", count).ToArray(), "en", "zh-Hans");
        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.Null(result.ErrorMessage);
        Assert.Contains("bad-model", result.Notice);
        Assert.Contains("译文质量不达标", result.Notice);
        Assert.Contains("数字", result.Notice);
        Assert.Equal(1, offline.Calls);
    }

    private sealed class FixedHandler(string response) : HttpMessageHandler
    {
        private int _calls;
        internal int Calls => Volatile.Read(ref _calls);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class FixedOfflineProvider(string[] translations) : ITranslationProvider
    {
        public string Id => TranslationProviderFactory.LocalLargeModelProviderId;
        internal int Calls { get; private set; }
        public Task<TranslationResult> TranslateAsync(string text, string sourceLanguage, string targetLanguage, CancellationToken cancellationToken = default)
            => Task.FromResult(new TranslationResult(true, translations[0], null));
        public Task<TranslationSegmentsResult> TranslateSegmentsAsync(IReadOnlyList<string> segments, string sourceLanguage, string targetLanguage, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new TranslationSegmentsResult(true, translations, null));
        }
    }
}
