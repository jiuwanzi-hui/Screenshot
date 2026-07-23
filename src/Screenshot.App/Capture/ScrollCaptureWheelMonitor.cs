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
    private const int MiddleButtonDownMessage = 0x0207;
    private const int MiddleButtonUpMessage = 0x0208;
    private const int HorizontalWheelMessage = 0x020E;
    private const int XButtonDownMessage = 0x020B;
    private const int XButtonUpMessage = 0x020C;

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
    private readonly HookProcedure _hookProcedure;
    private IntPtr _hookHandle;
    private bool _disposed;
    private volatile bool _blockNonWheelInput;

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
                "无法监听选区内的鼠标滚轮。");
        }

    }

    public ChannelReader<int> WheelEvents => _wheelEvents.Reader;

    public void BlockNonWheelInput()
    {
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

        if (_hookHandle != IntPtr.Zero)
        {
            _ = NativeMethods.UnhookWindowsHookEx(_hookHandle);
            _hookHandle = IntPtr.Zero;
        }


        _wheelEvents.Writer.TryComplete();
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
                var wheelDelta = unchecked((short)(hookData.MouseData >> 16));

                if (wheelDelta != 0)
                {
                    // Lock pointer actions before notifying the coordinator so
                    // there is no gap between the first wheel message and the
                    // right-click cancellation path.
                    _blockNonWheelInput = true;
                    try
                    {
                        _wheelDetected?.Invoke(wheelDelta);
                    }
                    catch
                    {
                    }

                    _wheelEvents.Writer.TryWrite(wheelDelta);
                }
            }
        }

        if (hookCode >= 0 &&
            _blockNonWheelInput &&
            IsBlockedPointerMessage(message.ToInt32()))
        {
            var hookData = Marshal.PtrToStructure<LowLevelMouseHookData>(
                hookDataPointer);

            ScreenRegion captureRegion;
            lock (_captureRegionSync)
            {
                captureRegion = _captureRegion;
            }

            // The full-screen overlay already prevents interaction outside the
            // selection. Keep the low-level hook limited to the click-through
            // selection itself so a bad preview-window hit test can never lock
            // mouse input across the whole desktop.
            if (!captureRegion.Contains(hookData.Point.X, hookData.Point.Y))
            {
                return NativeMethods.CallNextHookEx(
                    _hookHandle,
                    hookCode,
                    message,
                    hookDataPointer);
            }

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

            if (message.ToInt32() == RightButtonDownMessage)
            {
                try
                {
                    _cancelRequested?.Invoke();
                }
                catch
                {
                }

                // The cancellation callback owns the right-click. Do not let
                // it reach the target application underneath the overlay.
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


    private static bool IsBlockedPointerMessage(int message)
    {
        return message is LeftButtonDownMessage or
            LeftButtonUpMessage or
            RightButtonDownMessage or
            RightButtonUpMessage or
            MiddleButtonDownMessage or
            MiddleButtonUpMessage or
            XButtonDownMessage or
            XButtonUpMessage or
            HorizontalWheelMessage;
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
    }
}
