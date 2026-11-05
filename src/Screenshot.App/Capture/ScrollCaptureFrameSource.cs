using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using Screenshot.App.Text.Realtime;

namespace Screenshot.App.Capture;

// One window surface per active capture session. Window capture sees content
// beneath our preview/border without changing either HWND's visible state.
internal sealed class ScrollCaptureFrameSource : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly Func<IRealtimeCaptureSource> _factory;
    private readonly Func<ScreenRegion, Bitmap> _captureDesktop;
    private readonly CancellationToken _token;
    private IRealtimeCaptureSource? _source;
    private RealtimeFrame? _latest;
    private bool _disposed;

    internal ScrollCaptureFrameSource(nint window, CancellationToken token)
        : this(() => new RealtimeCaptureSource(window, null, preferBorderless: true), token) { }

    internal ScrollCaptureFrameSource(Func<IRealtimeCaptureSource> factory, CancellationToken token,
        Func<ScreenRegion, Bitmap>? captureDesktop = null)
    { _factory = factory; _token = token; _captureDesktop = captureDesktop ?? ForegroundWindowCaptureService.CaptureRegion; }

    internal Bitmap Capture(ScrollCaptureTarget target)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _token.ThrowIfCancellationRequested();
            _source ??= _factory();
            var wait = Stopwatch.StartNew();
            do
            {
                var next = _source.CaptureAsync(_token).GetAwaiter().GetResult();
                if (next is not null)
                {
                    _latest?.Dispose();
                    _latest = next;
                    break;
                }
                // An unchanged window does not produce a new WGC frame. Wait
                // briefly for presentation, then reuse only this session's last
                // complete surface. First-frame waiting is bounded/cancellable.
                if (_latest is not null && wait.ElapsedMilliseconds >= 32) break;
                if (wait.ElapsedMilliseconds >= 1500)
                    throw new InvalidOperationException("无法读取滚动窗口的画面，请重新框选。");
                Task.Delay(8, _token).GetAwaiter().GetResult();
            } while (true);
            var window = _latest!;
            var region = target.CaptureRegion;
            var overlap = ScreenRegion.Intersect(region, window.Bounds);
            if (overlap == region)
                return window.Image.Bitmap.Clone(new Rectangle(region.X - window.Bounds.X,
                    region.Y - window.Bounds.Y, region.Width, region.Height), PixelFormat.Format32bppPArgb);

            // A selection may include fixed desktop pixels outside the window.
            // Never substitute an occluded window DC (which may be black).
            if (target.PreviewRegionProvider?.Invoke() is { } preview &&
                ScreenRegion.Intersect(region, preview) is { IsEmpty: false } covered &&
                ScreenRegion.Intersect(covered, window.Bounds) != covered)
                throw new InvalidOperationException("预览遮挡的选区超出了目标窗口，无法取得完整画面。");
            var result = _captureDesktop(region);
            try
            {
                using var graphics = Graphics.FromImage(result);
                graphics.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
                // Keep fixed desktop/caption pixels under the selection border.
                // The live window surface below replaces every in-window pixel,
                // so this backdrop cannot freeze any scrolling content.
                if (target.BorderBackdrop is { } backdrop)
                {
                    foreach (var border in target.BorderRegions)
                    {
                        var fixedPart = ScreenRegion.Intersect(ScreenRegion.Intersect(region, border), backdrop.Region);
                        if (fixedPart.IsEmpty) continue;
                        graphics.DrawImage(backdrop.Image,
                            new Rectangle(fixedPart.X - region.X, fixedPart.Y - region.Y, fixedPart.Width, fixedPart.Height),
                            new Rectangle(fixedPart.X - backdrop.Region.X, fixedPart.Y - backdrop.Region.Y, fixedPart.Width, fixedPart.Height), GraphicsUnit.Pixel);
                    }
                }
                if (!overlap.IsEmpty)
                    graphics.DrawImage(window.Image.Bitmap,
                        new Rectangle(overlap.X - region.X, overlap.Y - region.Y, overlap.Width, overlap.Height),
                        new Rectangle(overlap.X - window.Bounds.X, overlap.Y - window.Bounds.Y, overlap.Width, overlap.Height), GraphicsUnit.Pixel);
                return result;
            }
            catch { result.Dispose(); throw; }
        }
    }

    public ValueTask DisposeAsync() => new(Task.Run(() =>
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _latest?.Dispose(); _latest = null;
            _source?.Dispose(); _source = null;
        }
    }));
}
