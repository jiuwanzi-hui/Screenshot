using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading.Channels;

namespace Screenshot.App.Capture;

public sealed class ScrollCaptureWheelMonitor : IDisposable
{
    private const int LowLevelMouseHook = 14;
    private const int MouseWheelMessage = 0x020A;
    private const int LeftButtonDownMessage = 0x0201;
    private const int LeftButtonUpMessage = 0x0202;
    private const int RightButtonDownMessage = 0x0204;
    private const int RightButtonUpMessage = 0x0205;
    private const uint InjectedMouseEvent = 0x00000001;
    private const uint MousePointerSignature = 0xFF515700;
    private const uint MousePointerSignatureMask = 0xFFFFFF00;
    private const uint MousePointerTouchFlag = 0x00000080;

    private readonly object _captureRegionSync = new();
    private ScreenRegion _captureRegion;
    private readonly Action<int>? _wheelDetected;
    private readonly Action? _cancelRequested;
    private readonly Func<int, int, bool>? _allowBlockedInputAt;
    private readonly Channel<int> _wheelEvents = Channel.CreateUnbounded<int>(
        new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true,
        });
    private readonly Channel<ScrollCapturePointerAction> _pointerActions =
        Channel.CreateUnbounded<ScrollCapturePointerAction>(
            new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false,
            });
    private readonly HookProcedure _hookProcedure;
    private IntPtr _hookHandle;
    private bool _disposed;
    private volatile bool _blockNonWheelInput;
    private volatile bool _controlledCaptureInput;
    private volatile bool _throttledWheelInput;
    private CancellationTokenSource? _pendingClickCancellation;
    private volatile Func<bool>? _shouldDeferClicks;
    private long _clickGeneration;
    private bool _rightButtonCancellationPending;
    private bool _leftButtonCapturePending;
    private readonly object _clickSync = new();
    private uint _lastLeftButtonUpTime;
    private NativePoint _lastLeftButtonUpPoint;
    private int _wheelDetectionRaised;

    public ScrollCaptureWheelMonitor(
        ScreenRegion captureRegion,
        Action<int>? wheelDetected = null,
        Func<int, int, bool>? allowBlockedInputAt = null,
        Action? cancelRequested = null)
    {
        if (captureRegion.IsEmpty)
        {
            throw new ArgumentException("滚动截图区域不能为空。", nameof(captureRegion));
        }

        _captureRegion = captureRegion;
        _wheelDetected = wheelDetected;
        _allowBlockedInputAt = allowBlockedInputAt;
        _cancelRequested = cancelRequested;
        _hookProcedure = OnMouseHook;
        _hookHandle = NativeMethods.SetWindowsHookEx(
            LowLevelMouseHook,
            _hookProcedure,
            NativeMethods.GetModuleHandle(moduleName: null),
            threadId: 0);

        if (_hookHandle == IntPtr.Zero)
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "无法监听长截图控制输入。");
        }

    }

    public ChannelReader<int> WheelEvents => _wheelEvents.Reader;

    public ChannelReader<ScrollCapturePointerAction> PointerActions =>
        _pointerActions.Reader;

    public void BlockNonWheelInput()
    {
        _blockNonWheelInput = true;
    }

    public void EnableControlledCaptureInput()
    {
        _controlledCaptureInput = true;
        _blockNonWheelInput = true;
    }

    /// <summary>
    /// Lets the coordinator decide per-click whether the single-click
    /// deferral (used to distinguish it from a double-click) is required.
    /// When the callback returns false the click is delivered immediately,
    /// so pausing an active scroll does not lag by the double-click interval.
    /// </summary>
    public void ConfigureClickDeferral(Func<bool> shouldDeferClicks)
    {
        ArgumentNullException.ThrowIfNull(shouldDeferClicks);
        _shouldDeferClicks = shouldDeferClicks;
    }

    /// <summary>
    /// Keeps the user's wheel direction as the gesture source, but prevents
    /// the target from consuming an unbounded burst.  CaptureOnWheelAsync
    /// replays the queued direction at a fixed cadence so the matcher always
    /// receives overlapping frames instead of an unrecoverable viewport jump.
    /// </summary>
    public void EnableThrottledWheelInput()
    {
        _throttledWheelInput = true;
        _blockNonWheelInput = true;
    }

    public void UpdateCaptureRegion(ScreenRegion captureRegion)
    {
        if (captureRegion.IsEmpty)
        {
            return;
        }

        lock (_captureRegionSync)
        {
            _captureRegion = captureRegion;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        lock (_clickSync)
        {
            _pendingClickCancellation?.Cancel();
            _pendingClickCancellation?.Dispose();
            _pendingClickCancellation = null;
            _clickGeneration++;
        }

        if (_hookHandle != IntPtr.Zero)
        {
            _ = NativeMethods.UnhookWindowsHookEx(_hookHandle);
            _hookHandle = IntPtr.Zero;
        }


        _wheelEvents.Writer.TryComplete();
        _pointerActions.Writer.TryComplete();
    }

    private IntPtr OnMouseHook(
        int hookCode,
        IntPtr message,
        IntPtr hookDataPointer)
    {
        if (hookCode >= 0 &&
            message.ToInt32() == MouseWheelMessage &&
            !_disposed)
        {
            var hookData = Marshal.PtrToStructure<LowLevelMouseHookData>(
                hookDataPointer);

            ScreenRegion captureRegion;
            lock (_captureRegionSync)
            {
                captureRegion = _captureRegion;
            }

            if (captureRegion.Contains(hookData.Point.X, hookData.Point.Y))
            {
                if (_controlledCaptureInput)
                {
                    // Programmatic wheel input must reach the target. Physical
                    // wheel input is blocked so it cannot violate the one-way
                    // controlled capture sequence or outrun the stitcher.
                    if ((hookData.Flags & InjectedMouseEvent) == 0)
                    {
                        return new IntPtr(1);
                    }

                    return NativeMethods.CallNextHookEx(
                        _hookHandle,
                        hookCode,
                        message,
                        hookDataPointer);
                }

                if (_throttledWheelInput &&
                    (hookData.Flags & InjectedMouseEvent) == 0)
                {
                    var throttledDelta = unchecked(
                        (short)(hookData.MouseData >> 16));
                    if (throttledDelta != 0)
                    {
                        // Preserve direction input for the replay driver, but
                        // swallow the physical packet so a high-speed fling
                        // cannot outrun screen sampling.
                        _wheelEvents.Writer.TryWrite(throttledDelta);
                    }

                    return new IntPtr(1);
                }

                if (_throttledWheelInput &&
                    (hookData.Flags & InjectedMouseEvent) != 0)
                {
                    return NativeMethods.CallNextHookEx(
                        _hookHandle,
                        hookCode,
                        message,
                        hookDataPointer);
                }

                var wheelDelta = unchecked((short)(hookData.MouseData >> 16));

                if (wheelDelta != 0)
                {
                    // Lock pointer actions before notifying the coordinator so
                    // there is no gap between the first wheel message and the
                    // right-click cancellation path.
                    _blockNonWheelInput = true;
                    // A low-level hook must return promptly. The callback exists
                    // only to perform first-wheel setup; invoking it for every
                    // detent used to synchronously wait on the WPF dispatcher and
                    // could make Windows silently remove this hook after a burst.
                    if (Interlocked.CompareExchange(
                            ref _wheelDetectionRaised,
                            1,
                            0) == 0)
                    {
                        try
                        {
                            _wheelDetected?.Invoke(wheelDelta);
                        }
                        catch
                        {
                        }
                    }

                    _wheelEvents.Writer.TryWrite(wheelDelta);
                }
            }
        }

        if (hookCode >= 0 &&
            _controlledCaptureInput &&
            message.ToInt32() is LeftButtonDownMessage or LeftButtonUpMessage)
        {
            var hookData = Marshal.PtrToStructure<LowLevelMouseHookData>(
                hookDataPointer);
            if (IsTouchPromotedMouse(hookData.ExtraInformation))
            {
                // A touch drag is also promoted to a synthetic mouse gesture.
                // Swallow that compatibility event: WM_POINTER still reaches
                // the target, while the promoted button-up cannot be mistaken
                // for a user's pause click. Do not classify by LLMHF_INJECTED;
                // remote mice and some touchpads carry that flag as well.
                return new IntPtr(1);
            }

            ScreenRegion captureRegion;
            lock (_captureRegionSync)
            {
                captureRegion = _captureRegion;
            }

            var pointerMessage = message.ToInt32();
            if (pointerMessage == LeftButtonDownMessage &&
                captureRegion.Contains(hookData.Point.X, hookData.Point.Y))
            {
                // The progress window may sit over the selected region on a
                // small display. Let its Edit/Complete/Cancel buttons receive
                // the click instead of treating it as a capture gesture.
                if (_allowBlockedInputAt?.Invoke(
                        hookData.Point.X,
                        hookData.Point.Y) == true)
                {
                    return NativeMethods.CallNextHookEx(
                        _hookHandle,
                        hookCode,
                        message,
                        hookDataPointer);
                }

                _leftButtonCapturePending = true;
                return new IntPtr(1);
            }

            if (pointerMessage == LeftButtonUpMessage &&
                _leftButtonCapturePending)
            {
                _leftButtonCapturePending = false;
                if (ShouldCompleteControlledClick(
                        captureRegion,
                        hookData.Point.X,
                        hookData.Point.Y))
                {
                    QueueClickGesture(hookData);
                }

                return new IntPtr(1);
            }
        }

        if (hookCode >= 0 &&
            _blockNonWheelInput &&
            IsBlockedPointerMessage(message.ToInt32()))
        {
            var hookData = Marshal.PtrToStructure<LowLevelMouseHookData>(
                hookDataPointer);
            var pointerMessage = message.ToInt32();

            // Keep the hook alive through the complete right-click gesture.
            // Canceling on button-down disposed the hook before button-up and
            // allowed the final event to leak into the application underneath.
            if (pointerMessage == RightButtonUpMessage &&
                _rightButtonCancellationPending)
            {
                _rightButtonCancellationPending = false;
                try
                {
                    _cancelRequested?.Invoke();
                }
                catch
                {
                }

                return new IntPtr(1);
            }

            // Let the preview handle its own right click. Everywhere else the
            // complete gesture is reserved for canceling the capture.
            if (_allowBlockedInputAt?.Invoke(
                    hookData.Point.X,
                    hookData.Point.Y) == true)
            {
                return NativeMethods.CallNextHookEx(
                    _hookHandle,
                    hookCode,
                    message,
                    hookDataPointer);
            }

            if (pointerMessage == RightButtonDownMessage)
            {
                _rightButtonCancellationPending = true;
                return new IntPtr(1);
            }

            return new IntPtr(1);
        }

        return NativeMethods.CallNextHookEx(
            _hookHandle,
            hookCode,
            message,
            hookDataPointer);
    }

    private void QueueClickGesture(LowLevelMouseHookData hookData)
    {
        var doubleClickTime = NativeMethods.GetDoubleClickTime();
        var isDoubleClick = false;
        lock (_clickSync)
        {
            var elapsed = unchecked(hookData.Time - _lastLeftButtonUpTime);
            var closeEnough =
                Math.Abs(hookData.Point.X - _lastLeftButtonUpPoint.X) <= 6 &&
                Math.Abs(hookData.Point.Y - _lastLeftButtonUpPoint.Y) <= 6;
            isDoubleClick = _lastLeftButtonUpTime != 0 &&
                            elapsed <= doubleClickTime &&
                            closeEnough;

            if (isDoubleClick)
            {
                _lastLeftButtonUpTime = 0;
            }
            else
            {
                _lastLeftButtonUpTime = hookData.Time;
                _lastLeftButtonUpPoint = hookData.Point;
            }
        }

        if (isDoubleClick)
        {
            lock (_clickSync)
            {
                _pendingClickCancellation?.Cancel();
                _pendingClickCancellation?.Dispose();
                _pendingClickCancellation = null;
                _clickGeneration++;
            }
            _pointerActions.Writer.TryWrite(
                ScrollCapturePointerAction.DoubleClick);
            return;
        }

        // A controlled capture assigns different meanings to a single click
        // and a double-click (resume downward vs. return upward). Publishing
        // the first click immediately makes the first half of a double-click
        // resume the downward driver before the second half is recognized.
        // Coalesce only in controlled mode; manual-wheel capture does not use
        // pointer gestures. A single click is still delivered after the normal
        // Windows double-click interval.
        if (!_controlledCaptureInput)
        {
            _pointerActions.Writer.TryWrite(ScrollCapturePointerAction.Click);
            return;
        }

        // While the capture is actively scrolling, a click means "stop now"
        // and the state machine maps a follow-up double-click from the
        // pause-preparing state to the same action it has from the motion
        // state, so nothing is lost by delivering the click immediately.
        // Waiting out the double-click interval here made every pause feel
        // like a one-second lag.
        if (_shouldDeferClicks?.Invoke() == false)
        {
            lock (_clickSync)
            {
                _pendingClickCancellation?.Cancel();
                _pendingClickCancellation?.Dispose();
                _pendingClickCancellation = null;
                _clickGeneration++;
            }

            _pointerActions.Writer.TryWrite(ScrollCapturePointerAction.Click);
            return;
        }

        CancellationTokenSource cancellation;
        long generation;
        lock (_clickSync)
        {
            _pendingClickCancellation?.Cancel();
            _pendingClickCancellation?.Dispose();
            cancellation = new CancellationTokenSource();
            _pendingClickCancellation = cancellation;
            generation = ++_clickGeneration;
        }

        _ = PublishDeferredClickAsync(
            cancellation,
            generation,
            TimeSpan.FromMilliseconds(NativeMethods.GetDoubleClickTime()));
    }

    private async Task PublishDeferredClickAsync(
        CancellationTokenSource cancellation,
        long generation,
        TimeSpan delay)
    {
        try
        {
            await Task.Delay(delay, cancellation.Token);
            lock (_clickSync)
            {
                if (_disposed ||
                    generation != _clickGeneration ||
                    !ReferenceEquals(_pendingClickCancellation, cancellation))
                {
                    return;
                }

                _pendingClickCancellation = null;
                _lastLeftButtonUpTime = 0;
            }

            _pointerActions.Writer.TryWrite(ScrollCapturePointerAction.Click);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            cancellation.Dispose();
        }
    }


    private static bool IsBlockedPointerMessage(int message)
    {
        // Right click is reserved for canceling the capture. All other pointer
        // input must continue normally; globally swallowing the left button
        // broke unrelated tools such as WeChat's screenshot selector.
        return message is RightButtonDownMessage or RightButtonUpMessage;
    }

    internal static bool ShouldCompleteControlledClick(
        ScreenRegion captureRegion,
        int releaseX,
        int releaseY)
    {
        return captureRegion.Contains(releaseX, releaseY);
    }

    internal static bool IsTouchPromotedMouse(IntPtr extraInformation)
    {
        var value = unchecked((uint)extraInformation.ToInt64());
        return (value & MousePointerSignatureMask) == MousePointerSignature &&
               (value & MousePointerTouchFlag) != 0;
    }

    private delegate IntPtr HookProcedure(
        int hookCode,
        IntPtr message,
        IntPtr hookDataPointer);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LowLevelMouseHookData
    {
        public NativePoint Point;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInformation;
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetWindowsHookEx(
            int hookIdentifier,
            HookProcedure hookProcedure,
            IntPtr moduleHandle,
            uint threadId);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool UnhookWindowsHookEx(IntPtr hookHandle);

        [DllImport("user32.dll")]
        public static extern IntPtr CallNextHookEx(
            IntPtr hookHandle,
            int hookCode,
            IntPtr message,
            IntPtr hookDataPointer);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr GetModuleHandle(string? moduleName);

        [DllImport("user32.dll")]
        public static extern uint GetDoubleClickTime();
    }
}
