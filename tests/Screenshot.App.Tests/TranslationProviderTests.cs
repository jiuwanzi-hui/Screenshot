using System.Net;
using System.Net.Http;
using System.Text;
using Screenshot.App.Core;
using Screenshot.App.Text;

namespace Screenshot.App.Tests;

public sealed class TranslationProviderTests
{
    [Fact]
    public async Task SendsConfiguredTextToAnOpenAiCompatibleEndpoint()
    {
        var handler = new RecordingHandler(
            """{"choices":[{"message":{"content":"translated text"}}]}""");
        using var client = new HttpClient(handler);
        var provider = new OpenAiCompatibleTranslationProvider(
            "https://translation.example/v1/chat/completions",
            "test-key",
            client);

        var result = await provider.TranslateAsync(
            "source text",
            "en",
            "zh-Hans");

        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.Equal("translated text", result.Text);
        Assert.Equal("Bearer", handler.AuthorizationScheme);
        Assert.Equal("test-key", handler.AuthorizationParameter);
        Assert.Contains("source text", handler.RequestBody);
        Assert.Contains("zh-Hans", handler.RequestBody);
    }

    [Fact]
    public async Task AppendsChatCompletionsForAnotherVendorsBaseUrl()
    {
        var handler = new RecordingHandler(
            """{"choices":[{"message":{"content":"译文"}}]}""");
        using var client = new HttpClient(handler);
        var provider = new OpenAiCompatibleTranslationProvider(
            "https://vendor.example/v1",
            "vendor-translate-model",
            "vendor-key",
            client);

        var result = await provider.TranslateAsync("hello", "en", "zh-Hans");

        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.Equal(
            "https://vendor.example/v1/chat/completions",
            handler.RequestUri?.ToString().TrimEnd('/'));
        Assert.Contains(
            "\"model\":\"vendor-translate-model\"",
            handler.RequestBody);
    }

    [Fact]
    public async Task TranslatesNumberedSegmentsInOneRequest()
    {
        var handler = new RecordingHandler(
            """{"choices":[{"message":{"content":"{\"translations\":[{\"id\":0,\"text\":\"你好\"},{\"id\":1,\"text\":\"世界\"}]}"}}]}""");
        using var client = new HttpClient(handler);
        var provider = new OpenAiCompatibleTranslationProvider(
            "https://translation.example/v1/chat/completions",
            "test-key",
            client);

        var result = await provider.TranslateSegmentsAsync(
            ["hello", "world"],
            "en",
            "zh-Hans");

        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.Equal(["你好", "世界"], result.Segments);
        Assert.Contains("hello", handler.RequestBody);
        Assert.Contains("world", handler.RequestBody);
        Assert.Contains("translations", handler.RequestBody);
    }

    [Fact]
    public async Task AutomaticallyDetectsTheSourceInsteadOfUsingTheOcrLanguage()
    {
        var handler = new RecordingHandler(
            """{"choices":[{"message":{"content":"服务暂时不可用"}}]}""");
        using var client = new HttpClient(handler);
        var provider = new OpenAiCompatibleTranslationProvider(
            "https://translation.example/v1/chat/completions",
            "test-key",
            client);

        var result = await provider.TranslateAsync(
            "Service temporarily unavailable",
            "zh-Hans",
            "zh-Hans");

        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.Equal("服务暂时不可用", result.Text);
        Assert.Contains("Detect the source language automatically", handler.RequestBody);
        Assert.DoesNotContain("Source language: zh-Hans", handler.RequestBody);
    }

    [Fact]
    public async Task RejectsAnUnchangedBatchAsNotTranslated()
    {
        var handler = new RecordingHandler(
            """{"choices":[{"message":{"content":"{\"translations\":[{\"id\":0,\"text\":\"Service temporarily unavailable\"}]}"}}]}""");
        using var client = new HttpClient(handler);
        var provider = new OpenAiCompatibleTranslationProvider(
            "https://translation.example/v1/chat/completions",
            "test-key",
            client);

        var result = await provider.TranslateSegmentsAsync(
            ["Service temporarily unavailable"],
            "auto",
            "zh-Hans");

        Assert.False(result.IsSuccess);
        Assert.Contains("原样返回", result.ErrorMessage);
    }

    [Fact]
    public async Task RefusesToUseHttpEndpoints()
    {
        using var client = new HttpClient(new RecordingHandler("{}"));
        var provider = new OpenAiCompatibleTranslationProvider(
            "http://translation.example/v1/chat/completions",
            "test-key",
            client);

        var result = await provider.TranslateAsync("text", "en", "zh-Hans");

        Assert.False(result.IsSuccess);
        Assert.Contains("HTTPS", result.ErrorMessage);
    }

    [Fact]
    public async Task UsesDeepSeekV4FlashForTheDeepSeekBaseEndpoint()
    {
        var handler = new RecordingHandler(
            """{"choices":[{"message":{"content":"译文"}}]}""");
        using var client = new HttpClient(handler);
        var provider = new OpenAiCompatibleTranslationProvider(
            "https://api.deepseek.com",
            "gpt-4.1-mini",
            "test-key",
            client);

        var result = await provider.TranslateAsync("hello", "en", "zh-Hans");

        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.Equal(
            "https://api.deepseek.com/chat/completions",
            handler.RequestUri?.ToString().TrimEnd('/'));
        Assert.Contains("\"model\":\"deepseek-v4-flash\"", handler.RequestBody);
    }

