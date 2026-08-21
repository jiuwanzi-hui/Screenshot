using SnapCut.Mac.App;
using SnapCut.Mac.Native;

namespace SnapCut.Mac.Input;

internal enum MacHotkeyAction
{
    Capture,
    ScrollCapture,
    VideoRecording,
    RecognizeText,
    Translation,
    PinImage,
    OpenSettings,
}

internal sealed class MacGlobalHotkeyService : IDisposable
{
    private readonly CoreGraphics.EventTapCallback _callback;
    private Thread? _runLoopThread;
    private IntPtr _tap;
    private IntPtr _runLoop;
    private bool _disposed;
    private MacHotkeyGesture _capture = MacHotkeyGesture.CaptureDefault;
    private MacHotkeyGesture _scroll = MacHotkeyGesture.ScrollDefault;
    private MacHotkeyGesture _recording = MacHotkeyGesture.RecordingDefault;
    private MacHotkeyGesture _ocr = MacHotkeyGesture.OcrDefault;
    private MacHotkeyGesture _translation = MacHotkeyGesture.TranslationDefault;
    private MacHotkeyGesture _pin = MacHotkeyGesture.PinDefault;
    private MacHotkeyGesture _settings = MacHotkeyGesture.SettingsDefault;

    public MacGlobalHotkeyService()
    {
        _callback = HandleEvent;
    }

    public event Action<MacHotkeyAction>? Pressed;

    public bool IsRunning { get; private set; }

    public static bool HasInputMonitoringAccess() =>
        CoreGraphics.CGPreflightListenEventAccess();

    public static bool RequestInputMonitoringAccess() =>
        CoreGraphics.CGRequestListenEventAccess();

    public void Update(MacSettings settings)
    {
        _capture = settings.CaptureHotkey;
        _scroll = settings.ScrollHotkey;
        _recording = settings.RecordingHotkey;
        _ocr = settings.OcrHotkey;
        _translation = settings.TranslationHotkey;
        _pin = settings.PinHotkey;
        _settings = settings.SettingsHotkey;
    }

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
            1UL << (int)CoreGraphics.EventKeyDown,
            _callback,
            IntPtr.Zero);
        if (_tap == IntPtr.Zero)
        {
            return false;
        }

        using var started = new ManualResetEventSlim(false);
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
            Name = "SnapCut.GlobalHotkeys",
        };
        _runLoopThread.Start();
        if (!started.Wait(TimeSpan.FromSeconds(2)))
        {
            return false;
        }

        IsRunning = true;
        return true;
    }

    private IntPtr HandleEvent(
        IntPtr proxy,
        uint eventType,
        IntPtr cgEvent,
        IntPtr userInfo)
    {
        if (eventType is CoreGraphics.EventTapDisabledByTimeout or
            CoreGraphics.EventTapDisabledByUserInput)
        {
            CoreGraphics.CGEventTapEnable(_tap, true);
            return cgEvent;
        }

        if (eventType != CoreGraphics.EventKeyDown)
        {
            return cgEvent;
        }

        if (CoreGraphics.CGEventGetIntegerValueField(
                cgEvent,
                CoreGraphics.KeyboardEventAutorepeat) != 0)
        {
            return cgEvent;
        }

        var keyCode = (ushort)CoreGraphics.CGEventGetIntegerValueField(
            cgEvent,
            CoreGraphics.KeyboardEventKeycode);
        var flags = CoreGraphics.CGEventGetFlags(cgEvent);
        if (_capture.Matches(keyCode, flags))
        {
            Pressed?.Invoke(MacHotkeyAction.Capture);
        }
        else if (_scroll.Matches(keyCode, flags))
        {
            Pressed?.Invoke(MacHotkeyAction.ScrollCapture);
        }
        else if (_recording.Matches(keyCode, flags))
        {
            Pressed?.Invoke(MacHotkeyAction.VideoRecording);
        }
        else if (_ocr.Matches(keyCode, flags))
        {
            Pressed?.Invoke(MacHotkeyAction.RecognizeText);
        }
        else if (_translation.Matches(keyCode, flags))
        {
            Pressed?.Invoke(MacHotkeyAction.Translation);
        }
        else if (_pin.Matches(keyCode, flags))
        {
            Pressed?.Invoke(MacHotkeyAction.PinImage);
        }
        else if (_settings.Matches(keyCode, flags))
        {
            Pressed?.Invoke(MacHotkeyAction.OpenSettings);
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
