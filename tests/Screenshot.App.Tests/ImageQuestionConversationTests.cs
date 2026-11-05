using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Screenshot.App.Core;
using Screenshot.App.Presentation;
using Screenshot.App.Text;
using static Screenshot.App.Tests.ImageQuestionTests;

namespace Screenshot.App.Tests;

public sealed class ImageQuestionConversationTests
{
    [Fact]
    public void EmptyEntryAcceptsFiveImagesAndOldSettingsDefaultToEnabled()
    {
        var old = JsonSerializer.Deserialize<AppSettings>("{}")!;
        Assert.True(old.EnableImageQuestion);
        var settings = old with { ImageQuestionHotKey = "Ctrl+Alt+I" };
        Assert.Contains(HotKeyConfiguration.CreateBindings(settings), b => b.Action == HotKeyAction.ImageQuestion);
        Assert.DoesNotContain(HotKeyConfiguration.CreateBindings(settings with { EnableImageQuestion = false }), b => b.Action == HotKeyAction.ImageQuestion);
        WpfTestHost.Invoke(() =>
        {
            var vm = new SettingsViewModel(settings) { EnableImageQuestion = false, ImageQuestionHotKey = "Ctrl+Shift+I" };
            var roundtrip = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(vm.CreateSettings()))!;
            Assert.False(roundtrip.EnableImageQuestion); Assert.Equal("Ctrl+Shift+I", roundtrip.ImageQuestionHotKey);
            using var window = new ImageQuestionWindow(null, [Profile()]);
            var images = (StackPanel)window.FindName("AttachmentsPanel");
            Assert.Empty(images.Children);
            for (var i = 0; i < 5; i++) Assert.True(window.AddImage(Pixels()));
            Assert.False(window.AddImage(Pixels())); Assert.Equal(5, images.Children.Count);
            window.RemoveImage(2); Assert.Equal(4, images.Children.Count);
            Assert.True(window.AddImage(Pixels())); window.Close();
            Assert.Empty(images.Children);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BusySendQueuesSnapshotsAndSteeringPreservesOriginalRequest(bool steer)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var payloads = new List<string>(); CancellationToken firstToken = default;
        using var client = new HttpClient(new Handler(async (request, token) =>
        {
            payloads.Add(await request.Content!.ReadAsStringAsync(token));
            if (payloads.Count == 1) { firstToken = token; entered.SetResult(); await release.Task; }
            return Reply();
        }));
        ImageQuestionWindow? window = null; Task work = Task.CompletedTask;
        WpfTestHost.Invoke(() =>
        {
            window = new ImageQuestionWindow(Pixels(), [Profile()], service: new ImageQuestionService(client, new Credentials()));
            ((TextBox)window.FindName("PromptBox")).Text = "总结原图"; work = window.SendAsync();
            Assert.Equal("", ((TextBox)window.FindName("PromptBox")).Text);
            Assert.Equal(Visibility.Collapsed, ((StackPanel)window.FindName("AttachmentBody")).Visibility);
            ((Button)window.FindName("ToggleImagesButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(Visibility.Visible, ((StackPanel)window.FindName("AttachmentBody")).Visibility);
            ((Button)window.FindName("ToggleImagesButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(Visibility.Collapsed, ((StackPanel)window.FindName("AttachmentBody")).Visibility);
            var bubbles = ((StackPanel)window.FindName("Conversation")).Children.OfType<Border>().ToArray();
            Assert.Equal(HorizontalAlignment.Right, bubbles[0].HorizontalAlignment);
            Assert.Equal(HorizontalAlignment.Left, bubbles[1].HorizontalAlignment);
            Assert.Contains("正在处理", ((StackPanel)bubbles[1].Child).Children.OfType<TextBox>().Single().Text);
        });
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            WpfTestHost.Invoke(() =>
            {
                window!.AddImage(Pixels());
                ((ComboBox)window.FindName("SpecificModelBox")).Text = "another-vision-model";
                ((ComboBox)window.FindName("StrengthBox")).SelectedValue = "high";
                ((TextBox)window.FindName("PromptBox")).Text = "用三句话回答"; _ = window.SendAsync();
                window.RemoveImage(0); window.RemoveImage(0); // queued request keeps both snapshots
                ((TextBox)window.FindName("PromptBox")).Text = "未发送草稿";
                if (steer) window.SteerQueued(0);
            });
            Assert.Equal(steer, firstToken.IsCancellationRequested); Assert.Single(payloads);
            release.SetResult(); await work.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(2, payloads.Count);
            using var body = JsonDocument.Parse(payloads[1]);
            Assert.Equal("another-vision-model", body.RootElement.GetProperty("model").GetString());
            Assert.Equal("high", body.RootElement.GetProperty("reasoning_effort").GetString());
            var messages = body.RootElement.GetProperty("messages");
            Assert.Equal("总结原图", messages[1].GetProperty("content").GetString());
            Assert.Equal(steer ? 3 : 4, messages.GetArrayLength());
            var latest = messages[messages.GetArrayLength() - 1].GetProperty("content");
            Assert.Equal(3, latest.GetArrayLength()); Assert.Equal("用三句话回答", latest[0].GetProperty("text").GetString());
            WpfTestHost.Invoke(() => Assert.Equal("未发送草稿", ((TextBox)window!.FindName("PromptBox")).Text));
        }
        finally { release.TrySetResult(); WpfTestHost.Invoke(() => window!.Close()); }
    }

    [Fact]
    public async Task StopKeepsQueueUntilExplicitResumeAndLateAnswerIsDiscarded()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); int calls = 0;
        using var client = new HttpClient(new Handler(async (_, _) =>
        { if (Interlocked.Increment(ref calls) == 1) { entered.SetResult(); await release.Task; } else completed.TrySetResult(); return Reply(); }));
        ImageQuestionWindow? window = null; Task work = Task.CompletedTask;
        WpfTestHost.Invoke(() =>
        {
            window = new ImageQuestionWindow(Pixels(), [Profile()], service: new ImageQuestionService(client, new Credentials()));
            ((TextBox)window.FindName("PromptBox")).Text = "第一条"; work = window.SendAsync();
        });
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            WpfTestHost.Invoke(() =>
            {
                ((TextBox)window!.FindName("PromptBox")).Text = "第二条"; _ = window.SendAsync(); window.StopProcessing();
            });
            release.SetResult(); await work.WaitAsync(TimeSpan.FromSeconds(5)); Assert.Equal(1, calls);
            WpfTestHost.Invoke(() =>
            {
                var bubbles = ((StackPanel)window!.FindName("Conversation")).Children.OfType<Border>().ToArray();
                Assert.Equal("已停止。", ((StackPanel)bubbles[1].Child).Children.OfType<TextBox>().Single().Text);
                var resume = (Button)window.FindName("ResumeButton"); Assert.Equal(Visibility.Visible, resume.Visibility);
                resume.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            });
            await completed.Task.WaitAsync(TimeSpan.FromSeconds(5)); Assert.Equal(2, calls);
        }
        finally { release.TrySetResult(); WpfTestHost.Invoke(() => window!.Close()); }
    }

    [Fact]
    public async Task SuggestionSendsImmediatelyWithoutReplacingTypedDraft()
    {
        var sent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = new HttpClient(new Handler((_, _) => { sent.TrySetResult(); return Task.FromResult(Reply()); }));
        ImageQuestionWindow? window = null;
        WpfTestHost.Invoke(() =>
        {
            window = new ImageQuestionWindow(Pixels(), [Profile()], service: new ImageQuestionService(client, new Credentials()));
            ((TextBox)window.FindName("PromptBox")).Text = "保留草稿";
            var method = typeof(ImageQuestionWindow).GetMethod("OnSuggestionClick", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            method.Invoke(window, [new Button { Tag = "提取文字" }, new RoutedEventArgs()]);
        });
        try { await sent.Task.WaitAsync(TimeSpan.FromSeconds(5)); WpfTestHost.Invoke(() => Assert.Equal("保留草稿", ((TextBox)window!.FindName("PromptBox")).Text)); }
        finally { WpfTestHost.Invoke(() => window!.Close()); }
    }
}
