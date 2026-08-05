using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
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
        Assert.Contains("same screenshot or document", handler.RequestBody);
        Assert.Contains("never mix in untranslated source-language", handler.RequestBody);
    }

    [Fact]
    public async Task AcceptsJsonAfterProviderCommentary()
    {
        var handler = new RecordingHandler(
            """{"choices":[{"message":{"content":"Translation result:\n```json\n{\"translations\":[{\"id\":0,\"text\":\"你好\"},{\"id\":1,\"text\":\"世界\"}]}\n```"}}]}""");
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
    }

    [Fact]
    public async Task AcceptsAnOrderedStringArrayFromProvider()
    {
        var handler = new RecordingHandler(
            """{"choices":[{"message":{"content":"[\"你好\",\"世界\"]"}}]}""");
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
    }

    [Fact]
    public async Task RetriesIndividualSegmentsWhenBatchResultIsIncomplete()
    {
        var handler = new IncompleteBatchFallbackHandler();
        using var client = new HttpClient(handler);
        var provider = new OpenAiCompatibleTranslationProvider(
            "https://translation.example/v1/chat/completions",
            "test-key",
            client);

        var result = await provider.TranslateSegmentsAsync(
            ["hello", "world", "常规设置"],
            "auto",
            "zh-Hans");

        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.Equal(["你好", "世界", "常规设置"], result.Segments);
        Assert.Equal(3, handler.RequestCount);
        Assert.DoesNotContain(
            handler.SentContents,
            content => content.Contains("常规设置", StringComparison.Ordinal));
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
    public async Task DoesNotSendChineseTextForChineseTranslation()
    {
        var handler = new RecordingHandler(
            """{"choices":[{"message":{"content":"不应调用"}}]}""");
        using var client = new HttpClient(handler);
        var provider = new OpenAiCompatibleTranslationProvider(
            "https://translation.example/v1/chat/completions",
            "test-key",
            client);

        var result = await provider.TranslateSegmentsAsync(
            ["常规设置", "截图保存位置"],
            "auto",
            "zh-Hans");

        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.Equal(["常规设置", "截图保存位置"], result.Segments);
        Assert.Empty(handler.RequestBody);
    }

    [Fact]
    public async Task PreservesTargetLanguageSegmentsInMixedTranslation()
    {
        var handler = new RecordingHandler(
            """{"choices":[{"message":{"content":"{\"translations\":[{\"id\":0,\"text\":\"保存位置\"}]}"}}]}""");
        using var client = new HttpClient(handler);
        var provider = new OpenAiCompatibleTranslationProvider(
            "https://translation.example/v1/chat/completions",
            "test-key",
            client);

        var result = await provider.TranslateSegmentsAsync(
            ["常规设置", "Save location"],
            "auto",
            "zh-Hans");

        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.Equal(["常规设置", "保存位置"], result.Segments);
        Assert.DoesNotContain("常规设置", handler.RequestBody);
    }

    [Fact]
    public async Task PreservesSingleTargetGlyphsAndShortUppercaseIdentifiers()
    {
        var handler = new RecordingHandler(
            """{"choices":[{"message":{"content":"{\"translations\":[{\"id\":0,\"text\":\"连接\"}]}"}}]}""");
        using var client = new HttpClient(handler);
        var provider = new OpenAiCompatibleTranslationProvider(
            "https://translation.example/v1/chat/completions",
            "test-key",
            client);

        var result = await provider.TranslateSegmentsAsync(
            ["旦", "GI", "V", "Connect"],
            "auto",
            "zh-Hans");

        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.Equal(["旦", "GI", "V", "连接"], result.Segments);
        Assert.DoesNotContain("旦", handler.RequestBody);
        Assert.DoesNotContain("GI", handler.RequestBody);
        Assert.DoesNotContain("\"V\"", handler.RequestBody);
    }

    [Fact]
    public async Task PreservesMixedChineseIdentifierRowsForChineseTarget()
    {
        var handler = new RecordingHandler(
            """{"choices":[{"message":{"content":"{\"translations\":[{\"id\":0,\"text\":\"连接\"}]}"}}]}""");
        using var client = new HttpClient(handler);
        var provider = new OpenAiCompatibleTranslationProvider(
            "https://translation.example/v1/chat/completions",
            "test-key",
            client);

        var result = await provider.TranslateSegmentsAsync(
            [
                "MySQL+PG_TD安装说明文档",
                "Ubuntu+Debian密码重置",
                "Connect",
            ],
            "auto",
            "zh-Hans");

        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.Equal(
            ["MySQL+PG_TD安装说明文档", "Ubuntu+Debian密码重置", "连接"],
            result.Segments);
        Assert.DoesNotContain("MySQL", handler.RequestBody);
        Assert.DoesNotContain("Ubuntu", handler.RequestBody);
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
    public void TranslationRemainsAvailableWithoutTheLegacyConsentFlag()
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

        var ordered = Assert.IsType<OrderedTranslationProvider>(provider);
        Assert.Equal(
            [
                TranslationProviderFactory.OpenAiCompatibleProviderId,
                TranslationProviderFactory.OfflineProviderId,
            ],
            ordered.ProviderIds);
    }

    [Fact]
    public void AutomaticModeCreatesProvidersInTheConfiguredOrder()
    {
        var settings = AppSettings.CreateDefault() with
        {
            TranslationMode = TranslationMode.Automatic,
            TranslationProviderPriority =
            [
                TranslationProviderKind.Offline,
                TranslationProviderKind.Online,
            ],
        };
        using var client = new HttpClient(new RecordingHandler("{}"));

        var provider = Assert.IsType<OrderedTranslationProvider>(
            TranslationProviderFactory.Create(
                settings,
                new FakeCredentialStore("test-key"),
                client));

        Assert.Equal(
            [
                TranslationProviderFactory.OfflineProviderId,
                TranslationProviderFactory.OpenAiCompatibleProviderId,
            ],
            provider.ProviderIds);
    }

    [Fact]
    public async Task OrderedProviderFallsBackForOcrSegmentTranslation()
    {
        var first = new StubTranslationProvider(
            "First",
            segmentResult: TranslationSegmentsResult.Failure("连接失败"));
        var second = new StubTranslationProvider(
            "Second",
            segmentResult: new TranslationSegmentsResult(
                true,
                ["你好", "世界"],
                null));
        var provider = new OrderedTranslationProvider([first, second]);

        var result = await provider.TranslateSegmentsAsync(
            ["hello", "world"],
            "auto",
            "zh-Hans");

        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.Equal(["你好", "世界"], result.Segments);
        Assert.Equal(1, first.SegmentCallCount);
        Assert.Equal(1, second.SegmentCallCount);
    }

    [Fact]
    public async Task OrderedProviderFallsBackWhenSegmentsAreReturnedUnchanged()
    {
        var first = new StubTranslationProvider(
            "LocalLargeModel",
            segmentResult: new TranslationSegmentsResult(
                true,
                ["Connect", "Channel"],
                null));
        var second = new StubTranslationProvider(
            "OpenAICompatible",
            segmentResult: new TranslationSegmentsResult(
                true,
                ["连接", "频道"],
                null));
        var provider = new OrderedTranslationProvider([first, second]);

        var result = await provider.TranslateSegmentsAsync(
            ["Connect", "Channel"],
            "auto",
            "zh-Hans");

        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.Equal(["连接", "频道"], result.Segments);
        Assert.Equal(1, first.SegmentCallCount);
        Assert.Equal(1, second.SegmentCallCount);
    }

    [Fact]
    public async Task OrderedProviderFallsBackWhenSingleTextIsReturnedUnchanged()
    {
        var first = new StubTranslationProvider(
            "LocalLargeModel",
            textResult: new TranslationResult(true, "Connect.", null));
        var second = new StubTranslationProvider(
            "OpenAICompatible",
            textResult: new TranslationResult(true, "连接", null));
        var provider = new OrderedTranslationProvider([first, second]);

        var result = await provider.TranslateAsync(
            "Connect",
            "auto",
            "zh-Hans");

        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.Equal("连接", result.Text);
        Assert.Equal(1, first.TextCallCount);
        Assert.Equal(1, second.TextCallCount);
    }

    [Fact]
    public async Task OrderedProviderAcceptsUnchangedTextAlreadyInTargetLanguage()
    {
        var first = new StubTranslationProvider(
            "First",
            textResult: new TranslationResult(true, "已经是中文", null));
        var second = new StubTranslationProvider(
            "Second",
            textResult: new TranslationResult(true, "不应调用", null));
        var provider = new OrderedTranslationProvider([first, second]);

        var result = await provider.TranslateAsync(
            "已经是中文",
            "auto",
            "zh-Hans");

        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.Equal("已经是中文", result.Text);
        Assert.Equal(1, first.TextCallCount);
        Assert.Equal(0, second.TextCallCount);
    }

    [Fact]
    public async Task OrderedProviderAcceptsPartialTranslationWithUnchangedProductName()
    {
        var first = new StubTranslationProvider(
            "First",
            segmentResult: new TranslationSegmentsResult(
                true,
                ["连接", "IDC Flare"],
                null));
        var second = new StubTranslationProvider(
            "Second",
            segmentResult: new TranslationSegmentsResult(
                true,
                ["不应调用", "不应调用"],
                null));
        var provider = new OrderedTranslationProvider([first, second]);

        var result = await provider.TranslateSegmentsAsync(
            ["Connect", "IDC Flare"],
            "auto",
            "zh-Hans");

        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.Equal(["连接", "IDC Flare"], result.Segments);
        Assert.Equal(1, first.SegmentCallCount);
        Assert.Equal(0, second.SegmentCallCount);
    }

    [Fact]
    public async Task OrderedProviderFallsBackWhenEnglishTranslationStillContainsChinese()
    {
        var first = new StubTranslationProvider(
            "OfflineBergamot",
            segmentResult: new TranslationSegmentsResult(
                true,
                ["Repair Cut Window", "横纵滚动条 The lower-right white box disappears"],
                null));
        var second = new StubTranslationProvider(
            "OpenAICompatible",
            segmentResult: new TranslationSegmentsResult(
                true,
                ["Crop window fixes", "The lower-right white box disappears with the scrollbars"],
                null));
        var provider = new OrderedTranslationProvider([first, second]);

        var result = await provider.TranslateSegmentsAsync(
            ["已修复裁剪窗口", "横纵滚动条交叉产生的右下角白框同步消失"],
            "auto",
            "en");

        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.Equal("Crop window fixes", result.Segments[0]);
        Assert.Equal(1, first.SegmentCallCount);
        Assert.Equal(1, second.SegmentCallCount);
    }

    [Fact]
    public async Task OrderedProviderTimesOutAStuckProviderWithoutBlockingTheCaller()
    {
        var first = new NeverCompletingTranslationProvider("OfflineBergamot");
        var second = new StubTranslationProvider(
            "OpenAICompatible",
            segmentResult: new TranslationSegmentsResult(true, ["translated"], null));
        var provider = new OrderedTranslationProvider(
            [first, second],
            offlineTimeout: TimeSpan.FromMilliseconds(80),
            onlineTimeout: TimeSpan.FromSeconds(1));

        var result = await provider.TranslateSegmentsAsync(
            ["source"],
            "auto",
            "en");

        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.Equal(["translated"], result.Segments);
        Assert.True(first.WasCancelled);
        Assert.Equal(1, second.SegmentCallCount);
    }

    [Fact]
    public async Task OrderedProviderStopsAfterTheFirstSuccessfulTranslation()
    {
        var first = new StubTranslationProvider(
            "First",
            textResult: new TranslationResult(true, "译文", null));
        var second = new StubTranslationProvider(
            "Second",
            textResult: new TranslationResult(true, "不应调用", null));
        var provider = new OrderedTranslationProvider([first, second]);

        var result = await provider.TranslateAsync(
            "source",
            "auto",
            "zh-Hans");

        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.Equal("译文", result.Text);
        Assert.Equal(1, first.TextCallCount);
        Assert.Equal(0, second.TextCallCount);
    }

    [Fact]
    public async Task OrderedProviderReportsAllFailures()
    {
        var provider = new OrderedTranslationProvider([
            new StubTranslationProvider(
                "First",
                textResult: TranslationResult.Failure("超时")),
            new StubTranslationProvider(
                "Second",
                textResult: TranslationResult.Failure("未安装模型")),
        ]);

        var result = await provider.TranslateAsync(
            "source",
            "auto",
            "zh-Hans");

        Assert.False(result.IsSuccess);
        Assert.Contains("First：超时", result.ErrorMessage);
        Assert.Contains("Second：未安装模型", result.ErrorMessage);
    }

    [Fact]
    public async Task OrderedProviderDoesNotTryAnotherProviderAfterCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var first = new StubTranslationProvider(
            "First",
            textHandler: _ =>
            {
                cancellation.Cancel();
                return TranslationResult.Failure("翻译已取消");
            });
        var second = new StubTranslationProvider(
            "Second",
            textResult: new TranslationResult(true, "不应调用", null));
        var provider = new OrderedTranslationProvider([first, second]);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            provider.TranslateAsync(
                "source",
                "auto",
                "zh-Hans",
                cancellation.Token));
        Assert.Equal(0, second.TextCallCount);
    }

    [Fact]
    public void ResolvesAnEmptyProviderInsideTheAutomaticChain()
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

        var ordered = Assert.IsType<OrderedTranslationProvider>(provider);
        Assert.Equal(
            TranslationProviderFactory.OpenAiCompatibleProviderId,
            ordered.ProviderIds[0]);
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

    private sealed class IncompleteBatchFallbackHandler : HttpMessageHandler
    {
        private int _requestCount;

        public int RequestCount => _requestCount;

        public List<string> SentContents { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var requestBody = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            lock (SentContents)
            {
                SentContents.Add(requestBody);
            }

            Interlocked.Increment(ref _requestCount);
            var responseText = requestBody.Contains(
                "\\\"segments\\\"",
                StringComparison.Ordinal)
                ? "{\"translations\":[{\"id\":0,\"text\":\"你好\"}]}"
                : requestBody.Contains("hello", StringComparison.Ordinal)
                    ? "你好"
                    : "世界";
            var responseBody = JsonSerializer.Serialize(new
            {
                choices = new[]
                {
                    new { message = new { content = responseText } },
                },
            });
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    responseBody,
                    Encoding.UTF8,
                    "application/json"),
            };
        }
    }

    private sealed class StubTranslationProvider : ITranslationProvider
    {
        private readonly Func<CancellationToken, TranslationResult> _textHandler;
        private readonly Func<CancellationToken, TranslationSegmentsResult>
            _segmentHandler;

        public StubTranslationProvider(
            string id,
            TranslationResult? textResult = null,
            TranslationSegmentsResult? segmentResult = null,
            Func<CancellationToken, TranslationResult>? textHandler = null)
        {
            Id = id;
            _textHandler = textHandler ?? (_ => textResult ??
                TranslationResult.Failure("未配置测试结果"));
            _segmentHandler = _ => segmentResult ??
                TranslationSegmentsResult.Failure("未配置测试结果");
        }

        public string Id { get; }

        public int TextCallCount { get; private set; }

        public int SegmentCallCount { get; private set; }

        public Task<TranslationResult> TranslateAsync(
            string text,
            string sourceLanguage,
            string targetLanguage,
            CancellationToken cancellationToken = default)
        {
            TextCallCount++;
            return Task.FromResult(_textHandler(cancellationToken));
        }

        public Task<TranslationSegmentsResult> TranslateSegmentsAsync(
            IReadOnlyList<string> segments,
            string sourceLanguage,
            string targetLanguage,
            CancellationToken cancellationToken = default)
        {
            SegmentCallCount++;
            return Task.FromResult(_segmentHandler(cancellationToken));
        }
    }

    private sealed class NeverCompletingTranslationProvider(string id)
        : ITranslationProvider
    {
        public string Id { get; } = id;

        public bool WasCancelled { get; private set; }

        public Task<TranslationResult> TranslateAsync(
            string text,
            string sourceLanguage,
            string targetLanguage,
            CancellationToken cancellationToken = default)
        {
            return WaitForCancellationAsync(cancellationToken);
        }

        public async Task<TranslationSegmentsResult> TranslateSegmentsAsync(
            IReadOnlyList<string> segments,
            string sourceLanguage,
            string targetLanguage,
            CancellationToken cancellationToken = default)
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return TranslationSegmentsResult.Failure("不应完成");
            }
            catch (OperationCanceledException)
            {
                WasCancelled = true;
                throw;
            }
        }

        private async Task<TranslationResult> WaitForCancellationAsync(
            CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return TranslationResult.Failure("不应完成");
            }
            catch (OperationCanceledException)
            {
                WasCancelled = true;
                throw;
            }
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
