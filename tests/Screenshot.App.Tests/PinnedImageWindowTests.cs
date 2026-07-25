using Screenshot.App.Capture;
using Screenshot.App.Pin;
using Screenshot.App.Text;
using System.Windows.Controls;

namespace Screenshot.App.Tests;

public sealed class PinnedImageWindowTests
{
    [Fact]
    public async Task AutomaticallyRecognizesAndDisplaysSelectableText()
    {
        PinnedImageWindow? window = null;
        Task? recognitionTask = null;

        try
        {
            WpfTestHost.Invoke(() =>
            {
                window = new PinnedImageWindow(
                    new CapturedImage(new System.Drawing.Bitmap(240, 120)),
                    _ => Task.FromResult(new OcrRecognitionResult(
                        true,
                        "Selectable text",
                        ErrorMessage: null)
                    {
                        Regions =
                        [
                            new OcrTextRegion(
                                "Selectable text",
                                20,
                                24,
                                140,
                                24),
                        ],
                    }),
                    _ => Task.FromResult(TranslationSegmentsResult.Failure(
                        "unused")));
                window.Show();
                window.UpdateLayout();
                recognitionTask = window.TextRecognitionTask;
            });

            await Assert.IsAssignableFrom<Task>(recognitionTask)
                .WaitAsync(TimeSpan.FromSeconds(5));

            WpfTestHost.Invoke(() =>
            {
                var overlay = Assert.IsType<Canvas>(window!.FindName("TextOverlay"));
                var textBox = Assert.Single(overlay.Children.OfType<TextBox>());
                var translateButton = Assert.IsType<Button>(
                    window.FindName("TranslateButton"));
                Assert.Equal("Selectable text", textBox.Text);
                Assert.True(textBox.IsReadOnly);
                Assert.True(translateButton.IsEnabled);
                Assert.True(PinnedImageWindow.IsSelectableTextSource(textBox));
                Assert.False(PinnedImageWindow.IsSelectableTextSource(overlay));
            });
        }
        finally
        {
            WpfTestHost.Invoke(() => window?.Close());
        }
    }

    [Fact]
    public async Task TranslationReplacesSelectableOverlayTextOnTheImage()
    {
        PinnedImageWindow? window = null;
        Task? recognitionTask = null;
        var translationRequestCount = 0;

        try
        {
            WpfTestHost.Invoke(() =>
            {
                window = new PinnedImageWindow(
                    new CapturedImage(new System.Drawing.Bitmap(240, 120)),
                    _ => Task.FromResult(new OcrRecognitionResult(
                        true,
                        "Hello",
                        ErrorMessage: null)
                    {
                        Regions = [new OcrTextRegion("Hello", 20, 24, 80, 24)],
                    }),
                    _ =>
                    {
                        translationRequestCount++;
                        return Task.FromResult(new TranslationSegmentsResult(
                            true,
                            ["你好"],
                            ErrorMessage: null));
                    });
                window.Show();
                window.UpdateLayout();
                recognitionTask = window.TextRecognitionTask;
            });

            await Assert.IsAssignableFrom<Task>(recognitionTask)
                .WaitAsync(TimeSpan.FromSeconds(5));
            Task? translationTask = null;
            WpfTestHost.Invoke(
                () => translationTask = window!.TranslateTextAsync());
            await Assert.IsAssignableFrom<Task>(translationTask)
                .WaitAsync(TimeSpan.FromSeconds(5));

            WpfTestHost.Invoke(() =>
            {
                var overlay = Assert.IsType<Canvas>(window!.FindName("TextOverlay"));
                var textBox = Assert.Single(overlay.Children.OfType<TextBox>());
                Assert.Equal("你好", textBox.Text);
                Assert.NotEqual(
                    System.Windows.Media.Brushes.Transparent,
                    textBox.Foreground);
                Assert.NotEqual(
                    System.Windows.Media.Brushes.Transparent,
                    textBox.Background);
                var statusText = Assert.IsType<TextBlock>(
                    window.FindName("HeaderStatusText"));
                Assert.Contains("译文可选择复制", statusText.Text);
                Assert.Equal("原文", Assert.IsType<Button>(
                    window.FindName("TranslateButton")).Content);
            });

            Task? showOriginalTask = null;
            WpfTestHost.Invoke(
                () => showOriginalTask = window!.TranslateTextAsync());
            await Assert.IsAssignableFrom<Task>(showOriginalTask)
                .WaitAsync(TimeSpan.FromSeconds(5));
            WpfTestHost.Invoke(() =>
            {
                var overlay = Assert.IsType<Canvas>(window!.FindName("TextOverlay"));
                Assert.Equal("Hello", Assert.Single(
                    overlay.Children.OfType<TextBox>()).Text);
                Assert.Equal("译文", Assert.IsType<Button>(
                    window.FindName("TranslateButton")).Content);
            });

            Task? showCachedTranslationTask = null;
            WpfTestHost.Invoke(
                () => showCachedTranslationTask = window!.TranslateTextAsync());
            await Assert.IsAssignableFrom<Task>(showCachedTranslationTask)
                .WaitAsync(TimeSpan.FromSeconds(5));
            WpfTestHost.Invoke(() =>
            {
                var overlay = Assert.IsType<Canvas>(window!.FindName("TextOverlay"));
                Assert.Equal("你好", Assert.Single(
                    overlay.Children.OfType<TextBox>()).Text);
            });
            Assert.Equal(1, translationRequestCount);
        }
        finally
        {
            WpfTestHost.Invoke(() => window?.Close());
        }
    }

    [Fact]
    public void IsTopmostAndHiddenFromTheTaskbar()
    {
        var isVisible = false;
        var isTopmost = false;
        var isHiddenFromTaskbar = false;

        WpfTestHost.Invoke(() =>
        {
            var virtualDesktop = VirtualScreen.GetBounds();
            CapturedImage? image = ScreenCaptureService.Capture(
                new ScreenRegion(virtualDesktop.X, virtualDesktop.Y, 1, 1));
            PinnedImageWindow? window = null;

            try
            {
                window = new PinnedImageWindow(image);
                image = null;
                window.Show();
                isVisible = window.IsVisible;
                isTopmost = window.Topmost;
                isHiddenFromTaskbar = !window.ShowInTaskbar;
            }
            finally
            {
                window?.Close();
                image?.Dispose();
            }
        });

        Assert.True(isVisible);
        Assert.True(isTopmost);
        Assert.True(isHiddenFromTaskbar);
    }
}
