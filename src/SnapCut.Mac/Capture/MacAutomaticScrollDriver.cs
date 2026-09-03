using SnapCut.Mac.Native;

namespace SnapCut.Mac.Capture;

internal sealed class MacAutomaticScrollDriver : IDisposable
{
    private readonly CancellationTokenSource _cancellation = new();
    private Task? _task;

    public void Start(CGRect region)
    {
        var center = new CGPoint
        {
            X = region.Left + (region.Size.Width / 2),
            Y = region.Top + (region.Size.Height / 2),
        };
        _ = CoreGraphics.CGWarpMouseCursorPosition(center);
        _task = Task.Run(async () =>
        {
            await Task.Delay(500, _cancellation.Token);
            while (!_cancellation.IsCancellationRequested)
            {
                var scroll = CoreGraphics.CGEventCreateScrollWheelEvent(
                    IntPtr.Zero,
                    CoreGraphics.ScrollEventUnitPixel,
                    1,
                    -72);
                if (scroll != IntPtr.Zero)
                {
                    CoreGraphics.CGEventPost(CoreGraphics.EventTapHid, scroll);
                    CoreFoundation.CFRelease(scroll);
                }
                await Task.Delay(85, _cancellation.Token);
            }
        }, _cancellation.Token);
    }

    public void Dispose()
    {
        _cancellation.Cancel();
        try
        {
            _task?.Wait(TimeSpan.FromSeconds(1));
        }
        catch (AggregateException exception) when (
            exception.InnerExceptions.All(item => item is TaskCanceledException))
        {
        }
        _cancellation.Dispose();
    }
}
