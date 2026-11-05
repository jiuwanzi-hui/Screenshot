using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Screenshot.App.Core;
using Screenshot.App.Text;

namespace Screenshot.App.Tests;

public sealed class BaiduTranslationProviderTests
{
    [Fact]
    public async Task LargeModelUsesOfficialEndpointBearerAndLlmWithoutChangingGeneralTranslation()
    {
        var profile = new AiTranslationProfile { Provider = BaiduTranslationProvider.LargeModelProviderId,
            AppId = Guid.NewGuid().ToString(), Model = "llm", Endpoint = BaiduTranslationProvider.LargeModelEndpoint };
        using var handler = new Handler(async request =>
        {
            Assert.Equal(BaiduTranslationProvider.LargeModelEndpoint, request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal("api-key", request.Headers.Authorization.Parameter);
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Assert.Equal(profile.AppId, json.RootElement.GetProperty("appid").GetString());
            Assert.Equal("llm", json.RootElement.GetProperty("model_type").GetString());
            Assert.Equal("zh", json.RootElement.GetProperty("to").GetString());
            Assert.False(json.RootElement.TryGetProperty("sign", out _));
            return Response(new { trans_result = new[] {
                new { src = "The meeting starts at 9:30.", dst = "会议在 9:30 开始。" },
                new { src = "Please save this document.", dst = "请保存此文档。" } } });
        });
        using var client = new HttpClient(handler);
        var test = await TranslationModelCatalogService.TestAsync(profile, "api-key", client);
        Assert.True(test.IsSuccess, test.Message);
        Assert.True(TranslationProfileHealth.Latest(profile)!.IsAvailable);
        Assert.True(TranslationProviderFactory.IsPublicProvider(profile.Provider));
        Assert.Empty(ImageQuestionService.GetProfiles(AppSettings.CreateDefault() with { TranslationProfiles = [profile] }));
        TranslationProfileHealth.Invalidate(profile.Id);
    }

    [Fact]
    public async Task LargeModelFailureAndRecoveryUpdateOnlyItsOwnProfile()
    {
        var profile = new AiTranslationProfile { Provider = BaiduTranslationProvider.LargeModelProviderId, AppId = Guid.NewGuid().ToString() };
        var fail = true;
        using var client = new HttpClient(new Handler(_ => Task.FromResult(fail
            ? Response(new { error_code = "54004" })
            : Response(new { trans_result = new[] { new { src = "Please save this document.", dst = "请保存此文档。" } } }))));
        var provider = TranslationProviderFactory.CreateOnlineProvider(profile, "key", client);
        Assert.False((await provider.TranslateAsync("Please save this document.", "en", "zh")).IsSuccess);
        Assert.False(TranslationProfileHealth.Latest(profile)!.IsAvailable);
        fail = false;
        Assert.True((await provider.TranslateAsync("Please save this document.", "en", "zh")).IsSuccess);
        Assert.True(TranslationProfileHealth.Latest(profile)!.IsAvailable);
        TranslationProfileHealth.Invalidate(profile.Id);
    }

    [Fact]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5351", Justification = "Verifies the MD5 signature required by Baidu's official protocol.")]
    public async Task SignsOriginalUnicodeAndMatchesReorderedMultilineResults()
    {
        string[] input = ["请保存此文档。\n有 25 位参与者。", "请保存此文档。"];
        using var handler = new Handler(async request =>
        {
            Assert.Equal(BaiduTranslationProvider.Endpoint, request.RequestUri!.AbsoluteUri);
            var fields = Parse(await request.Content!.ReadAsStringAsync());
            Assert.Equal("请保存此文档。\n有 25 位参与者。", fields["q"]);
            Assert.Equal("auto", fields["from"]);
            Assert.Equal("en", fields["to"]);
            Assert.Equal(Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(
                fields["appid"] + fields["q"] + fields["salt"] + "key"))).ToLowerInvariant(), fields["sign"]);
            return Response(new { trans_result = new[] {
                new { src = "有 25 位参与者。", dst = "There are 25 participants." },
                new { src = "请保存此文档。", dst = "Please save this document." } } });
        });
        using var client = new HttpClient(handler);
        var result = await new BaiduTranslationProvider(Guid.NewGuid().ToString(), "key", client).TranslateSegmentsAsync(input, "auto", "en");
        Assert.Empty(result.FailedSegmentIndices!);
        Assert.Equal("Please save this document.\nThere are 25 participants.", result.Segments[0]);
        Assert.Equal("Please save this document.", result.Segments[1]);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task MissingRowsAndChangedNumbersDoNotOverwriteGoodTranslations()
    {
        string[] input = ["There are 7 users.", "There are 25 requests.", "There are 4 windows."];
        using var handler = new Handler(_ => Task.FromResult(Response(new { trans_result = new[] {
            new { src = input[2], dst = "有 5 个窗口。" }, new { src = input[0], dst = "有 7 位用户。" } } })));
        using var client = new HttpClient(handler);
        var result = await new BaiduTranslationProvider(Guid.NewGuid().ToString(), "key", client).TranslateSegmentsAsync(input, "en", "zh");
        Assert.Equal([1, 2], result.FailedSegmentIndices);
        Assert.Equal(["有 7 位用户。", input[1], input[2]], result.Segments);
        Assert.Contains("质量不达标", result.ErrorMessage);
    }

