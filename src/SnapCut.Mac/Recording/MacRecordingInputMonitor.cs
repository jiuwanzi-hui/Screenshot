using SnapCut.Mac.Native;

namespace SnapCut.Mac.Recording;

internal sealed class MacRecordingInputMonitor : IDisposable
{
    private readonly CoreGraphics.EventTapCallback _callback;
    private readonly bool _keyboard;
    private readonly bool _mouse;
    private Thread? _thread;
    private IntPtr _tap;
    private IntPtr _runLoop;

    public MacRecordingInputMonitor(bool keyboard, bool mouse)
    {
        _keyboard = keyboard;
        _mouse = mouse;
        _callback = HandleEvent;
    }

    public event Action<string>? KeyPressed;

    public event Action<CGPoint, bool>? MousePressed;

    public bool Start()
    {
        ulong mask = 0;
        if (_keyboard)
        {
            mask |= 1UL << (int)CoreGraphics.EventKeyDown;
        }
        if (_mouse)
        {
            mask |= 1UL << (int)CoreGraphics.EventLeftMouseDown;
            mask |= 1UL << (int)CoreGraphics.EventRightMouseDown;
        }
        if (mask == 0)
        {
            return false;
        }

        _tap = CoreGraphics.CGEventTapCreate(
            CoreGraphics.EventTapSession,
            CoreGraphics.EventTapHeadInsert,
            CoreGraphics.EventTapOptionListenOnly,
            mask,
            _callback,
            IntPtr.Zero);
        if (_tap == IntPtr.Zero)
        {
            return false;
        }

        using var started = new ManualResetEventSlim(false);
        _thread = new Thread(() =>
        {
            var source = CoreFoundation.CFMachPortCreateRunLoopSource(
                IntPtr.Zero, _tap, 0);
            _runLoop = CoreFoundation.CFRunLoopGetCurrent();
            CoreFoundation.CFRunLoopAddSource(
                _runLoop, source, CoreFoundation.RunLoopCommonModes);
            CoreGraphics.CGEventTapEnable(_tap, true);
            started.Set();
            CoreFoundation.CFRunLoopRun();
            CoreFoundation.CFRelease(source);
        })
        {
            IsBackground = true,
            Name = "SnapCut.RecordingInput",
        };
        _thread.Start();
        return started.Wait(TimeSpan.FromSeconds(2));
    }

    private IntPtr HandleEvent(
        IntPtr proxy,
        uint eventType,
        IntPtr cgEvent,
        IntPtr userInfo)
    {
        if (eventType == CoreGraphics.EventKeyDown && _keyboard)
        {
            var code = (int)CoreGraphics.CGEventGetIntegerValueField(
                cgEvent,
                CoreGraphics.KeyboardEventKeycode);
            KeyPressed?.Invoke(KeyName(code));
        }
        else if (_mouse && eventType is CoreGraphics.EventLeftMouseDown or
                 CoreGraphics.EventRightMouseDown)
        {
            MousePressed?.Invoke(
                CoreGraphics.CGEventGetLocation(cgEvent),
                eventType == CoreGraphics.EventRightMouseDown);
        }

        return cgEvent;
    }

    private static string KeyName(int code) => code switch
    {
        36 => "Return",
        49 => "Space",
        51 => "Delete",
        53 => "Esc",
        123 => "←",
        124 => "→",
        125 => "↓",
        126 => "↑",
        0 => "A", 1 => "S", 2 => "D", 3 => "F", 4 => "H", 5 => "G",
        6 => "Z", 7 => "X", 8 => "C", 9 => "V", 11 => "B", 12 => "Q",
        13 => "W", 14 => "E", 15 => "R", 16 => "Y", 17 => "T",
        31 => "O", 35 => "P",
        _ => $"Key {code}",
    };

    public void Dispose()
    {
        if (_tap != IntPtr.Zero)
        {
            CoreGraphics.CGEventTapEnable(_tap, false);
        }
        if (_runLoop != IntPtr.Zero)
        {
            CoreFoundation.CFRunLoopStop(_runLoop);
        }
        _thread?.Join(TimeSpan.FromSeconds(1));
        if (_tap != IntPtr.Zero)
        {
            CoreFoundation.CFRelease(_tap);
            _tap = IntPtr.Zero;
        }
    }
}
