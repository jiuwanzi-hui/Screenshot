using SnapCut.Core;
using SnapCut.Mac.Native;

namespace SnapCut.Mac.Capture;

/// <summary>
/// Listens to global scroll-wheel events through a listen-only CGEventTap and
/// feeds them into a <see cref="ScrollWheelMotionTracker"/>.
/// </summary>
/// <remarks>
/// The tap needs the 输入监控 (Input Monitoring) 或辅助功能 permission. When it
/// cannot be created the capture still works — the composer treats wheel input
/// only as a direction preference and always probes both directions — so the
/// monitor reports <see cref="IsRunning"/> and the engine degrades gracefully.
/// Delta sign follows the Windows convention the tracker expects: positive
/// means content scrolls up. 自然滚动 flips the raw sign for line deltas, but a
/// wrong preference only costs the cheap opposite-direction probe.
/// </remarks>
internal sealed class MacScrollWheelMonitor : IDisposable
{
    private const int LineDeltaUnits = 120;
    private readonly ScrollWheelMotionTracker _tracker;
    // Rooted so the unmanaged tap can never call a collected delegate.
    private readonly CoreGraphics.EventTapCallback _callback;
    private Thread? _runLoopThread;
    private IntPtr _tap;
    private IntPtr _runLoop;
    private bool _disposed;

    public MacScrollWheelMonitor(ScrollWheelMotionTracker tracker)
    {
        _tracker = tracker;
        _callback = HandleEvent;
    }

    public bool IsRunning { get; private set; }

    public bool TryStart()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (IsRunning)
        {
            return true;
        }

        _tap = CoreGraphics.CGEventTapCreate(
            CoreGraphics.EventTapSession,
            CoreGraphics.EventTapHeadInsert,
            CoreGraphics.EventTapOptionListenOnly,
            1UL << (int)CoreGraphics.EventScrollWheel,
            _callback,
            IntPtr.Zero);

        if (_tap == IntPtr.Zero)
        {
            return false;
        }

        var started = new ManualResetEventSlim(false);
        _runLoopThread = new Thread(() =>
        {
            var source = CoreFoundation.CFMachPortCreateRunLoopSource(
                IntPtr.Zero,
                _tap,
                0);
            _runLoop = CoreFoundation.CFRunLoopGetCurrent();
            CoreFoundation.CFRunLoopAddSource(
                _runLoop,
                source,
                CoreFoundation.RunLoopCommonModes);
            CoreGraphics.CGEventTapEnable(_tap, true);
            started.Set();
            CoreFoundation.CFRunLoopRun();
            CoreFoundation.CFRelease(source);
        })
        {
            IsBackground = true,
            Name = "SnapCut.ScrollWheelTap",
        };
        _runLoopThread.Start();
        started.Wait(TimeSpan.FromSeconds(2));
        IsRunning = true;
        return true;
    }

    private IntPtr HandleEvent(
        IntPtr proxy,
        uint eventType,
        IntPtr cgEvent,
        IntPtr userInfo)
    {
        if (eventType == CoreGraphics.EventScrollWheel)
        {
            var continuous = CoreGraphics.CGEventGetIntegerValueField(
                cgEvent,
                CoreGraphics.ScrollWheelEventIsContinuous) != 0;
            // Trackpads/Magic Mouse report pixel deltas; classic wheels report
            // lines. Normalize both to the 120-per-notch scale the shared
            // tracker calibrates against.
            var delta = continuous
                ? CoreGraphics.CGEventGetIntegerValueField(
                    cgEvent,
                    CoreGraphics.ScrollWheelEventPointDeltaAxis1)
                : CoreGraphics.CGEventGetIntegerValueField(
                    cgEvent,
                    CoreGraphics.ScrollWheelEventDeltaAxis1) * LineDeltaUnits;

            if (delta != 0)
            {
                _tracker.AddDelta((int)Math.Clamp(delta, int.MinValue, int.MaxValue));
            }
        }

        return cgEvent;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_tap != IntPtr.Zero)
        {
            CoreGraphics.CGEventTapEnable(_tap, false);
        }

        if (_runLoop != IntPtr.Zero)
        {
            CoreFoundation.CFRunLoopStop(_runLoop);
        }

        _runLoopThread?.Join(TimeSpan.FromSeconds(2));

        if (_tap != IntPtr.Zero)
        {
            CoreFoundation.CFRelease(_tap);
            _tap = IntPtr.Zero;
        }

        IsRunning = false;
    }
}