    [Theory]
    [InlineData("54001", "凭据")]
    [InlineData("54003", "频率")]
    [InlineData("54004", "余额")]
    public async Task ServiceErrorsDoNotCountAsSuccessfulTranslations(string code, string reason)
    {
        using var handler = new Handler(_ => Task.FromResult(Response(new { error_code = code })));
        using var client = new HttpClient(handler);
        var result = await new BaiduTranslationProvider(Guid.NewGuid().ToString(), "key", client).TranslateAsync("Please save this document.", "en", "zh");
        Assert.False(result.IsSuccess);
        Assert.Contains(reason, result.ErrorMessage);
    }

    [Fact]
    public async Task CancellationAndStandardAccountLimitsAreRespected()
    {
        var sizes = new List<int>();
        var times = new List<long>();
        using var handler = new Handler(async request =>
        {
            times.Add(Environment.TickCount64);
            var query = Parse(await request.Content!.ReadAsStringAsync())["q"];
            sizes.Add(query.Length);
            return Response(new { trans_result = query.Split('\n').Select(src => new { src, dst = "请保存此文档。" }) });
        });
        using var client = new HttpClient(handler);
        var provider = new BaiduTranslationProvider(Guid.NewGuid().ToString(), "key", client);
        await provider.TranslateSegmentsAsync([new string('a', 850), new string('b', 850)], "en", "zh");
        Assert.Equal(2, sizes.Count);
        Assert.All(sizes, size => Assert.InRange(size, 1, 1000));
        Assert.True(times[1] - times[0] >= 1000);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.TranslateAsync("Please save the document.", "en", "zh", cancellation.Token));
    }

    [Fact]
    public void FactoryKeepsConfiguredCategoryAndProfileOrder()
    {
        using var client = new HttpClient();
        var settings = AppSettings.CreateDefault() with { TranslationProviderPriority = [TranslationProviderKind.Public, TranslationProviderKind.Offline, TranslationProviderKind.Online],
            TranslationProfiles = [new() { Provider = "Baidu", AppId = "a" }, new() { Provider = "Youdao", AppId = "b" },
                new() { Provider = "Zhipu", Model = "chosen-model", Endpoint = "https://example.invalid" }] };
        var provider = Assert.IsType<OrderedTranslationProvider>(TranslationProviderFactory.Create(settings, new NoCredentials(), client));
        Assert.Equal(["Baidu", "Youdao", TranslationProviderFactory.OfflineProviderId, TranslationProviderFactory.OpenAiCompatibleProviderId], provider.ProviderIds);
    }

    private sealed class NoCredentials : ITranslationCredentialStore
    {
        public string? GetApiKey(string providerId) => "key";
        public void SetApiKey(string providerId, string? apiKey) { }
    }
    private static Dictionary<string, string> Parse(string form) => form.Split('&').Select(pair => pair.Split('=', 2))
        .ToDictionary(pair => WebUtility.UrlDecode(pair[0]), pair => WebUtility.UrlDecode(pair[1]));
    private static HttpResponseMessage Response(object value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        internal int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); Calls++; return respond(request); }
    }
}