    [Fact]
    public async Task NormalizesTheLegacyDeepSeekModelName()
    {
        var handler = new RecordingHandler(
            """{"choices":[{"message":{"content":"译文"}}]}""");
        using var client = new HttpClient(handler);
        var provider = new OpenAiCompatibleTranslationProvider(
            "https://api.deepseek.com",
            "DeepSeek",
            "test-key",
            client);

        var result = await provider.TranslateAsync("hello", "en", "zh-Hans");

        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.Contains("\"model\":\"deepseek-v4-flash\"", handler.RequestBody);
    }

    [Fact]
    public async Task IncludesTheProviderMessageForBadRequests()
    {
        var handler = new RecordingHandler(
            """{"error":{"message":"Model Not Exist"}}""",
            HttpStatusCode.BadRequest);
        using var client = new HttpClient(handler);
        var provider = new OpenAiCompatibleTranslationProvider(
            "https://api.deepseek.com",
            "deepseek-v4-flash",
            "test-key",
            client);

        var result = await provider.TranslateAsync("hello", "en", "zh-Hans");

        Assert.False(result.IsSuccess);
        Assert.Contains("HTTP 400", result.ErrorMessage);
        Assert.Contains("Model Not Exist", result.ErrorMessage);
    }

    [Fact]
    public void UsesNoTranslationProviderWhenConsentIsDisabled()
    {
        var settings = AppSettings.CreateDefault() with
        {
            SendTextToOnlineTranslation = false,
            TranslationProvider = TranslationProviderFactory.OpenAiCompatibleProviderId,
        };
        using var client = new HttpClient(new RecordingHandler("{}"));

        var provider = TranslationProviderFactory.Create(
            settings,
            new FakeCredentialStore("test-key"),
            client);

        Assert.IsType<NoTranslationProvider>(provider);
    }

    [Fact]
    public void ResolvesAnEmptyProviderToTheOpenAiCompatibleProvider()
    {
        var settings = AppSettings.CreateDefault() with
        {
            SendTextToOnlineTranslation = true,
            TranslationProvider = string.Empty,
            TranslationEndpoint = "https://api.deepseek.com",
        };
        using var client = new HttpClient(new RecordingHandler("{}"));

        var provider = TranslationProviderFactory.Create(
            settings,
            new FakeCredentialStore("test-key"),
            client);

        Assert.IsType<OpenAiCompatibleTranslationProvider>(provider);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("openaicompatible")]
    [InlineData("legacy-free-text-value")]
    public void ResolveProviderIdFallsBackToTheOpenAiCompatibleId(string? configured)
    {
        Assert.Equal(
            TranslationProviderFactory.OpenAiCompatibleProviderId,
            TranslationProviderFactory.ResolveProviderId(configured));
    }

    [Fact]
    public void LooksUpTheApiKeyUsingTheResolvedProviderId()
    {
        var settings = AppSettings.CreateDefault() with
        {
            SendTextToOnlineTranslation = true,
            TranslationProvider = string.Empty,
            TranslationEndpoint = "https://api.deepseek.com",
        };
        var credentialStore = new RecordingCredentialStore("stored-key");
        using var client = new HttpClient(new RecordingHandler("{}"));

        _ = TranslationProviderFactory.Create(settings, credentialStore, client);

        Assert.Equal(
            TranslationProviderFactory.OpenAiCompatibleProviderId,
            credentialStore.RequestedProviderId);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly string _responseBody;
        private readonly HttpStatusCode _statusCode;

        public RecordingHandler(
            string responseBody,
            HttpStatusCode statusCode = HttpStatusCode.OK)
        {
            _responseBody = responseBody;
            _statusCode = statusCode;
        }

        public string? AuthorizationScheme { get; private set; }

        public string? AuthorizationParameter { get; private set; }

        public string RequestBody { get; private set; } = string.Empty;

        public Uri? RequestUri { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            AuthorizationScheme = request.Headers.Authorization?.Scheme;
            AuthorizationParameter = request.Headers.Authorization?.Parameter;
            RequestUri = request.RequestUri;
            RequestBody = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);

            return new HttpResponseMessage(_statusCode)
            {
                Content = new StringContent(
                    _responseBody,
                    Encoding.UTF8,
                    "application/json"),
            };
        }
    }

    private sealed class FakeCredentialStore : ITranslationCredentialStore
    {
        private readonly string? _apiKey;

        public FakeCredentialStore(string? apiKey)
        {
            _apiKey = apiKey;
        }

        public string? GetApiKey(string providerId)
        {
            return _apiKey;
        }

        public void SetApiKey(string providerId, string? apiKey)
        {
        }
    }

    private sealed class RecordingCredentialStore : ITranslationCredentialStore
    {
        private readonly string? _apiKey;

        public RecordingCredentialStore(string? apiKey)
        {
            _apiKey = apiKey;
        }

        public string? RequestedProviderId { get; private set; }

        public string? GetApiKey(string providerId)
        {
            RequestedProviderId = providerId;
            return _apiKey;
        }

        public void SetApiKey(string providerId, string? apiKey)
        {
        }
    }
}
