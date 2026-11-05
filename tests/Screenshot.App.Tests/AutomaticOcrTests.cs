using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Screenshot.App.Capture;
using Screenshot.App.Core;
using Screenshot.App.Editor;
using Screenshot.App.Pin;
using Screenshot.App.Presentation;
using Screenshot.App.Text;

namespace Screenshot.App.Tests;

public sealed class AutomaticOcrTests
{
    private static OcrRecognitionResult TextResult(string text = "hello") => new(true, text, null)
    {
        Regions = [new OcrTextRegion(text, 20, 20, 60, 20)],
        Words = [new OcrWordRegion(text, 20, 20, 60, 20)],
    };

    [Fact]
    public void SettingDefaultsOffAndSurvivesSaveReloadAndApply()
    {
        Assert.False(AppSettings.CreateDefault().AutoOcrAfterCapture);
        var model = new SettingsViewModel(AppSettings.CreateDefault()) { AutoOcrAfterCapture = true };
        var saved = model.CreateSettings();
        var json = System.Text.Json.JsonSerializer.Serialize(saved);
        var restored = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json)!;
        Assert.True(new SettingsViewModel(restored).AutoOcrAfterCapture);
        Assert.True(restored.Normalize().AutoOcrAfterCapture);
    }

    [Fact]
    public async Task ConsecutiveRequestsOnlySnapshotAndRecognizeTheFinalSelection()
    {
        var calls = 0; var snapshots = 0; string? displayed = null;
        CapturedImage? usedImage = null;
        using var session = new AutomaticOcrSession();
        Task task = Task.CompletedTask;
        WpfTestHost.Invoke(() =>
        {
            for (var index = 0; index < 15; index++)
            {
                var value = index.ToString(System.Globalization.CultureInfo.InvariantCulture);
                session.Schedule(() => { snapshots++; return usedImage = new(new System.Drawing.Bitmap(30, 20)); },
                    (_, _) => { calls++; return Task.FromResult(TextResult(value)); },
                    result => displayed = result.Text, delayMilliseconds: 30);
            }
            task = session.Completion;
        });
        await task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, calls); Assert.Equal(1, snapshots); Assert.Equal("14", displayed);
        Assert.Throws<ObjectDisposedException>(() => usedImage!.WarmPreview());
    }

    [Fact]
    public async Task SlowCancelledNativeResultIsDiscardedAndDoesNotOverlapItsReplacement()
    {
        using var session = new AutomaticOcrSession();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldResult = new TaskCompletionSource<OcrRecognitionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var published = new List<string>();
        var secondCalls = 0;
        CancellationToken oldToken = default;
        Task first = Task.CompletedTask, second = Task.CompletedTask;
        WpfTestHost.Invoke(() =>
        {
            session.Schedule(() => new(new System.Drawing.Bitmap(30, 20)), (_, token) =>
            { oldToken = token; started.SetResult(); return oldResult.Task; }, result => published.Add(result.Text), delayMilliseconds: 0);
            first = session.Completion;
        });
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        WpfTestHost.Invoke(() =>
        {
            session.Schedule(() => new(new System.Drawing.Bitmap(30, 20)), (_, _) =>
            { secondCalls++; return Task.FromResult(TextResult("new")); }, result => published.Add(result.Text), delayMilliseconds: 0);
            second = session.Completion;
        });
        Assert.True(oldToken.IsCancellationRequested);
        Assert.Equal(0, secondCalls);
        oldResult.SetResult(TextResult("stale"));
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("new", Assert.Single(published));
        Assert.Equal(1, secondCalls);
    }

    [Fact]
    public async Task CancellingBeforeTheQuietPeriodDoesNotAllocateAnImage()
    {
        using var session = new AutomaticOcrSession();
        var snapshots = 0;
        Task task = Task.CompletedTask;
        WpfTestHost.Invoke(() =>
        {
            session.Schedule(() => { snapshots++; return new(new System.Drawing.Bitmap(20, 20)); },
                (_, _) => Task.FromResult(TextResult()), _ => Assert.Fail("Cancelled result was published"));
            task = session.Completion;
            session.Cancel();
        });
        await task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, snapshots);
    }

    [Fact]
    public async Task PinAutomaticallyRecognizesAndOnlyTextInterceptsDragging()
    {
        PinnedImageWindow? window = null;
        var settings = AppSettings.CreateDefault() with { AutoOcrAfterCapture = true };
        var calls = 0;
        Task task = Task.CompletedTask;
        WpfTestHost.Invoke(() =>
        {
            window = new PinnedImageWindow(new CapturedImage(new System.Drawing.Bitmap(200, 120)),
                settingsProvider: () => settings,
                recognizeTextWithCancellationAsync: (_, _) => { calls++; return Task.FromResult(TextResult()); });
            var viewport = (Grid)window.FindName("ImageViewport");
            viewport.Measure(new Size(200, 120)); viewport.Arrange(new Rect(0, 0, 200, 120));
            window.RefreshAutomaticOcrSetting();
            task = window.TextRecognitionTask;
        });
        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(5));
            WpfTestHost.Invoke(() =>
            {
                var layer = (Canvas)window!.FindName("TextOverlay");
                var text = Assert.IsType<SelectableOcrTextOverlay>(Assert.Single(layer.Children.Cast<UIElement>()));
                Assert.True(layer.IsHitTestVisible);
                text.Measure(new Size(200, 120)); text.Arrange(new Rect(0, 0, 200, 120));
                Assert.Same(text, VisualTreeHelper.HitTest(text, new Point(30, 25))?.VisualHit);
                Assert.Null(VisualTreeHelper.HitTest(text, new Point(150, 90)));
                Assert.True(PinnedImageWindow.IsSelectableTextSource(text));
                Assert.True(window.CanMoveImage);
                text.SelectAllText(); Assert.Equal("hello", text.SelectedText);
                window.ScheduleAutomaticOcr(); // same pixels are reused, e.g. after restoring a pin
                Assert.Equal(1, calls);
                settings = settings with { AutoOcrAfterCapture = false };
                window.RefreshAutomaticOcrSetting();
                Assert.Empty(layer.Children.Cast<UIElement>());
            });
        }
        finally { WpfTestHost.Invoke(() => window!.Close()); }
    }

    [Fact]
    public async Task SelectionAdjustmentCancelsOldOcrAndOnlyRecognizesAfterRelease()
    {
        CaptureOverlayWindow? window = null;
        using var pins = new PinnedImageManager();
        var calls = 0;
        Task task = Task.CompletedTask;
        WpfTestHost.Invoke(() =>
        {
            window = new CaptureOverlayWindow(new CaptureOverlayOptions
            {
                SaveDirectory = System.IO.Path.GetTempPath(), KeepHistory = false, HistoryLimit = 0,
                HistoryService = new CaptureHistoryService(), PinnedImageManager = pins,
                StartOcrAsync = _ => Task.CompletedTask, AutoOcrAfterCapture = true,
                RecognizeTextWithCancellationAsync = (_, _) => { calls++; return Task.FromResult(TextResult()); },
                RecognitionResultPresentation = RecognitionResultPresentationMode.Popup,
            }, initialScreenSnapshot: new CapturedImage(new System.Drawing.Bitmap(800, 600)), deferInitialColorPickerActivation: true);
            var selection = (System.Windows.Shapes.Rectangle)window.FindName("SelectionRectangle");
            Canvas.SetLeft(selection, 100); Canvas.SetTop(selection, 80); selection.Width = 200; selection.Height = 120;
            var image = new CapturedImage(new System.Drawing.Bitmap(200, 120));
            SetField(window, "_inlineEditorImage", image);
            ((ImageEditorCanvas)window.FindName("InlineEditorCanvas")).Initialize(image, 200, 120);
            Invoke(window, "ClearInlineToolSelection");
            window.ScheduleAutomaticOcr();
            Invoke(window, "ClearInlineOcrText");
            SetField(window, "_isMovingSelection", true);
            window.ScheduleAutomaticOcr();
            task = window.AutomaticOcrTask;
        });
        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, calls);
            WpfTestHost.Invoke(() =>
            {
                SetField(window!, "_isMovingSelection", false);
                window!.ScheduleAutomaticOcr(); task = window.AutomaticOcrTask;
            });
            await task.WaitAsync(TimeSpan.FromSeconds(5));
            WpfTestHost.Invoke(() =>
            {
                Assert.Equal(1, calls);
                var layer = (Canvas)window!.FindName("OcrTextOverlay");
                Assert.Equal(Visibility.Visible, layer.Visibility);
                Assert.Single(layer.Children.Cast<UIElement>());
                Invoke(window, "ShowAutomaticRecognitionResults"); // a late QR result cannot hide the OCR layer
                Assert.Equal(Visibility.Visible, layer.Visibility);
                Assert.True(window.CanMoveSelectionFromPoint(new Point(250, 170)));
                ((ImageEditorCanvas)window.FindName("InlineEditorCanvas")).SetAnnotationCreationEnabled(true);
                window.ScheduleAutomaticOcr(); Assert.Equal(1, calls);
            });
        }
        finally { WpfTestHost.Invoke(() => { window!.Close(); window.Dispose(); }); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PinReplacementOrCloseDiscardsPendingRecognitionAndReleasesItsSnapshot(bool close)
    {
        PinnedImageWindow? window = null;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldResult = new TaskCompletionSource<OcrRecognitionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        CapturedImage? snapshot = null;
        CancellationToken token = default;
        Task oldTask = Task.CompletedTask, nextTask = Task.CompletedTask;
        WpfTestHost.Invoke(() =>
        {
            window = new PinnedImageWindow(new CapturedImage(new System.Drawing.Bitmap(200, 120)),
                settingsProvider: () => AppSettings.CreateDefault() with { AutoOcrAfterCapture = true },
                recognizeTextWithCancellationAsync: (image, cancellation) =>
                {
                    if (++calls != 1) return Task.FromResult(TextResult("replacement"));
                    snapshot = image; token = cancellation; started.SetResult(); return oldResult.Task;
                });
            var viewport = (Grid)window.FindName("ImageViewport");
            viewport.Measure(new Size(200, 120)); viewport.Arrange(new Rect(0, 0, 200, 120));
            window.RefreshAutomaticOcrSetting(); oldTask = window.TextRecognitionTask;
        });
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            WpfTestHost.Invoke(() =>
            {
                if (close) window!.Close();
                else { window!.ReplaceImage(new(new System.Drawing.Bitmap(200, 120))); nextTask = window.TextRecognitionTask; }
            });
            Assert.True(token.IsCancellationRequested);
            oldResult.TrySetResult(TextResult("obsolete"));
            await Task.WhenAll(oldTask, nextTask).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Throws<ObjectDisposedException>(() => snapshot!.WarmPreview());
            Assert.Equal(close ? 1 : 2, calls);
            if (!close) WpfTestHost.Invoke(() =>
            {
                var overlay = Assert.IsType<SelectableOcrTextOverlay>(Assert.Single(((Canvas)window!.FindName("TextOverlay")).Children.Cast<UIElement>()));
                overlay.SelectAllText(); Assert.Equal("replacement", overlay.SelectedText);
            });
        }
        finally
        {
            oldResult.TrySetResult(TextResult("obsolete"));
            if (!close) WpfTestHost.Invoke(() => window!.Close());
        }
    }

    private static void SetField(object target, string name, object value) =>
        target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
    private static void Invoke(object target, string name) =>
        target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, null);
}
