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
    public async Task OrderedProviderRejectsHexGarbageAndFallsBack()
    {
        var qwen = new StubTranslationProvider(
            TranslationProviderFactory.LocalLargeModelProviderId,
            textResult: new TranslationResult(
                true,
                "5E76E628A77ED53E5876DF763",
                null));
        var bergamot = new StubTranslationProvider(
            TranslationProviderFactory.OfflineProviderId,
            textResult: new TranslationResult(
                true,
                "The target language is saved immediately.",
                null));
        var provider = new OrderedTranslationProvider([qwen, bergamot]);

        var result = await provider.TranslateAsync(
            "目标语言会立即保存。",
            "auto",
            "en");

        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.Equal(
            "The target language is saved immediately.",
            result.Text);
        Assert.Equal(1, qwen.TextCallCount);
        Assert.Equal(1, bergamot.TextCallCount);
    }

    [Fact]
    public void RejectsChineseAndHexGarbageAsEnglishTranslations()
    {
        Assert.False(OrderedTranslationProvider.HasPlausibleTargetLanguage(
            ["目标语言会立即保存。"],
            ["目标语言会立即保存。"],
            "en"));
        Assert.False(OrderedTranslationProvider.HasPlausibleTargetLanguage(
            ["目标语言会立即保存。"],
            ["5E76E628A77ED53E5876DF763"],
            "en"));
        Assert.True(OrderedTranslationProvider.HasPlausibleTargetLanguage(
            ["目标语言会立即保存。"],
            ["The target language is saved immediately."],
            "en"));
    }

    [Fact]
    public void RejectsImplausiblyShortOrWrongScriptJapaneseTranslation()
    {
        const string source =
            "修正截图翻译失败时原文已经是目标语言的误导提示。";

        Assert.False(OrderedTranslationProvider.HasPlausibleTargetLanguage(
            [source],
            ["2023年6月1日"],
            "ja"));
        Assert.False(OrderedTranslationProvider.HasPlausibleTargetLanguage(
            [source],
            ["これは誤訳ではありません".Replace("は", string.Empty)
                .Replace("れ", string.Empty)
                .Replace("で", string.Empty)
                .Replace("あ", string.Empty)
                .Replace("り", string.Empty)
                .Replace("ま", string.Empty)
                .Replace("せ", string.Empty)
                .Replace("ん", string.Empty)],
            "ja"));
        Assert.True(OrderedTranslationProvider.HasPlausibleTargetLanguage(
            [source],
            ["スクリーンショット翻訳失敗時の誤解を招く表示を修正します。"],
            "ja"));
    }

    [Fact]
    public void TechnicalScreenshotLabelsMayRemainUnchanged()
    {
        string[] source =
        [
            "OrderedTranslationProvider.cs",
            "src/Screenshot.App/Capture/AutomaticImageOverlapMatcher.cs +2240",
            "AutomaticViewportFingerprint",
            "��",
        ];

        Assert.True(OrderedTranslationProvider.HasMeaningfulTranslation(
            source,
            source,
            "zh-Hans"));
        Assert.True(OrderedTranslationProvider.HasPlausibleTargetLanguage(
            source,
            source,
            "zh-Hans"));
    }

    [Fact]
    public void OfflineDetectorUsesChineseScriptForShortMixedTechnicalText()
    {
        var result = Cld3OfflineLanguageDetector.Shared.Detect(
            "另外看离线/本机模型的线程配置，把 CPU 用满。");

        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.Equal("zh", result.LanguageCode);
    }

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
                "iOS 系统 WebKit 内核",
                "生成 Word 文档",
                "Connect",
            ],
            "auto",
            "zh-Hans");

        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.Equal(
            [
                "MySQL+PG_TD安装说明文档",
                "Ubuntu+Debian密码重置",
                "iOS 系统 WebKit 内核",
                "生成 Word 文档",
                "连接",
            ],
            result.Segments);
        Assert.DoesNotContain("MySQL", handler.RequestBody);
        Assert.DoesNotContain("Ubuntu", handler.RequestBody);
        Assert.DoesNotContain("WebKit", handler.RequestBody);
        Assert.DoesNotContain("Word", handler.RequestBody);
    }

    [Fact]
    public void NaturalLanguageSentencesAreNotMistakenForTechnicalTokens()
    {
        string[] sentences =
        [
            "Software creation is changing. We have much to learn and build.",
            "Companies need residential IP addresses for ad verification.",
            "Manages Python, Java/JDK, .NET SDK, and the Android toolchain.",
            "Shows PATH entries, version conflicts, backups, and diagnostics.",
        ];

        Assert.All(sentences, sentence =>
        {
            Assert.False(TranslationTargetLanguageMatcher.IsAlreadyTargetLanguage(
                sentence,
                "zh-Hans"));
            Assert.False(OrderedTranslationProvider.HasMeaningfulTranslation(
                [sentence],
                [sentence],
                "zh-Hans"));
        });
        Assert.True(TranslationTargetLanguageMatcher.IsAlreadyTargetLanguage(
            "Grok 4.5",
            "zh-Hans"));
    }

    [Fact]
    public void TranslationTermsAreProtectedByShapeAndOccurrence()
    {
        string[] source =
        [
            "AcmeWidget is a desktop application built with Rust and Tauri 2.",
            "AcmeWidget supports PATH, .NET SDK, and python-install.",
        ];

        var protector = TranslationTermProtector.Create(source);

        Assert.DoesNotContain("AcmeWidget", string.Join(' ', protector.Segments));
        Assert.Contains("Tauri", protector.Segments[0]);
        Assert.Contains("desktop application", protector.Segments[0]);
        var restored = protector.Restore(
            1,
            protector.Segments[1]
                .Replace("supports", "支持", StringComparison.Ordinal)
                .Replace("and", "和", StringComparison.Ordinal));
        Assert.StartsWith("AcmeWidget 支持", restored, StringComparison.Ordinal);
        Assert.Contains("PATH", restored, StringComparison.Ordinal);
        Assert.Contains(".NET SDK", restored, StringComparison.Ordinal);
        Assert.Contains("python-install", restored, StringComparison.Ordinal);
    }

    [Fact]
    public void RestoresMinorTechnicalTokenSpellingChanges()
    {
        const string source =
            "DeepSeek and env-diagnose work with Anthropic/Claude and Gemini.";
        const string translated =
            "DeepSeok 和 env-dianose 可与 Anthropic/Delaude 及 Gemii 配合使用。";

        var restored = TranslationTechnicalTokenRestorer.Restore(
            source,
            translated);

        Assert.Equal(
            "DeepSeek 和 env-diagnose 可与 Anthropic/Claude 及 Gemini 配合使用。",
            restored);
    }

    [Fact]
    public void MixedPageMayKeepAmbiguousShortLabelsAfterTranslatingProse()
    {
        string[] source =
        [
            "Cursor Desktop",
            "Acme Labs",
            "Software creation is changing for everyone.",
            "Sema",
        ];
        string[] translated =
        [
            "Cursor Desktop",
            "Acme Labs",
            "软件创作正在改变每一个人。",
            "Sema",
        ];

        Assert.True(OrderedTranslationProvider.HasMeaningfulTranslation(
            source,
            translated,
            "zh-Hans"));
        Assert.True(OrderedTranslationProvider.HasPlausibleTargetLanguage(
            source,
            translated,
            "zh-Hans"));
        Assert.False(OrderedTranslationProvider.HasMeaningfulTranslation(
            ["Connect"],
            ["Connect"],
            "zh-Hans"));
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
    public async Task ReportsInsufficientBalanceAsAnActionableProviderError()
    {
        var handler = new RecordingHandler(
            """{"error":{"message":"Insufficient Balance"}}""",
            HttpStatusCode.PaymentRequired);
        using var client = new HttpClient(handler);
        var provider = new OpenAiCompatibleTranslationProvider(
            "https://api.deepseek.com",
            "deepseek-v4-flash",
            "test-key",
            client);

        var result = await provider.TranslateAsync("hello", "en", "zh-Hans");

        Assert.False(result.IsSuccess);
        Assert.Equal(
            "在线翻译账户余额不足，请充值或切换到离线翻译。",
            result.ErrorMessage);
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
    public void QwenSelectionKeepsBergamotAsTheFirstOfflineTranslator()
    {
        var settings = AppSettings.CreateDefault() with
        {
            OfflineTranslationEngine = OfflineTranslationEngine.QwenLargeModel,
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
                TranslationProviderFactory.LocalLargeModelProviderId,
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
    public async Task OrderedProviderDoesNotSkipEnglishInChineseMixedLine()
    {
        var first = new StubTranslationProvider(
            "First",
            segmentResult: new TranslationSegmentsResult(
                true,
                ["服务状态 Service is running"],
                null));
        var second = new StubTranslationProvider(
            "Second",
            segmentResult: new TranslationSegmentsResult(
                true,
                ["服务状态 服务正在运行"],
                null));
        var provider = new OrderedTranslationProvider([first, second]);

        var result = await provider.TranslateSegmentsAsync(
            ["服务状态 Service is running"],
            "auto",
            "zh-Hans");

        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.Equal(["服务状态 服务正在运行"], result.Segments);
        Assert.Equal(1, first.SegmentCallCount);
        Assert.Equal(1, second.SegmentCallCount);
    }

    [Fact]
    public async Task OrderedProviderRejectsAResultThatTranslatesOnlyAFewLines()
    {
        var source = Enumerable.Range(1, 20)
            .Select(index => $"English sentence {index}")
            .ToArray();
        var complete = source.Select((_, index) => $"完整译文 {index + 1}").ToArray();
        var first = new StubTranslationProvider(
            "First",
            segmentsHandler: segments => new TranslationSegmentsResult(
                true,
                segments
                    .Select((segment, index) => index < 2 ? $"英文句子 {index + 1}" : segment)
                    .ToArray(),
                null));
        var second = new StubTranslationProvider(
            "Second",
            segmentsHandler: segments => new TranslationSegmentsResult(
                true,
                segments
                    .Select((_, index) => $"完整译文 {index + 1}")
                    .ToArray(),
                null));
        var provider = new OrderedTranslationProvider([first, second]);

        var result = await provider.TranslateSegmentsAsync(
            source,
            "auto",
            "zh-Hans");

        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.Equal(complete, result.Segments);
        Assert.Equal(1, first.SegmentCallCount);
        Assert.Equal(1, second.SegmentCallCount);
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
    public async Task OrderedProviderProbesAFailedProviderOnlyOnceAcrossBatches()
    {
        // 90 segments split into batches of at most 24. The failing first
        // provider must be probed exactly once for the whole request, not
        // once per batch — re-probing a dead timeout provider per batch made
        // full-screen translations look like a hard freeze.
        var source = Enumerable.Range(1, 90)
            .Select(index => $"English sentence {index}")
            .ToArray();
        var first = new StubTranslationProvider(
            "LocalLargeModel",
            segmentsHandler: _ =>
                TranslationSegmentsResult.Failure("连接失败"));
        var second = new StubTranslationProvider(
            "OpenAICompatible",
            segmentsHandler: segments => new TranslationSegmentsResult(
                true,
                segments.Select(segment => "译文 " + segment).ToArray(),
                null));
        var provider = new OrderedTranslationProvider([first, second]);

        var result = await provider.TranslateSegmentsAsync(
            source,
            "auto",
            "zh-Hans");

        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.Equal(90, result.Segments.Count);
        Assert.All(
            result.Segments.Select((text, index) => (text, index)),
            item => Assert.Equal($"译文 English sentence {item.index + 1}", item.text));
        Assert.Equal(0, first.SegmentCallCount);
        Assert.Equal(4, second.SegmentCallCount);
    }

    [Fact]
    public async Task OrderedProviderReusesTheWinnerForEveryLaterBatch()
    {
        var source = Enumerable.Range(1, 90)
            .Select(index => $"English sentence {index}")
            .ToArray();
        var first = new StubTranslationProvider(
            "OpenAICompatible",
            segmentsHandler: segments => new TranslationSegmentsResult(
                true,
                segments.Select(segment => "译文 " + segment).ToArray(),
                null));
        var second = new StubTranslationProvider(
            "OfflineBergamot",
            segmentsHandler: _ =>
                TranslationSegmentsResult.Failure("不应调用"));
        var provider = new OrderedTranslationProvider([first, second]);

        var result = await provider.TranslateSegmentsAsync(
            source,
            "auto",
            "zh-Hans");

        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.Equal(4, first.SegmentCallCount);
        // A successful online provider remains the winner; the offline
        // fallback is not probed again for every later full-screen batch.
        Assert.Equal(0, second.SegmentCallCount);
    }

    [Fact]
    public async Task OrderedProviderSplitsATimedOutBatchInsteadOfFailingTheCapture()
    {
        var source = Enumerable.Range(1, 8)
            .Select(index => $"English sentence {index}")
            .ToArray();
        var calls = 0;
        var online = new StubTranslationProvider(
            "OpenAICompatible",
            segmentsHandler: segments =>
            {
                Interlocked.Increment(ref calls);
                if (segments.Count > 4)
                {
                    return TranslationSegmentsResult.Failure(
                        "翻译超时，已切换到下一种翻译方式");
                }

                return new TranslationSegmentsResult(
                    true,
                    segments.Select(segment => "译文 " + segment).ToArray(),
                    null);
            });
        var provider = new OrderedTranslationProvider([online]);

        var result = await provider.TranslateSegmentsAsync(
            source,
            "auto",
            "zh-Hans");

        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.Equal(8, result.Segments.Count);
        Assert.All(
            result.Segments.Select((text, index) => (text, index)),
            item => Assert.Equal($"译文 English sentence {item.index + 1}", item.text));
        Assert.True(calls >= 3, $"expected split retries, got {calls} calls");
    }

    [Fact]
    public async Task OrderedProviderSplitsIncompleteOnlineBatchAndKeepsProvider()
    {
        var source = Enumerable.Range(1, 12)
            .Select(index => $"English sentence {index}")
            .ToArray();
        var online = new StubTranslationProvider(
            TranslationProviderFactory.OpenAiCompatibleProviderId,
            segmentsHandler: segments => segments.Count > 6
                ? TranslationSegmentsResult.Failure(
                    "翻译服务返回的分段结果不完整")
                : new TranslationSegmentsResult(
                    true,
                    segments.Select(segment => "译文 " + segment).ToArray(),
                    null));
        var provider = new OrderedTranslationProvider([online]);

        var result = await provider.TranslateSegmentsAsync(
            source,
            "auto",
            "zh-Hans");

        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.Equal(12, result.Segments.Count);
        Assert.Equal(3, online.SegmentCallCount);
    }

    [Fact]
    public async Task OrderedProviderKeepsTranslatedLinesWhenLaterBatchesFail()
    {
        var source = Enumerable.Range(1, 48)
            .Select(index => $"English sentence {index}")
            .ToArray();
        var call = 0;
        var online = new StubTranslationProvider(
            "OpenAICompatible",
            segmentsHandler: segments =>
            {
                var attempt = Interlocked.Increment(ref call);
                if (attempt == 1)
                {
                    return new TranslationSegmentsResult(
                        true,
                        segments.Select(segment => "译文 " + segment).ToArray(),
                        null);
                }

                return TranslationSegmentsResult.Failure("服务不可用");
            });
        var provider = new OrderedTranslationProvider([online]);

        var result = await provider.TranslateSegmentsAsync(
            source,
            "auto",
            "zh-Hans");

        Assert.True(result.IsSuccess);
        Assert.Contains("部分行翻译失败", result.ErrorMessage);
        Assert.StartsWith("译文 ", result.Segments[0]);
        Assert.Equal(source[^1], result.Segments[^1]);
    }

    [Fact]
    public async Task OrderedProviderDefersLocalLargeModelForLargeCaptures()
    {
        var source = Enumerable.Range(1, 24)
            .Select(index => $"English sentence {index}")
            .ToArray();
        var qwen = new StubTranslationProvider(
            TranslationProviderFactory.LocalLargeModelProviderId,
            segmentsHandler: _ =>
                TranslationSegmentsResult.Failure("不应优先调用"));
        var online = new StubTranslationProvider(
            "OpenAICompatible",
            segmentsHandler: segments => new TranslationSegmentsResult(
                true,
                segments.Select(segment => "译文 " + segment).ToArray(),
                null));
        var provider = new OrderedTranslationProvider([qwen, online]);

        var result = await provider.TranslateSegmentsAsync(
            source,
            "auto",
            "zh-Hans");

        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.Equal(1, qwen.SegmentCallCount);
        Assert.Equal(1, online.SegmentCallCount);
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
        private readonly Func<IReadOnlyList<string>, TranslationSegmentsResult>
            _segmentHandler;
        private int _segmentCallCount;

        public StubTranslationProvider(
            string id,
            TranslationResult? textResult = null,
            TranslationSegmentsResult? segmentResult = null,
            Func<CancellationToken, TranslationResult>? textHandler = null,
            Func<IReadOnlyList<string>, TranslationSegmentsResult>?
                segmentsHandler = null)
        {
            Id = id;
            _textHandler = textHandler ?? (_ => textResult ??
                TranslationResult.Failure("未配置测试结果"));
            _segmentHandler = segmentsHandler ?? (_ => segmentResult ??
                TranslationSegmentsResult.Failure("未配置测试结果"));
        }

        public string Id { get; }

        public int TextCallCount { get; private set; }

        public int SegmentCallCount => _segmentCallCount;

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
            Interlocked.Increment(ref _segmentCallCount);
            return Task.FromResult(_segmentHandler(segments));
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
