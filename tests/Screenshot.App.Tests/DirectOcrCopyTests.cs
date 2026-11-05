using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Screenshot.App.Capture;
using Screenshot.App.Core;
using Screenshot.App.Editor;
using Screenshot.App.Pin;
using Screenshot.App.Text;

namespace Screenshot.App.Tests;

public sealed class DirectOcrCopyTests
{
    [DesktopTheory]
    [InlineData(RecognitionResultPresentationMode.Overlay, false)]
    [InlineData(RecognitionResultPresentationMode.Popup, false)]
    [InlineData(RecognitionResultPresentationMode.Popup, true)]
    public async Task DirectCopyHonorsClipboardSuccessAndNeverOpensAResultPopup(
        RecognitionResultPresentationMode mode, bool clipboardFails)
    {
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        using var pins = new PinnedImageManager();
        CaptureOverlayWindow? window = null;
        HwndSource? surface = null;
        Task? work = null;
        string? copied = null;
        var popupCalls = 0;
        WpfTestHost.Invoke(() =>
        {
            var result = new OcrRecognitionResult(true, "识别出的文字 hello", null)
                { Regions = [new("识别出的文字 hello", 10, 10, 160, 20)] };
            window = new CaptureOverlayWindow(new CaptureOverlayOptions
            {
                SaveDirectory = System.IO.Path.GetTempPath(), KeepHistory = false, HistoryLimit = 0,
                HistoryService = new CaptureHistoryService(), PinnedImageManager = pins,
                StartOcrAsync = _ => Task.CompletedTask, RecognitionResultPresentation = mode,
                RecognizeTextAsync = _ => Task.FromResult(result),
                ShowRecognitionPopupAsync = (_, _, _, _) => { popupCalls++; return Task.CompletedTask; },
            }, isStartupPrewarm: true, deferInitialScreenCapture: true);
            var root = (FrameworkElement)window.Content;
            window.Content = null; root.Resources = window.Resources;
            surface = new HwndSource(new HwndSourceParameters("Hidden direct OCR copy test")
                { WindowStyle = unchecked((int)0x80000000), Width = 800, Height = 600 });
            surface.RootVisual = root;
            root.Measure(new Size(800, 600)); root.Arrange(new Rect(0, 0, 800, 600)); root.UpdateLayout();
            var selection = (System.Windows.Shapes.Rectangle)window.FindName("SelectionRectangle");
            Canvas.SetLeft(selection, 100); Canvas.SetTop(selection, 80); selection.Width = 200; selection.Height = 120;
            var image = new CapturedImage(new System.Drawing.Bitmap(200, 120));
            typeof(CaptureOverlayWindow).GetField("_inlineEditorImage", flags)!.SetValue(window, image);
            ((ImageEditorCanvas)window.FindName("InlineEditorCanvas")).Initialize(image, 200, 120);
            Func<string, CancellationToken, Task> copy = (text, _) =>
            {
                if (clipboardFails) throw new System.IO.IOException("Clipboard busy");
                copied = text; return Task.CompletedTask;
            };
            work = (Task)typeof(CaptureOverlayWindow).GetMethod("CopyRecognizedTextAsync", flags)!.Invoke(window, [copy])!;
        });
        try
        {
            await work!.WaitAsync(TimeSpan.FromSeconds(8));
            WpfTestHost.Invoke(() =>
            {
                Assert.Equal(!clipboardFails, (bool)typeof(CaptureOverlayWindow).GetField("_isCompleted", flags)!.GetValue(window)!);
                Assert.Equal(clipboardFails ? null : "识别出的文字 hello", copied);
                Assert.Equal(0, popupCalls);
                Assert.Empty(System.Windows.Application.Current.Windows.OfType<RecognitionResultPopupWindow>());
                System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                var toast = CaptureCompletionWorkerTests.FindToast(Environment.ProcessId, "已识别复制完成");
                Assert.Equal(!clipboardFails, toast != 0);
            });
            if (!clipboardFails) await Task.Delay(2000);
        }
        finally { WpfTestHost.Invoke(() => { window?.Close(); surface?.Dispose(); }); }
    }
}
