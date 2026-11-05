using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Screenshot.App.Core;
using Screenshot.App.Text;

namespace Screenshot.App.Tests;

public sealed class ImageQuestionTests
{
    internal static AiTranslationProfile Profile() => new() { Name = "我的模型", Endpoint = "https://example.invalid/v1", Model = "vision-test" };
    internal sealed class Credentials(string? key = "test-key") : ITranslationCredentialStore
    { public string? GetApiKey(string id) => key; public void SetApiKey(string id, string? value) { } }
    internal sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token);
    }
    internal static HttpResponseMessage Reply() => new(HttpStatusCode.OK)
    { Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"图片包含一段文字。\"},\"finish_reason\":\"stop\"}]}") };
    internal static BitmapSource Pixels()
    {
        var image = BitmapSource.Create(20, 12, 96, 96, PixelFormats.Bgr32, null, new byte[20 * 12 * 4], 80);
        image.Freeze(); return image;
    }

    [Fact]
    public void OnlyConfiguredEnabledOnlineProfilesAreOfferedInOriginalOrder()
    {
        var first = Profile(); var last = Profile();
        var settings = AppSettings.CreateDefault() with { TranslationProfiles = [first,
            Profile() with { Provider = "Baidu" }, Profile() with { Provider = "Youdao" },
            Profile() with { IsEnabled = false }, Profile() with { Model = "" }, last] };
        Assert.Equal(new[] { first.Id, last.Id }, ImageQuestionService.GetProfiles(settings).Select(p => p.Id));
    }

    [Fact]
    public async Task FollowupIncludesExactImageOnceAndFullConversationWithoutChangingModel()
    {
        var calls = 0;
        using var client = new HttpClient(new Handler(async (request, token) =>
        {
            calls++;
            Assert.Equal("https://example.invalid/v1/chat/completions", request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal("test-key", request.Headers.Authorization.Parameter);
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            Assert.Equal("vision-test", json.RootElement.GetProperty("model").GetString());
            var messages = json.RootElement.GetProperty("messages");
            Assert.Equal(4, messages.GetArrayLength());
            Assert.Equal("总结", messages[1].GetProperty("content").GetString());
            var content = messages[3].GetProperty("content");
            Assert.Equal("详细解释", content[0].GetProperty("text").GetString());
            Assert.Equal("data:image/png;base64,AQID", content[1].GetProperty("image_url").GetProperty("url").GetString());
            Assert.Equal("前一轮回答", messages[2].GetProperty("content").GetString());
            return Reply();
        }));
        var result = await new ImageQuestionService(client, new Credentials()).AskAsync(Profile(), [1, 2, 3],
            [new("总结", "前一轮回答")], "详细解释", default);
        Assert.True(result.IsSuccess); Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData(400, false)]
    [InlineData(401, true)]
    [InlineData(402, true)]
    [InlineData(429, true)]
    public async Task ImageIncompatibilityDoesNotDisableTextTranslationButConnectionFailuresDo(int status, bool healthFailure)
    {
        var profile = Profile();
        using var client = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)
        { Content = new StringContent("{}") })));
        var result = await new ImageQuestionService(client, new Credentials()).AskAsync(profile, [1], [], "问题", default);
        Assert.False(result.IsSuccess);
        Assert.Equal(healthFailure, TranslationProfileHealth.Latest(profile) is { IsAvailable: false });
        TranslationProfileHealth.Invalidate(profile.Id);
    }

    [Fact]
    public async Task MissingCredentialsNeverSendsAnImage()
    {
        using var client = new HttpClient(new Handler((_, _) => throw new InvalidOperationException("Unexpected network request")));
        var result = await new ImageQuestionService(client, new Credentials(null)).AskAsync(Profile(), [1], [], "问题", default);
        Assert.False(result.IsSuccess); Assert.Contains("配置", result.Error);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChatRemainsEditableAndClosingCancelsWithoutPublishingLateAnswers(bool close)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken requestToken = default;
        using var client = new HttpClient(new Handler(async (_, token) =>
        { requestToken = token; entered.SetResult(); await release.Task; return Reply(); }));
        ImageQuestionWindow? window = null;
        Task send = Task.CompletedTask;
        WpfTestHost.Invoke(() =>
        {
            window = new ImageQuestionWindow(Pixels(), [Profile()], service: new ImageQuestionService(client, new Credentials()));
            Assert.False(entered.Task.IsCompleted); // Opening merely attaches; sending is explicit.
            ((TextBox)window.FindName("PromptBox")).Text = "总结";
            send = window.SendAsync();
        });
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            WpfTestHost.Invoke(() =>
            {
                var prompt = (TextBox)window!.FindName("PromptBox");
                Assert.True(prompt.IsEnabled); prompt.Text = "下一轮草稿";
                Assert.True(((Button)window.FindName("SendButton")).IsEnabled);
                if (close) window.Close();
            });
            if (close) Assert.True(requestToken.IsCancellationRequested);
            release.SetResult();
            await send.WaitAsync(TimeSpan.FromSeconds(5));
            WpfTestHost.Invoke(() =>
            {
                var conversation = (StackPanel)window!.FindName("Conversation");
                if (close) { Assert.Empty(conversation.Children); Assert.Empty(((StackPanel)window.FindName("AttachmentsPanel")).Children); }
                else { Assert.Equal(3, conversation.Children.Count); Assert.Equal("下一轮草稿", ((TextBox)window.FindName("PromptBox")).Text); }
            });
        }
        finally { release.TrySetResult(); WpfTestHost.Invoke(() => window!.Close()); }
    }
}
