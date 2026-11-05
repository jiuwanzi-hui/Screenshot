using System.Windows.Media;

namespace Screenshot.App.Infrastructure;

// Keep one live subscription per gesture, independent of mouse report rate.
// Read the physical cursor in the callback, just before presenting the frame.
internal sealed class PointerFramePump(Action update, bool nativeFrames = false) : IDisposable
{
    private TimeSpan _lastFrame = TimeSpan.MinValue;
    private NativePointerFrameClock? _nativeClock;
    private bool _nativeClockUnavailable;
    public bool IsActive { get; private set; }

    public void Start()
    {
        if (IsActive) return;
        IsActive = true;
        _lastFrame = TimeSpan.MinValue;
        if (nativeFrames && !_nativeClockUnavailable)
        {
            _nativeClock ??= new NativePointerFrameClock(() =>
                Present(System.Diagnostics.Stopwatch.GetElapsedTime(0)), () =>
                {
                    _nativeClockUnavailable = true;
                    if (IsActive) CompositionTarget.Rendering += OnRendering;
                });
            _nativeClock.Start();
        }
        else CompositionTarget.Rendering += OnRendering;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        if (e is RenderingEventArgs frame) Present(frame.RenderingTime);
    }

    internal void Present(TimeSpan renderingTime)
    {
        // WPF can raise Rendering again after a layout invalidation in the
        // same frame. Never submit another HWND update for that timestamp.
        if (!IsActive || _lastFrame == renderingTime) return;
        _lastFrame = renderingTime;
        update();
    }

    public void Stop()
    {
        if (!IsActive) return;
        IsActive = false;
        _nativeClock?.Stop();
        CompositionTarget.Rendering -= OnRendering;
    }

    public void Dispose()
    {
        Stop();
        _nativeClock?.Dispose();
        _nativeClock = null;
    }
}
