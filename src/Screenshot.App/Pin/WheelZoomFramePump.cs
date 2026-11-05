using Point = System.Windows.Point;
using Screenshot.App.Infrastructure;

namespace Screenshot.App.Pin;

// Accumulate high-resolution wheel deltas without queuing layout for each packet.
internal sealed class WheelZoomFramePump : IDisposable
{
    private readonly Action<double, Point> _apply;
    private readonly PointerFramePump _frames;
    private double _steps;
    private Point _pointer;

    internal WheelZoomFramePump(Action<double, Point> apply)
    {
        _apply = apply;
        _frames = new PointerFramePump(Flush);
    }

    internal void Queue(int delta, Point pointer)
    {
        _steps = Math.Clamp(_steps + delta / 120d, -20, 20);
        _pointer = pointer;
        _frames.Start();
    }

    internal void Flush()
    {
        _frames.Stop();
        var steps = _steps;
        _steps = 0;
        if (Math.Abs(steps) > .0001) _apply(steps, _pointer);
    }

    internal void Cancel() { _steps = 0; _frames.Stop(); }
    public void Dispose() { Cancel(); _frames.Dispose(); }
}
