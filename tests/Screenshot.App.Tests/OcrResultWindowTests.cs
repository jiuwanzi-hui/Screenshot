using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using Screenshot.App.Core;
using Screenshot.App.Text;

namespace Screenshot.App.Tests;

public sealed class OcrResultWindowTests
{
    [Fact]
    public async Task ReusesCachedTranslationForUnchangedRecognizedText()
    {
        var handler = new CountingTranslationHandler();
        using var client = new HttpClient(handler);
        OcrResultWindow? window = null;
        Task? firstTranslation = null;

        try
        {
            WpfTestHost.Invoke(() =>
            {
                window = new OcrResultWindow(
                    new OcrRecognitionResult(
                        true,
                        "hello",
                        ErrorMessage: null),
                    AppSettings.CreateDefault() with
                    {
                        SendTextToOnlineTranslation = true,
                        TranslationEndpoint =
                            "https://translation.example/v1/chat/completions",
                        TranslationModel = "test-model",
                    },
                    new TestCredentialStore(),
                    client);
                var method = typeof(OcrResultWindow).GetMethod(
                    "TranslateAsync",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.NotNull(method);
                firstTranslation = Assert.IsAssignableFrom<Task>(
                    method.Invoke(window, null));
            });

            await Assert.IsAssignableFrom<Task>(firstTranslation)
                .WaitAsync(TimeSpan.FromSeconds(5));

            Task? cachedTranslation = null;
            WpfTestHost.Invoke(() =>
            {
                var translationTextBox = Assert.IsType<
                    System.Windows.Controls.TextBox>(
                    window!.FindName("TranslationTextBox"));
                translationTextBox.Clear();
                var method = typeof(OcrResultWindow).GetMethod(
                    "TranslateAsync",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                cachedTranslation = Assert.IsAssignableFrom<Task>(
                    method!.Invoke(window, null));
            });

            await Assert.IsAssignableFrom<Task>(cachedTranslation)
                .WaitAsync(TimeSpan.FromSeconds(5));
            WpfTestHost.Invoke(() =>
            {
                var translationTextBox = Assert.IsType<
                    System.Windows.Controls.TextBox>(
                    window!.FindName("TranslationTextBox"));
                var statusText = Assert.IsType<System.Windows.Controls.TextBlock>(
                    window.FindName("StatusText"));
                Assert.Equal("你好", translationTextBox.Text);
                Assert.Contains("缓存译文", statusText.Text);
            });
            Assert.Equal(1, handler.RequestCount);
        }
        finally
        {
            WpfTestHost.Invoke(() => window?.Close());
        }
    }

    private sealed class CountingTranslationHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"choices":[{"message":{"content":"你好"}}]}""",
                    Encoding.UTF8,
                    "application/json"),
            });
        }
    }

    private sealed class TestCredentialStore : ITranslationCredentialStore
    {
        public string? GetApiKey(string providerId) => "test-key";

        public void SetApiKey(string providerId, string? apiKey)
        {
        }
    }
}
