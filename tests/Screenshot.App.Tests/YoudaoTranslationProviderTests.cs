using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Screenshot.App.Core;
using Screenshot.App.Presentation;
using Screenshot.App.Text;

namespace Screenshot.App.Tests;

public sealed class YoudaoTranslationProviderTests
{
    [Fact]
    public async Task BatchesUnicodeFormFieldsAndSignsTheExactCombinedText()
    {
        string[] source = ["Save & copy this document.", "There are 25 participants."];
        using var handler = new Handler(async request =>
        {
            Assert.Equal(YoudaoTranslationProvider.Endpoint, request.RequestUri!.AbsoluteUri);
            Assert.Null(request.Headers.Authorization);
            var fields = (await request.Content!.ReadAsStringAsync()).Split('&').Select(pair => pair.Split('=', 2))
                .ToLookup(pair => WebUtility.UrlDecode(pair[0]), pair => WebUtility.UrlDecode(pair[1]));
            Assert.Equal(source, fields["q"]);
            Assert.Equal("zh-CHS", fields["to"].Single());
            Assert.Equal("v3", fields["signType"].Single());
            var all = string.Concat(source);
            var input = all[..10] + all.Length + all[^10..];
            Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                "app-id" + input + fields["salt"].Single() + fields["curtime"].Single() + "private-key"))), fields["sign"].Single());
            return Response(new { errorCode = "0", translateResults = new[] {
                new { query = source[1], translation = "有 25 位参与者。" },
                new { query = source[0], translation = "保存并复制此文档。" } } });
        });
        using var client = new HttpClient(handler);
        var result = await new YoudaoTranslationProvider("app-id", "private-key", client).TranslateSegmentsAsync(source, "en", "zh-Hans");
        Assert.Empty(result.FailedSegmentIndices!);
        Assert.Equal(["保存并复制此文档。", "有 25 位参与者。"], result.Segments);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task PartialResponsesKeepMatchingRowsAndRejectChangedNumbers()
    {
        string[] source = ["There are 7 windows.", "There are 25 requests.", "There are 4 users."];
        int[] errorIndices = [1];
        using var handler = new Handler(_ => Task.FromResult(Response(new { errorCode = "0", errorIndex = errorIndices,
            translateResults = new[] { new { query = source[0], translation = "有 7 个窗口。" },
                new { query = source[2], translation = "有 5 位用户。" } } })));
        using var client = new HttpClient(handler);
        var result = await new YoudaoTranslationProvider("id", "key", client).TranslateSegmentsAsync(source, "en", "zh");
        Assert.Equal([1, 2], result.FailedSegmentIndices);
        Assert.Equal(["有 7 个窗口。", source[1], source[2]], result.Segments);
        Assert.Contains("质量不达标", result.ErrorMessage);
    }

    [Theory]
    [InlineData("110", "绑定")]
    [InlineData("202", "凭据")]
    [InlineData("401", "余额")]
    [InlineData("411", "频率")]
    public async Task ReportsActionableServiceErrors(string code, string message)
    {
        using var handler = new Handler(_ => Task.FromResult(Response(new { errorCode = code })));
        using var client = new HttpClient(handler);
        var result = await new YoudaoTranslationProvider("id", "key", client).TranslateAsync("Please save this document.", "en", "zh");
        Assert.False(result.IsSuccess);
        Assert.Contains(message, result.ErrorMessage);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task FactoryUsesThePublicProviderAndCancellationPropagates()
    {
        using var handler = new Handler(async _ => { await Task.Yield(); return Response(new { errorCode = "110" }); });
        using var client = new HttpClient(handler);
        var profile = new AiTranslationProfile { Provider = "Youdao", AppId = "app-id" };
        var provider = TranslationProviderFactory.CreateOnlineProvider(profile, "secret", client);
        Assert.IsType<YoudaoTranslationProvider>(provider);
        var item = new AiTranslationProfileItem(profile);
        item.Name = "改名后仍保留连接";
        Assert.Equal(profile.AppId, item.ToProfile().AppId);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.TranslateAsync("Please save this document.", "en", "zh", cancellation.Token));
    }

    [Theory]
    [InlineData("zh-Hans", "zh-CHS")]
    [InlineData("zh-Hant", "zh-CHT")]
    [InlineData("en-US", "en")]
    [InlineData("auto", "auto")]
    public void MapsLanguageCodes(string source, string expected) => Assert.Equal(expected, YoudaoTranslationProvider.MapLanguage(source));

    [Fact]
    public void PublicConfigurationShowsApplicationFieldsAndKeepsAiModelControlsSeparate()
    {
        WpfTestHost.Invoke(() =>
        {
            var item = new AiTranslationProfileItem(new AiTranslationProfile
                { Provider = "Youdao", AppId = "application-id", Model = "NMT", Endpoint = YoudaoTranslationProvider.Endpoint });
            var window = new TranslationProfileEditorWindow(item, new NoCredentials());
            try
            {
                Assert.Equal("application-id", ((System.Windows.Controls.TextBox)window.FindName("AppIdBox")).Text);
                Assert.Equal(System.Windows.Visibility.Visible, ((System.Windows.Controls.TextBox)window.FindName("AppIdBox")).Visibility);
                Assert.Equal(System.Windows.Visibility.Collapsed, ((System.Windows.Controls.Button)window.FindName("FetchModelsButton")).Visibility);
                Assert.Equal("测试翻译", ((System.Windows.Controls.Button)window.FindName("TestButton")).Content);
                ((System.Windows.Controls.ComboBox)window.FindName("ProviderBox")).SelectedValue = "Zhipu";
                Assert.Equal(System.Windows.Visibility.Collapsed, ((System.Windows.Controls.TextBox)window.FindName("AppIdBox")).Visibility);
                Assert.Equal(System.Windows.Visibility.Visible, ((System.Windows.Controls.ComboBox)window.FindName("ModelBox")).Visibility);
                Assert.Equal("glm-4-flash", ((System.Windows.Controls.ComboBox)window.FindName("ModelBox")).Text);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public async Task LongInputStaysInsideOfficialLimitsAndKeepsDuplicateRows()
    {
        var text = string.Concat(Enumerable.Repeat("Please save this document. ", 210));
        var sizes = new List<int>();
        using var handler = new Handler(async request =>
        {
            var fields = (await request.Content!.ReadAsStringAsync()).Split('&').Select(pair => pair.Split('=', 2))
                .Where(pair => pair[0] == "q").Select(pair => WebUtility.UrlDecode(pair[1])).ToArray();
            sizes.Add(fields.Sum(value => value.Length));
            return Response(new { errorCode = "0", translateResults = fields.Select(query => new
                { query, translation = string.Concat(Enumerable.Repeat("请保存此文档。", query.Length / 27 + 1)) }).ToArray() });
        });
        using var client = new HttpClient(handler);
        var result = await new YoudaoTranslationProvider("id", "key", client).TranslateSegmentsAsync([text, text], "en", "zh");
        Assert.Empty(result.FailedSegmentIndices!);
        Assert.Equal(result.Segments[0], result.Segments[1]);
        Assert.Equal(2, handler.Calls);
        Assert.All(sizes, size => Assert.InRange(size, 1, YoudaoTranslationProvider.MaximumBatchCharacters));
    }

    private sealed class NoCredentials : ITranslationCredentialStore
    {
        public string? GetApiKey(string providerId) => null;
        public void SetApiKey(string providerId, string? apiKey) => throw new NotSupportedException();
    }

    [Fact]
    public async Task FullScreenshotRouteRetainsPublicBatchingAndModelAttribution()
    {
        var source = Enumerable.Range(1, 65).Select(index => $"There are {index} participants.").ToArray();
        using var handler = new Handler(async request =>
        {
            var text = (await request.Content!.ReadAsStringAsync()).Split('&').Select(pair => pair.Split('=', 2))
                .Where(pair => pair[0] == "q").Select(pair => WebUtility.UrlDecode(pair[1])).ToArray();
            return Response(new { errorCode = "0", translateResults = text.Select(query => new
                { query, translation = "有 " + new string(query.Where(char.IsDigit).ToArray()) + " 位参与者。" }).ToArray() });
        });
        using var client = new HttpClient(handler);
        var provider = new OrderedTranslationProvider([new YoudaoTranslationProvider("id", "key", client)]);
        var result = await provider.TranslateSegmentsAsync(source, "en", "zh-Hans");
        Assert.Null(result.ErrorMessage);
        Assert.Empty(result.FailedSegmentIndices!);
        Assert.Equal(source.Length, result.Segments.Count);
        Assert.Equal(2, handler.Calls);
        Assert.Contains("有道", result.Notice);
    }

    private static HttpResponseMessage Response(object value) => new(HttpStatusCode.OK)
        { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> response) : HttpMessageHandler
    {
        internal int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); Calls++; return response(request); }
    }
}
