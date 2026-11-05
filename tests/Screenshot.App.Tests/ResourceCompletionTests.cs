using System.Drawing;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows.Controls;
using Screenshot.App.Capture;
using Screenshot.App.Core;
using Screenshot.App.Pin;
using Screenshot.App.Text;

namespace Screenshot.App.Tests;

public sealed class ResourceCompletionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClosingPinOrGroupCancelsRunningTranslation(bool grouped)
    {
        var entered = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var recognition = new OcrRecognitionResult(true, "hello", null)
        {
            Regions = [new OcrTextRegion("hello", 0, 0, 50, 20)],
        };
        PinnedImageWindow? pin = null;
        PinnedImageGroupWindow? group = null;
        Task? operation = null;
        try
        {
            WpfTestHost.Invoke(() =>
            {
                pin = new PinnedImageWindow(new CapturedImage(new Bitmap(80, 50)),
                    recognizeTextAsync: _ => Task.FromResult(recognition),
                    initialRecognition: recognition,
                    translateTextWithCancellationAsync: async (_, token) =>
                    {
                        entered.TrySetResult(token);
                        await Task.Delay(Timeout.Infinite, token);
                        throw new InvalidOperationException("Canceled translation must not complete.");
                    });
                if (grouped)
                {
                    group = new PinnedImageGroupWindow([pin]);
                    // Translation belongs to the group editor. Establish that
                    // state without showing a real toolbar on the desktop.
                    typeof(PinnedImageGroupWindow).GetField("_isEditorMode",
                        BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(group, true);
                    typeof(PinnedImageGroupWindow).GetField("_recognition",
                        BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(group, recognition);
                    operation = (Task)typeof(PinnedImageGroupWindow).GetMethod("TranslateCurrentImageAsync",
                        BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(group, null)!;
                }
                else operation = pin.TranslateTextAsync();
            });
            var token = await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            WpfTestHost.Invoke(() => { if (grouped) group!.Close(); else pin!.Close(); });
            await operation!.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(token.IsCancellationRequested);
            WpfTestHost.Invoke(() =>
            {
                if (grouped)
                {
                    Assert.False(group!.IsVisible);
                    // Closing a group cancels its task without closing its member.
                    Assert.Equal(80, pin!.Preview.PixelWidth);
                }
            });
        }
        finally { WpfTestHost.Invoke(() => { group?.Close(); pin?.Close(); }); }
    }

    [Fact]
    public async Task ClosingOcrResultCancelsHttpRequestWithoutPublishingLateText()
    {
        using var handler = new PendingTranslationHandler();
        using var client = new HttpClient(handler);
        OcrResultWindow? window = null;
        Task? operation = null;
        try
        {
            WpfTestHost.Invoke(() =>
            {
                window = new OcrResultWindow(new OcrRecognitionResult(true, "hello", null),
                    AppSettings.CreateDefault() with
                    {
                        SendTextToOnlineTranslation = true,
                        TranslationEndpoint = "https://translation.example/v1/chat/completions",
                        TranslationModel = "test-model",
                    }, new Credentials(), client);
                operation = (Task)typeof(OcrResultWindow).GetMethod("TranslateAsync",
                    BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null)!;
            });
            var token = await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            WpfTestHost.Invoke(() => window!.Close());
            await operation!.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(token.IsCancellationRequested);
            WpfTestHost.Invoke(() =>
            {
                Assert.Empty(((TextBox)window!.FindName("TranslationTextBox")).Text);
                Assert.Empty(((TextBox)window.FindName("ResultTextBox")).Text);
            });
        }
        finally { WpfTestHost.Invoke(() => window?.Close()); }
    }

    [Fact]
    public async Task HistoryOffloadReleasesSharedSurfaceAndPreservesPixelsAfterEditorDisposal()
    {
        var directory = Path.Combine(Path.GetTempPath(), "SnapCut-history-lifetime-" + Guid.NewGuid().ToString("N"));
        var history = new CaptureHistoryService(directory);
        CaptureHistoryItem? item = null;
        WeakReference? surface = null;
        try
        {
            WpfTestHost.Invoke(() => (item, surface) = AddSharedImage(history));
            var pending = typeof(CaptureHistoryItem).GetField("_pendingImage", BindingFlags.Instance | BindingFlags.NonPublic)!;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (pending.GetValue(item) is not null) await Task.Delay(20, timeout.Token);
            WpfTestHost.Invoke(() => { });
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            Assert.False(surface!.IsAlive);
            using var restored = item!.CreateCapturedImage();
            Assert.Equal(240, restored.Bitmap.Width);
            Assert.Equal(1600, restored.Bitmap.Height);
            Assert.Equal(Color.Coral.ToArgb(), restored.Bitmap.GetPixel(12, 1450).ToArgb());
            Assert.Single(history.Items);
        }
        finally
        {
            history.Clear();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (CaptureHistoryItem Item, WeakReference Surface) AddSharedImage(CaptureHistoryService history)
    {
        using var image = new CapturedImage(new Bitmap(240, 1600));
        image.Bitmap.SetPixel(12, 1450, Color.Coral);
        var preview = image.Preview;
        return (history.Add(preview, 10)!, new WeakReference(preview));
    }

    private sealed class PendingTranslationHandler : HttpMessageHandler
    {
        public TaskCompletionSource<CancellationToken> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Entered.TrySetResult(cancellationToken);
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("Canceled request must not complete.");
        }
    }

    private sealed class Credentials : ITranslationCredentialStore
    {
        public string? GetApiKey(string providerId) => "test-key";
        public void SetApiKey(string providerId, string? apiKey) { }
    }
}
