using System.Drawing;
using System.Drawing.Imaging;
using Screenshot.App.Text;

namespace Screenshot.App.Tests;

public sealed class WebTranslationRegressionTests
{
    [Fact]
    public void InlineCodeWithDifferentTopJoinsInReadingOrderWithoutShrinkingBody()
    {
        var regions = TranslationPresentationLayout.GroupParagraphs([
            new("Models stay in", 20, 10, 180, 24) { EstimatedFontSize = 22 },
            new("beside", 185, 39, 54, 20) { EstimatedFontSize = 16 },
            new("and survive updates.", 370, 39, 290, 24) { EstimatedFontSize = 22 },
            new("TranslationModels", 20, 43, 152, 16) { EstimatedFontSize = 13 },
            new("SnapCut.exe", 250, 44, 106, 18) { EstimatedFontSize = 15 },
        ]);
        var region = Assert.Single(regions);
        Assert.Equal("Models stay in TranslationModels beside SnapCut.exe and survive updates.", region.Text);
        Assert.Equal(22, region.EstimatedFontSize);
    }

    [Fact]
    public void ParagraphInkKeepsAllRowsAndReadableFont()
    {
        using var bitmap = new Bitmap(240, 140);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.White);
            for (var y = 20; y <= 100; y += 20)
                for (var x = 15; x < 205; x += 12) graphics.FillRectangle(Brushes.Black, x, y, 4, 12);
        }
        var source = string.Join(" ", Enumerable.Repeat("A paragraph with several lines.", 5));
        var region = Assert.Single(TranslationInkBounds.Refine(bitmap, [new(source, 10, 15, 210, 105)]));
        Assert.InRange(region.Height, 92, 100);
        Assert.InRange(region.EstimatedFontSize, 12, 16);
        Assert.Equal(source, region.Text);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NarrowCjkColumnsNeedRepeatedGutterEvidence(bool secondLine)
    {
        var regions = new List<OcrTextRegion>();
        var words = new List<OcrWordRegion>();
        AddLine("古い町カフェ", 20);
        if (secondLine) AddLine("新たな業態店", 45);
        var result = TranslationPresentationLayout.GroupParagraphs(
            TranslationPresentationLayout.TightenToWordBounds(regions, words));
        if (secondLine)
        {
            Assert.Equal(2, result.Count);
            Assert.Contains(result, region => region.Text == "古い町 新たな");
            Assert.Contains(result, region => region.Text == "カフェ 業態店");
        }
        else Assert.Equal("古い町カフェ", Assert.Single(result).Text);

        void AddLine(string text, double y)
        {
            regions.Add(new OcrTextRegion(text, 20, y, 78, 20) { EstimatedFontSize = 18 });
            for (var index = 0; index < text.Length; index++)
                words.Add(new OcrWordRegion(text[index].ToString(), 20 + index * 10 + (index >= 3 ? 18 : 0), y, 10, 20));
        }
    }

    [Theory]
    [InlineData("1 Comment")]
    [InlineData("1274 participants")]
    public void CountsAreNotMistakenForUnits(string source) =>
        Assert.False(TranslationTargetLanguageMatcher.IsLikelyInvariant(source));

    [Fact]
    public void InterleavedColumnsKeepTheirOwnParagraphContext()
    {
        var result = TranslationPresentationLayout.GroupParagraphs([
            new("The body starts here", 300, 100, 550, 20),
            new("A sidebar question", 950, 101, 240, 18),
            new("and continues here.", 300, 124, 480, 20),
            new("continues on its next line?", 950, 123, 240, 18),
            new("A different question?", 950, 165, 240, 18),
        ]);
        Assert.Equal(3, result.Count);
        Assert.Contains(result, region => region.Text == "The body starts here and continues here.");
        Assert.Contains(result, region => region.Text == "A sidebar question continues on its next line?");
        Assert.DoesNotContain(result, region => region.Width > 600);
    }

    [Theory]
    [InlineData(PixelFormat.Format32bppArgb)]
    [InlineData(PixelFormat.Format32bppPArgb)]
    public void PaddedOcrBoxesDoNotDetermineTheOverlayFontSize(PixelFormat format)
    {
        using var bitmap = new Bitmap(220, 100, format);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.White);
            graphics.FillRectangle(Brushes.Blue, 15, 30, 100, 12);
            graphics.FillRectangle(Brushes.Blue, 15, 60, 100, 12);
        }
        var region = Assert.Single(TranslationInkBounds.Refine(bitmap, [new("Text", 10, 20, 120, 32)]));
        Assert.InRange(region.Y, 28, 30);
        Assert.InRange(region.Height, 12, 16);
        Assert.InRange(region.EstimatedFontSize, 12, 16);
    }

    [Theory]
    [InlineData("answered Sep 10, 2009 at 17:43", "回答于2009年9月10日17:43")]
    [InlineData("Are both 「に」 and 「の」 valid?", "「に」和「の」都正确吗？")]
    public void ValidDatesAndQuotedLanguageExamplesAreNotRejected(string source, string target) =>
        Assert.Null(TranslationCompletenessValidator.Validate(source, target, "zh-Hans"));

    [Fact]
    public void AFailedPartCannotDiscardOtherCompletedParagraphs()
    {
        var result = LocalLargeModelTranslationProvider.CombineParts(
            ["First paragraph", "Second paragraph", "Third paragraph"], [0, 1, 1, 2],
            ["第一段", "第二段上半", null, "第三段"], [null, null, "数字变化", null], null);
        Assert.Equal(["第一段", "Second paragraph", "第三段"], result.Segments);
        Assert.Equal([1], result.FailedSegmentIndices);
        Assert.Contains("数字变化", result.ErrorMessage);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResourceFailureRetainsItsCauseWhenRoutingToTheNextConfiguredProvider(bool fallback)
    {
        const string cause = "可用内存不足，已停止本机加载；请在设置 → 翻译选择在线翻译或轻量模型。";
        var offline = new Provider(TranslationProviderFactory.LocalLargeModelProviderId, input =>
            LocalLargeModelTranslationProvider.CombineParts(input, [0], [null], [null], cause));
        var onlineCalls = 0;
        var online = new Provider(TranslationProviderFactory.OpenAiCompatibleProviderId, input =>
        {
            onlineCalls++;
            return new(true, ["Please open the settings."], null);
        });
        var failed = await offline.TranslateSegmentsAsync(["请打开设置。"], "zh", "en");
        Assert.False(failed.IsSuccess);
        var result = await new OrderedTranslationProvider(fallback ? [offline, online] : [offline])
            .TranslateSegmentsAsync(["请打开设置。"], "zh", "en");
        Assert.Contains(cause, fallback ? result.Notice : result.ErrorMessage);
        Assert.Equal(fallback ? 1 : 0, onlineCalls);
        if (fallback) Assert.Equal("Please open the settings.", Assert.Single(result.Segments));
    }

    [Fact]
    public async Task FallbackOnlyReceivesFailedOriginalSegmentsAndExplainsTheRoute()
    {
        var order = new List<string>();
        var offline = new Provider(TranslationProviderFactory.LocalLargeModelProviderId, input =>
        {
            order.Add("offline");
            return new(true, ["第一段", input[1], "第三段", "第四段"], "本机离线模型超时")
            { FailedSegmentIndices = [1] };
        });
        var online = new Provider(TranslationProviderFactory.OpenAiCompatibleProviderId, input =>
        {
            order.Add("online");
            Assert.Equal(["Second paragraph"], input);
            return new(true, ["第二段"], null);
        });
        var result = await new OrderedTranslationProvider([offline, online]).TranslateSegmentsAsync(
            ["First paragraph", "Second paragraph", "Third paragraph", "Fourth paragraph"], "en", "zh-Hans");
        Assert.Equal(["offline", "online"], order);
        Assert.Equal(["第一段", "第二段", "第三段", "第四段"], result.Segments);
        Assert.Null(result.ErrorMessage);
        Assert.Contains("本机离线模型超时", result.Notice);
        Assert.Contains("按配置顺序", result.Notice);
        Assert.Contains("在线模型", result.Notice);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HeadingAndLongOcrBodyReachLocalSchedulerTogetherInConfiguredOrder(bool firstFails)
    {
        var source = new[] { "Why SnapCut", string.Join(" ", Enumerable.Repeat("Please save this document before closing the window.", 40)) };
        var calls = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var first = new Provider(TranslationProviderFactory.OpenAiCompatibleProviderId, input =>
        {
            calls.Enqueue("online");
            return firstFails ? TranslationSegmentsResult.Failure("network unavailable") :
                new(true, input.Select(_ => "已翻译的段落").ToArray(), null);
        });
        var local = new Provider(TranslationProviderFactory.LocalLargeModelProviderId, input =>
        {
            calls.Enqueue("local");
            Assert.Equal(source, input);
            return new(true, ["为什么选择 SnapCut", "关闭窗口前请保存此文档。"], null);
        });
        var result = await new OrderedTranslationProvider([first, local])
            .TranslateSegmentsAsync(source, "en", "zh-Hans");
        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.Equal(firstFails ? new[] { "online", "local" } : ["online"], calls.Distinct());
        Assert.Equal(firstFails ? 1 : 0, calls.Count(route => route == "local"));
        Assert.Equal(2, result.Segments.Count);
        if (firstFails) Assert.Contains("network unavailable", result.Notice);
    }

    private sealed class Provider(string id, Func<IReadOnlyList<string>, TranslationSegmentsResult> translate) : ITranslationProvider
    {
        public string Id => id;
        public Task<TranslationSegmentsResult> TranslateSegmentsAsync(IReadOnlyList<string> segments,
            string sourceLanguage, string targetLanguage, CancellationToken cancellationToken = default) =>
            Task.FromResult(translate(segments));
        public Task<TranslationResult> TranslateAsync(string text, string sourceLanguage, string targetLanguage,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    [Fact]
    public async Task TheOverallDeadlineKeepsCompletedOfflineParagraphs()
    {
        var offline = new Provider(TranslationProviderFactory.LocalLargeModelProviderId,
            input => new(true, ["第一段", input[1], "第三段", "第四段"], "第二段未完成")
            { FailedSegmentIndices = [1] });
        var failedOnline = new Provider(TranslationProviderFactory.OpenAiCompatibleProviderId, _ =>
        {
            Thread.Sleep(45);
            return TranslationSegmentsResult.Failure("服务暂不可用");
        });
        var provider = new OrderedTranslationProvider([offline, failedOnline, new HangingProvider()],
            offlineTimeout: TimeSpan.FromSeconds(1), onlineTimeout: TimeSpan.FromSeconds(1),
            translationBudget: TimeSpan.FromMilliseconds(30), onlineTranslationBudget: TimeSpan.FromMilliseconds(60));
        var result = await provider.TranslateSegmentsAsync(
            ["First paragraph", "Second paragraph", "Third paragraph", "Fourth paragraph"], "en", "zh-Hans");
        Assert.Equal(["第一段", "Second paragraph", "第三段", "第四段"], result.Segments);
        Assert.Equal([1], result.FailedSegmentIndices);
        Assert.Contains("时间限制", result.ErrorMessage);
    }

    private sealed class HangingProvider : ITranslationProvider
    {
        public string Id => TranslationProviderFactory.OpenAiCompatibleProviderId;
        public async Task<TranslationSegmentsResult> TranslateSegmentsAsync(IReadOnlyList<string> segments,
            string sourceLanguage, string targetLanguage, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException();
        }
        public Task<TranslationResult> TranslateAsync(string text, string sourceLanguage, string targetLanguage,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
