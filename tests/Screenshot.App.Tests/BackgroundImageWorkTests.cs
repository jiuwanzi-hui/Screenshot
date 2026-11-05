using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using Screenshot.App.Capture;
using Screenshot.App.Core;
using Screenshot.App.Editor;
using Screenshot.App.Pin;
using Screenshot.App.Text;

namespace Screenshot.App.Tests;

public sealed class BackgroundImageWorkTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static object? Call(object target, string method, params object[] args) => target.GetType().GetMethod(method, Private)!.Invoke(target, args);
    private static void Set(object target, string field, object value) => target.GetType().GetField(field, Private)!.SetValue(target, value);
    private static OcrRecognitionResult Result() => new(true, "hello", null) { Regions = [new("hello", 10, 10, 60, 20)] };

    [Fact]
    public void BackgroundCaptureCannotConsumeOtherApplicationsCopyOrEscape()
    {
        WpfTestHost.Invoke(() =>
        {
            using var window = new CaptureOverlayWindow(null, isStartupPrewarm: true, deferInitialScreenCapture: true);
            var active = typeof(CaptureOverlayWindow).GetField("_activeInteractiveOverlay", BindingFlags.NonPublic | BindingFlags.Static)!;
            var previous = active.GetValue(null);
            try
            {
                active.SetValue(null, window); Set(window, "_isCaptureBackgrounded", true); Set(window, "_isColorPickerActive", true);
                Assert.False(CaptureOverlayWindow.TryHandleGlobalPickerKey(0x1B, HotKeyModifiers.None));
                Assert.False(CaptureOverlayWindow.TryHandleGlobalPickerKey(0x43, HotKeyModifiers.Control));
                Assert.False((bool)Call(window, "TryCompleteFromConfiguredHotKey")!);
            }
            finally { active.SetValue(null, previous); window.Close(); }
        });
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task CaptureWorkDoesNotBlockEditingAndDiscardsChangedSelection(bool changeSelection, bool translate)
    {
        using var pins = new PinnedImageManager();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<OcrRecognitionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var translationRelease = new TaskCompletionSource<TranslationSegmentsResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        CaptureOverlayWindow? window = null; Task work = Task.CompletedTask;
        System.Windows.Interop.HwndSource? surface = null;
        var uiThread = 0; var computeThread = 0; CancellationToken token = default;
        WpfTestHost.Invoke(() =>
        {
            uiThread = Environment.CurrentManagedThreadId;
            Func<CapturedImage, Task<OcrRecognitionResult>> recognize = _ => release.Task;
            window = new CaptureOverlayWindow(new CaptureOverlayOptions
            {
                SaveDirectory = System.IO.Path.GetTempPath(), KeepHistory = false, HistoryLimit = 0,
                HistoryService = new CaptureHistoryService(), PinnedImageManager = pins, StartOcrAsync = _ => Task.CompletedTask,
                RecognizeTextAsync = recognize, RecognizeTextWithCancellationAsync = (_, ct) =>
                { if (translate) return Task.FromResult(Result()); token = ct; computeThread = Environment.CurrentManagedThreadId; entered.SetResult(); return release.Task; },
                TranslateTextAsync = _ => translationRelease.Task,
                TranslateTextWithCancellationAsync = (_, ct) =>
                { token = ct; computeThread = Environment.CurrentManagedThreadId; entered.SetResult(); return translationRelease.Task; },
            }, isStartupPrewarm: true, deferInitialScreenCapture: true);
            var root = (FrameworkElement)window.Content;
            window.Content = null;
            root.Resources = window.Resources;
            surface = new System.Windows.Interop.HwndSource(new System.Windows.Interop.HwndSourceParameters("Hidden background image work test")
                { WindowStyle = unchecked((int)0x80000000), Width = 800, Height = 600 });
            surface.RootVisual = root;
            root.Measure(new Size(800, 600)); root.Arrange(new Rect(0, 0, 800, 600)); root.UpdateLayout();
            var selection = (System.Windows.Shapes.Rectangle)window.FindName("SelectionRectangle");
            Canvas.SetLeft(selection, 100); Canvas.SetTop(selection, 80); selection.Width = 200; selection.Height = 120;
            var image = new CapturedImage(new System.Drawing.Bitmap(200, 120));
            Set(window, "_inlineEditorImage", image);
            ((ImageEditorCanvas)window.FindName("InlineEditorCanvas")).Initialize(image, 200, 120);
            work = translate ? (Task)Call(window, "TranslateInlineTextAsync", false)! : (Task)Call(window, "RecognizeInlineTextAsync", recognize)!;
        });
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.NotEqual(uiThread, computeThread);
            WpfTestHost.Invoke(() =>
            {
                Assert.True(((Border)window!.FindName("CaptureToolbar")).IsEnabled);
                Assert.True(((ImageEditorCanvas)window.FindName("InlineEditorCanvas")).IsEnabled);
                Assert.Equal(Visibility.Visible, ((Button)window.FindName("BackgroundCaptureButton")).Visibility);
                if (changeSelection) Call(window, "ClearInlineOcrText");
                else ((ImageEditorCanvas)window.FindName("InlineEditorCanvas")).AddMosaicRegions([new Rect(120, 70, 20, 20)]);
            });
            Assert.Equal(changeSelection, token.IsCancellationRequested);
            release.SetResult(Result()); translationRelease.SetResult(new(true, ["你好"], null));
            await work.WaitAsync(TimeSpan.FromSeconds(10));
            WpfTestHost.Invoke(() =>
            {
                var cached = typeof(CaptureOverlayWindow).GetField(translate ? "_inlineTranslationResult" : "_inlineOcrResult", Private)!.GetValue(window);
                if (changeSelection) Assert.Null(cached); else Assert.NotNull(cached);
                if (!changeSelection) Assert.True(((ImageEditorCanvas)window!.FindName("InlineEditorCanvas")).CanUndo);
                Assert.NotEqual(Visibility.Visible, ((Canvas)window!.FindName("OcrTextOverlay")).Visibility);
            });
        }
        finally { release.TrySetResult(Result()); translationRelease.TrySetResult(new(true, ["你好"], null)); WpfTestHost.Invoke(() => { window!.Close(); window.Dispose(); surface?.Dispose(); }); }
    }

    [Fact]
    public async Task PinnedImageRecognitionRunsOffDispatcherAndCancellationPropagates()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<OcrRecognitionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        PinnedImageWindow? window = null; Task<OcrRecognitionResult> work = null!;
        using var image = new CapturedImage(new System.Drawing.Bitmap(50, 30));
        var uiThread = 0; var computeThread = 0; CancellationToken token = default;
        WpfTestHost.Invoke(() =>
        {
            uiThread = Environment.CurrentManagedThreadId;
            window = new PinnedImageWindow(image.Clone(), recognizeTextWithCancellationAsync: (_, ct) =>
            { computeThread = Environment.CurrentManagedThreadId; token = ct; entered.SetResult(); return release.Task; });
            work = window.RecognizeImageAsync(image);
        });
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); Assert.NotEqual(uiThread, computeThread);
            WpfTestHost.Invoke(() => window!.Close()); Assert.True(token.IsCancellationRequested);
            release.SetResult(Result()); await work.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { release.TrySetResult(Result()); WpfTestHost.Invoke(() => window!.Close()); }
    }

    [Fact]
    public async Task PinTranslationKeepsNewAnnotationsAndReusesCompletedResultOnNextClick()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<TranslationSegmentsResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        PinnedImageWindow? window = null; Task work = Task.CompletedTask;
        var calls = 0;
        WpfTestHost.Invoke(() =>
        {
            window = new PinnedImageWindow(new CapturedImage(new System.Drawing.Bitmap(200, 120)),
                initialRecognition: Result(), translateTextWithCancellationAsync: (_, _) =>
                { calls++; entered.SetResult(); return release.Task; });
            Set(window, "_isEditorMode", true);
            var image = new CapturedImage(new System.Drawing.Bitmap(200, 120));
            Set(window, "_inlineEditorImage", image);
            ((ImageEditorCanvas)window.FindName("InlineEditorCanvas")).Initialize(image, 200, 120);
            work = (Task)Call(window, "TranslateFromToolbarAsync")!;
        });
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            WpfTestHost.Invoke(() =>
            {
                ((ImageEditorCanvas)window!.FindName("InlineEditorCanvas")).AddMosaicRegions([new Rect(120, 70, 20, 20)]);
                _ = (Task)Call(window, "TranslateFromToolbarAsync")!;
            });
            release.SetResult(new(true, ["你好"], null)); await work.WaitAsync(TimeSpan.FromSeconds(5));
            WpfTestHost.Invoke(() =>
            {
                var canvas = (ImageEditorCanvas)window!.FindName("InlineEditorCanvas");
                Assert.True(canvas.CanUndo); Assert.False(canvas.HasTranslationOverlay);
                work = (Task)Call(window, "TranslateFromToolbarAsync")!;
            });
            await work.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, calls);
            WpfTestHost.Invoke(() =>
            {
                var canvas = (ImageEditorCanvas)window!.FindName("InlineEditorCanvas");
                Assert.True(canvas.CanUndo); Assert.True(canvas.HasTranslationOverlay);
            });
        }
        finally { release.TrySetResult(new(true, ["你好"], null)); WpfTestHost.Invoke(() => window!.Close()); }
    }
}
