using Avalonia.Threading;
using SnapCut.Core;
using SnapCut.Mac.Capture;
using SnapCut.Mac.Native;
using SnapCut.Mac.Presentation;

namespace SnapCut.Mac.App;

internal sealed class CaptureCoordinator
{
    private readonly CaptureHistoryStore _history;
    private readonly Func<MacSettings> _settings;
    private int _busy;

    public CaptureCoordinator(
        CaptureHistoryStore history,
        Func<MacSettings> settings)
    {
        _history = history;
        _settings = settings;
    }

    public event Action<string>? CaptureCompleted;

    public bool IsBusy => Volatile.Read(ref _busy) != 0;

    public async Task StartAsync(bool scrollCapture)
    {
        if (Interlocked.Exchange(ref _busy, 1) != 0)
        {
            return;
        }

        try
        {
            if (!EnsureScreenCaptureAccess())
            {
                ShowNotice(
                    "需要屏幕录制权限",
                    "请在 系统设置 → 隐私与安全性 → 屏幕录制 中允许 SnapCut，然后重新启动应用。");
                return;
            }

            var cursor = MacCursorService.GetGlobalPosition();
            var display = MacDisplayService.SelectDisplayFor(
                new CGRect(cursor.X, cursor.Y, 1, 1));
            var desktop = MacScreenCaptureService.CaptureRegion(display.Bounds);
            var selectionWindow = new RegionSelectionWindow(
                display,
                desktop,
                scrollCapture);
            var selection = await selectionWindow.SelectAsync();
            if (selection is null)
            {
                return;
            }

            var region = SelectionGeometry.ToGlobalRect(
                selection.Value,
                selectionWindow.SelectionSurfaceSize,
                display.Bounds);
            await Task.Delay(90);
            if (scrollCapture)
            {
                await RunScrollCaptureAsync(region);
            }
            else
            {
                CompleteCapture(
                    MacScreenCaptureService.CaptureRegion(region),
                    isScrollCapture: false);
            }
        }
        catch (Exception exception)
        {
            ShowNotice("截图失败", exception.Message);
        }
        finally
        {
            Volatile.Write(ref _busy, 0);
        }
    }

    private async Task RunScrollCaptureAsync(CGRect region)
    {
        using var cancellation = new CancellationTokenSource();
        var discard = false;
        var progressWindow = new LongCaptureProgressWindow();
        progressWindow.StopRequested += cancellation.Cancel;
        progressWindow.CancelRequested += () =>
        {
            discard = true;
            cancellation.Cancel();
        };
        progressWindow.Show();
        PixelImage? result = null;
        try
        {
            result = await Task.Run(() =>
            {
                var engine = new ScrollCaptureEngine(ScrollCaptureOptions.Default);
                return engine.Run(
                    region,
                    cancellation.Token,
                    progress => Dispatcher.UIThread.Post(
                        () => progressWindow.UpdateProgress(progress)));
            });
        }
        finally
        {
            progressWindow.Finish();
        }

        if (!discard && result is not null)
        {
            CompleteCapture(result, isScrollCapture: true);
        }
    }

    private void CompleteCapture(PixelImage image, bool isScrollCapture)
    {
        var path = _history.Save(image, isScrollCapture);
        CaptureCompleted?.Invoke(path);
        if (_settings().ShowPreviewAfterCapture)
        {
            new CapturePreviewWindow(image, path, isScrollCapture).Show();
        }
        else
        {
            MacNativeUi.CopyPngFile(path);
        }
    }

    private static bool EnsureScreenCaptureAccess()
    {
        return MacScreenCaptureService.HasScreenCaptureAccess() ||
               MacScreenCaptureService.RequestScreenCaptureAccess();
    }

    private static void ShowNotice(string title, string message)
    {
        new NoticeWindow(title, message).Show();
    }
}
