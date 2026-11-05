using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using Screenshot.App.Capture;
using Screenshot.App.Presentation;
using Screenshot.App.Text;
using Screenshot.App.Pin;

namespace Screenshot.App.Tests;

public sealed class TranslationRepairAndStatusTests
{
    [Fact]
    public void PinTranslationStatusSharesTheOptionsRowAndKeepsLongDetailsInTheTooltip()
    {
        WpfTestHost.Invoke(() =>
        {
            var owner = new Window();
            _ = new System.Windows.Interop.WindowInteropHelper(owner).EnsureHandle();
            var toolbar = new PinnedImageEditorToolbarWindow(owner);
            try
            {
                var message = string.Join("\n", Enumerable.Repeat("本机离线模型正在翻译，已完成 3 段文字。", 15));
                toolbar.SetTranslationProgress(message);
                var status = (TextBlock)toolbar.FindName("TranslationProgressText");
                var colors = (StackPanel)toolbar.FindName("ColorOptions");
                var line = Assert.IsType<ToolbarAdjustmentPanel>(colors.Parent);
                Assert.Same(line.Parent, status.Parent);
                Assert.Same(status, line.ReservedWidthSource);
                Assert.Equal(1, Grid.GetColumn(status));
                Assert.Equal(HorizontalAlignment.Right, status.HorizontalAlignment);
                Assert.Equal(VerticalAlignment.Center, status.VerticalAlignment);
                Assert.DoesNotContain('\n', status.Text);
                Assert.Equal(message, Assert.IsType<TextBlock>(status.ToolTip).Text);
                status.Measure(new Size(460, double.PositiveInfinity));
                Assert.InRange(status.DesiredSize.Height, 18, 36);
                toolbar.SetTranslationProgress("");
                Assert.Equal(Visibility.Collapsed, status.Visibility);
            }
            finally { toolbar.Close(); owner.Close(); }
        });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RetryingOnlyFailedParagraphsKeepsPreviouslyValidatedTranslations(bool succeeds)
    {
        string[] source = ["There are 7 windows.", "It received 25 requests.", "There are 4 users."];
        var previous = new TranslationSegmentsResult(true, ["有 7 个窗口。", source[1], "有 4 位用户。"], "第二段未完成")
            { FailedSegmentIndices = [1], Notice = "本机离线模型 TranslateGemma 4B" };
        var retry = new TranslationSegmentsResult(true, [succeeds ? "收到了 25 个请求。" : "收到了 24 个请求。"], null)
            { Notice = "本机离线模型 TranslateGemma 4B" };
        var merged = LocalTranslationRepair.MergeRetry(source, previous, [1], retry, "zh-Hans");
        Assert.Equal(previous.Segments[0], merged.Segments[0]);
        Assert.Equal(previous.Segments[2], merged.Segments[2]);
        Assert.Equal(succeeds ? "收到了 25 个请求。" : source[1], merged.Segments[1]);
        if (succeeds) Assert.Empty(merged.FailedSegmentIndices!);
        else Assert.Equal(1, Assert.Single(merged.FailedSegmentIndices!));
        Assert.Equal(!succeeds, merged.ErrorMessage is not null);
    }

    [Fact]
    public async Task SentenceRepairRetainsEveryNumberAndRejectsAFailedSentence()
    {
        const string source = "The application has 7 windows. It received 25 requests.";
        var good = new Queue<string>(["应用程序有 7 个窗口。", "它收到了 25 个请求。"]);
        var result = await LocalTranslationRepair.RetryAsync(source, "zh-Hans",
            (_, _) => Task.FromResult(good.Dequeue()), CancellationToken.None);
        Assert.Equal("应用程序有 7 个窗口。 它收到了 25 个请求。", result);
        var bad = new Queue<string>(["应用程序有 7 个窗口。", "它收到了 24 个请求。"]);
        Assert.Null(await LocalTranslationRepair.RetryAsync(source, "zh-Hans",
            (_, _) => Task.FromResult(bad.Dequeue()), CancellationToken.None));
    }

    [Fact]
    public async Task ShortDeterministicFailuresDoNotLoopAndCancellationStopsRepairs()
    {
        var calls = 0;
        Assert.Null(await LocalTranslationRepair.RetryAsync("There are 1274 participants.", "zh-Hans",
            (_, _) => { calls++; return Task.FromResult("127 个参与者"); }, CancellationToken.None));
        Assert.Equal(0, calls);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => LocalTranslationRepair.RetryAsync(
            "First paragraph. Second paragraph.", "zh-Hans", (_, _) => Task.FromResult("段落"), cancellation.Token));
    }

    [Fact]
    public void TranslationStatusAllowsTwoReadableLinesWithoutUnboundedToolbarGrowth()
    {
        WpfTestHost.Invoke(() =>
        {
            var window = new CaptureOverlayWindow(null, isStartupPrewarm: true, deferInitialScreenCapture: true);
            try
            {
                var message = string.Join("\n", Enumerable.Repeat("本机离线模型：一个段落未完成，已保留原文。可下载离线语言包后补译。", 20));
                var property = typeof(CaptureOverlayWindow).GetProperty("CaptureStatusMessage", BindingFlags.Instance | BindingFlags.NonPublic)!;
                property.SetValue(window, message);
                var text = (TextBlock)window.FindName("CaptureStatusText");
                text.Visibility = Visibility.Visible;
                text.Measure(new System.Windows.Size(460, double.PositiveInfinity));
                Assert.InRange(text.DesiredSize.Height, 18, 36);
                Assert.DoesNotContain('\n', text.Text);
                Assert.Equal(message, Assert.IsType<TextBlock>(text.ToolTip).Text);
                var activity = (ActivityIndicator)window.FindName("CaptureActivity");
                Assert.Equal(Visibility.Collapsed, activity.Visibility);
                activity.IsActive = true;
                Assert.Equal(Visibility.Visible, activity.Visibility);
                activity.IsActive = false;
                Assert.Equal(Visibility.Collapsed, activity.Visibility);
            }
            finally { window.Close(); }
        });
    }
}
