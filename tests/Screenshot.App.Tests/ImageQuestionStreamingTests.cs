using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Screenshot.App.Text;
using System.Windows;
using System.Windows.Controls;
using TextBox = System.Windows.Controls.TextBox;

namespace Screenshot.App.Tests;

public sealed class ImageQuestionStreamingTests
{
    [Fact]
    public async Task LiveConversationRemainsEditableAndStopKeepsReceivedText()
    {
        using var stream = new GatedStream("data: {\"choices\":[{\"delta\":{\"reasoning_content\":\"核对图片标题。\",\"content\":\"收到的回答\"}}]}\n\n", "");
        using var client = new HttpClient(new ImageQuestionTests.Handler((_, _) =>
        {
            var content = new StreamContent(stream);
            content.Headers.ContentType = new("text/event-stream");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }));
        ImageQuestionWindow window = null!;
        Task send = Task.CompletedTask;
        StackPanel answer = null!;
        WpfTestHost.Invoke(() =>
        {
            window = new ImageQuestionWindow(ImageQuestionTests.Pixels(), [ImageQuestionTests.Profile()],
                service: new ImageQuestionService(client, new ImageQuestionTests.Credentials()));
            ((TextBox)window.FindName("PromptBox")).Text = "总结图片";
            send = window.SendAsync();
            var conversation = (StackPanel)window.FindName("Conversation");
            answer = (StackPanel)((Border)conversation.Children[^1]).Child;
            ((TextBox)window.FindName("PromptBox")).Text = "下一条草稿";
        });
        try
        {
            var received = false;
            for (var i = 0; i < 100 && !received; i++)
            {
                await Task.Delay(20);
                WpfTestHost.Invoke(() => received = answer.Children.OfType<TextBox>().Single().Text == "收到的回答");
            }
            Assert.True(received);
            WpfTestHost.Invoke(() =>
            {
                Assert.Equal("下一条草稿", ((TextBox)window.FindName("PromptBox")).Text);
                var reasoning = answer.Children.OfType<Expander>().Single();
                Assert.Equal(Visibility.Visible, reasoning.Visibility);
                Assert.True(reasoning.IsExpanded);
                Assert.Equal("核对图片标题。", ((TextBox)reasoning.Content).Text);
                window.StopProcessing();
            });
            await send.WaitAsync(TimeSpan.FromSeconds(5));
            WpfTestHost.Invoke(() =>
            {
                Assert.Contains("收到的回答", answer.Children.OfType<TextBox>().Single().Text);
                Assert.Contains("已停止", answer.Children.OfType<TextBox>().Single().Text);
                Assert.False(answer.Children.OfType<Expander>().Single().IsExpanded);
            });
        }
        finally { WpfTestHost.Invoke(() => { window.Dispose(); window.Close(); }); }
    }

    [Fact]
    public async Task PublishesReasoningBeforeResponseEndsAndKeepsItOutOfAnswer()
    {
        using var stream = new GatedStream("data: {\"choices\":[{\"delta\":{\"reasoning_content\":\"先核对图片中的标题。\"}}]}\r\n\r\n",
            "data: {\"choices\":[{\"delta\":{\"content\":\"图片总结。\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n");
        var first = new TaskCompletionSource<ImageQuestionProgress>(TaskCreationOptions.RunContinuationsAsynchronously);
        var task = ImageQuestionStream.ReadAsync(stream, true, p => first.TrySetResult(p), default);
        var progress = await first.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("先核对图片中的标题。", progress.Reasoning);
        Assert.Empty(progress.Answer);
        Assert.False(task.IsCompleted);
        stream.Release.TrySetResult();
        var result = await task;
        Assert.True(result.IsSuccess); Assert.Equal("图片总结。", result.Text);
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("data: [DONE]\n\n", true)]
    [InlineData("data: {\"error\":{\"message\":\"private upstream detail\"}}\n\n", false)]
    public async Task TruncatedAndFailedStreamsRetainPartialAnswerWithoutReportingSuccess(string ending, bool success)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("data: {\"choices\":[{\"delta\":{\"content\":\"部分文字\"}}]}\n\n" + ending));
        var result = await ImageQuestionStream.ReadAsync(stream, true, null, default);
        Assert.Equal(success, result.IsSuccess); Assert.Equal("部分文字", result.Text);
        Assert.DoesNotContain("private upstream", result.Error ?? "");
    }

    [Fact]
    public async Task CancellationInterruptsAnAwaitedChunk()
    {
        using var stream = new GatedStream("data: {\"choices\":[{\"delta\":{\"content\":\"部分文字\"}}]}\n\n", "");
        using var cancel = new CancellationTokenSource();
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var task = ImageQuestionStream.ReadAsync(stream, true, _ => first.TrySetResult(), cancel.Token);
        await first.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }

    [Fact]
    public async Task RequestsStreamingButAcceptsAJsonOnlyCompatibleService()
    {
        using var client = new HttpClient(new ImageQuestionTests.Handler(async (request, token) =>
        {
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            Assert.True(json.RootElement.GetProperty("stream").GetBoolean());
            return ImageQuestionTests.Reply();
        }));
        var result = await new ImageQuestionService(client, new ImageQuestionTests.Credentials())
            .AskAsync(ImageQuestionTests.Profile(), [1], [], "总结图片", default);
        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task ExplicitStreamRejectionRetriesOnceWithSameModelWithoutStreaming()
    {
        var calls = 0;
        using var client = new HttpClient(new ImageQuestionTests.Handler(async (request, token) =>
        {
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            Assert.Equal("vision-test", json.RootElement.GetProperty("model").GetString());
            Assert.Equal(calls++ == 0, json.RootElement.GetProperty("stream").GetBoolean());
            return calls == 1 ? new(HttpStatusCode.BadRequest) { Content = new StringContent("{\"error\":{\"message\":\"stream is not supported\"}}") }
                : ImageQuestionTests.Reply();
        }));
        var result = await new ImageQuestionService(client, new ImageQuestionTests.Credentials())
            .AskAsync(ImageQuestionTests.Profile(), [1], [], "总结图片", default);
        Assert.True(result.IsSuccess); Assert.Equal(2, calls);
    }

    private sealed class GatedStream(string first, string last) : MemoryStream
    {
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly byte[] _first = Encoding.UTF8.GetBytes(first);
        private readonly byte[] _last = Encoding.UTF8.GetBytes(last);
        private int _stage;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            var stage = _stage++;
            if (stage > 1) return 0;
            if (stage == 1) await Release.Task.WaitAsync(token);
            var bytes = stage == 0 ? _first : _last;
            bytes.CopyTo(buffer); return bytes.Length;
        }
    }
}
