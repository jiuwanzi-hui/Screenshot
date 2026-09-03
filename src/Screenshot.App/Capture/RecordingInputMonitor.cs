using System.Runtime.InteropServices;
using System.Windows.Threading;
using Screenshot.App.Core;
using Screenshot.App.Infrastructure;

namespace Screenshot.App.Capture;

internal sealed class RecordingInputChangedEventArgs(
    string displayText,
    bool isTransient) : EventArgs
{
    public string DisplayText { get; } = displayText;

    public bool IsTransient { get; } = isTransient;
}

internal sealed class RecordingMouseMovedEventArgs(int x, int y) : EventArgs
{
    public int X { get; } = x;

    public int Y { get; } = y;
}

internal sealed class RecordingInputMonitor : IDisposable
{
    private const int LowLevelKeyboardHook = 13;
    private const int LowLevelMouseHook = 14;
    private const int KeyDownMessage = 0x0100;
    private const int KeyUpMessage = 0x0101;
    private const int SystemKeyDownMessage = 0x0104;
    private const int SystemKeyUpMessage = 0x0105;
    private const int MouseMoveMessage = 0x0200;
    private const int LeftButtonDownMessage = 0x0201;
    private const int LeftButtonUpMessage = 0x0202;
    private const int RightButtonDownMessage = 0x0204;
    private const int RightButtonUpMessage = 0x0205;
    private const int MiddleButtonDownMessage = 0x0207;
    private const int MiddleButtonUpMessage = 0x0208;
    private const int MouseWheelMessage = 0x020A;
    private const int XButtonDownMessage = 0x020B;
    private const int XButtonUpMessage = 0x020C;
    private const int HorizontalMouseWheelMessage = 0x020E;
    private const uint XButtonBack = 1;
    private const uint XButtonForward = 2;
    private const uint InjectedKeyboardFlag = 0x10;
    private const uint InjectedMouseFlag = 0x01;

    private readonly bool _showKeyboard;
    private readonly bool _showMouse;
    private readonly bool _showMouseTrail;
    private readonly NativeMethods.LowLevelKeyboardProcedure _keyboardProcedure;
    private readonly NativeMethods.LowLevelMouseProcedure _mouseProcedure;
    private readonly List<uint> _pressedKeys = [];
    private readonly List<string> _pressedMouseButtons = [];
    private readonly DispatcherTimer _keyboardStateTimer;
    private readonly TimeSpan _interactionFrameInterval;
    private IntPtr _keyboardHook;
    private IntPtr _mouseHook;
    private bool _isPaused;
    private bool _disposed;
    private long _lastMouseMoveTimestamp;
    private NativeMethods.NativePoint _lastMousePoint;
    private static readonly uint[] CommonVirtualKeys =
    [
        0x10, 0x11, 0x12, 0x5B, 0x5C,
        0x08, 0x09, 0x0D, 0x1B, 0x20,
        0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28,
        0x2D, 0x2E,
        0x30, 0x31, 0x32, 0x33, 0x34, 0x35, 0x36, 0x37, 0x38, 0x39,
        0x41, 0x42, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48, 0x49, 0x4A,
        0x4B, 0x4C, 0x4D, 0x4E, 0x4F, 0x50, 0x51, 0x52, 0x53, 0x54,
        0x55, 0x56, 0x57, 0x58, 0x59, 0x5A,
        0x70, 0x71, 0x72, 0x73, 0x74, 0x75, 0x76, 0x77,
        0x78, 0x79, 0x7A, 0x7B, 0x7C, 0x7D, 0x7E, 0x7F, 0x80, 0x81,
        0x82, 0x83, 0x84, 0x85, 0x86, 0x87,
    ];

    private static readonly (uint VirtualKey, string Token)[] CommonMouseButtons =
    [
        (0x01, "鼠标左键"),
        (0x02, "鼠标右键"),
        (0x04, "鼠标中键"),
        (0x05, "鼠标后退键"),
        (0x06, "鼠标前进键"),
    ];

    public RecordingInputMonitor(
        bool showKeyboard,
        bool showMouse,
        bool showMouseTrail = false)
    {
        _showKeyboard = showKeyboard;
        _showMouse = showMouse;
        _showMouseTrail = showMouseTrail;
        var virtualBounds = VirtualScreen.GetBounds();
        _interactionFrameInterval =
            DisplayRefreshRateService.GetInteractionFrameInterval(
                new System.Drawing.Rectangle(
                    virtualBounds.X,
                    virtualBounds.Y,
                    virtualBounds.Width,
                    virtualBounds.Height));
        _keyboardStateTimer = new DispatcherTimer
        {
            Interval = _interactionFrameInterval,
        };
        _keyboardStateTimer.Tick += OnInputStateTimerTick;
        _keyboardProcedure = OnKeyboardInput;
        _mouseProcedure = OnMouseInput;
    }

    public event EventHandler<RecordingInputChangedEventArgs>? InputChanged;

    public event EventHandler<RecordingMouseMovedEventArgs>? MouseMoved;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var moduleHandle = NativeMethods.GetModuleHandle(moduleName: null);
        if (_showKeyboard && _keyboardHook == IntPtr.Zero)
        {
            _keyboardHook = NativeMethods.SetWindowsHookEx(
                LowLevelKeyboardHook,
                _keyboardProcedure,
                moduleHandle,
                threadId: 0);
        }

        if ((_showMouse || _showMouseTrail) && _mouseHook == IntPtr.Zero)
        {
            _mouseHook = NativeMethods.SetWindowsHookEx(
                LowLevelMouseHook,
                _mouseProcedure,
                moduleHandle,
                threadId: 0);
        }

        if ((_showKeyboard || _showMouse) && !_isPaused)
        {
            _keyboardStateTimer.Start();
        }
    }

    public void SetPaused(bool isPaused)
    {
        if (_isPaused == isPaused)
        {
            return;
        }

        _isPaused = isPaused;
        _pressedKeys.Clear();
        _pressedMouseButtons.Clear();
        _keyboardStateTimer.Stop();
        if (!isPaused && (_showKeyboard || _showMouse))
        {
            _keyboardStateTimer.Start();
        }
        PublishCurrent(isTransient: true);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_keyboardHook != IntPtr.Zero)
        {
            _ = NativeMethods.UnhookWindowsHookEx(_keyboardHook);
            _keyboardHook = IntPtr.Zero;
        }

        if (_mouseHook != IntPtr.Zero)
        {
            _ = NativeMethods.UnhookWindowsHookEx(_mouseHook);
            _mouseHook = IntPtr.Zero;
        }

        _pressedKeys.Clear();
        _pressedMouseButtons.Clear();
        _keyboardStateTimer.Stop();
        _keyboardStateTimer.Tick -= OnInputStateTimerTick;
    }

    internal static string JoinInputTokens(
        IEnumerable<string> keyboardTokens,
        IEnumerable<string> mouseTokens)
    {
        return string.Join(
            " + ",
            keyboardTokens
                .Concat(mouseTokens)
                .Where(token => !string.IsNullOrWhiteSpace(token))
                .Distinct(StringComparer.Ordinal));
    }

    internal static string GetKeyboardToken(uint virtualKey)
    {
        if (virtualKey is >= 'A' and <= 'Z' or >= '0' and <= '9')
        {
            return char.ConvertFromUtf32((int)virtualKey);
        }

        if (virtualKey is >= 0x70 and <= 0x87)
        {
            return $"F{virtualKey - 0x70 + 1}";
        }

        if (virtualKey is >= 0x60 and <= 0x69)
        {
            return $"Num {virtualKey - 0x60}";
        }

        return virtualKey switch
        {
            0x10 or 0xA0 or 0xA1 => "Shift",
            0x11 or 0xA2 or 0xA3 => "Ctrl",
            0x12 or 0xA4 or 0xA5 => "Alt",
            0x5B or 0x5C => "Win",
            0x08 => "Backspace",
            0x09 => "Tab",
            0x0D => "Enter",
            0x1B => "Esc",
            0x20 => "Space",
            0x21 => "PageUp",
            0x22 => "PageDown",
            0x23 => "End",
            0x24 => "Home",
            0x25 => "Left",
            0x26 => "Up",
            0x27 => "Right",
            0x28 => "Down",
            0x2D => "Insert",
            0x2E => "Delete",
            0x6A => "Num *",
            0x6B => "Num +",
            0x6D => "Num -",
            0x6E => "Num .",
            0x6F => "Num /",
            0xBA => ";",
            0xBB => "+",
            0xBC => ",",
            0xBD => "-",
            0xBE => ".",
            0xBF => "/",
            0xC0 => "`",
            0xDB => "[",
            0xDC => "\\",
            0xDD => "]",
            0xDE => "'",
            _ => new HotKeyGesture(HotKeyModifiers.None, virtualKey).ToString(),
        };
    }

    private IntPtr OnKeyboardInput(int code, IntPtr message, IntPtr dataPointer)
    {
        if (code >= 0 && !_isPaused)
        {
            var data = Marshal.PtrToStructure<NativeMethods.LowLevelKeyboardData>(
                dataPointer);
            if ((data.Flags & InjectedKeyboardFlag) == 0)
            {
                var messageId = unchecked((int)message.ToInt64());
                if (messageId is KeyDownMessage or SystemKeyDownMessage)
                {
                    if (!_pressedKeys.Contains(data.VirtualKey))
                    {
                        _pressedKeys.Add(data.VirtualKey);
                        PublishCurrent(isTransient: false);
                    }
                }
                else if (messageId is KeyUpMessage or SystemKeyUpMessage)
                {
                    _pressedKeys.Remove(data.VirtualKey);
                    PublishEmptyWhenReleased();
                }
            }
        }

        return NativeMethods.CallNextHookEx(
            _keyboardHook,
            code,
            message,
            dataPointer);
    }

    private void OnInputStateTimerTick(object? sender, EventArgs e)
    {
        if (_isPaused)
        {
            return;
        }

        var changed = false;
        if (_showKeyboard)
        {
            var observedKeys = CommonVirtualKeys
                .Where(key => (NativeMethods.GetAsyncKeyState((int)key) & 0x8000) != 0)
                .ToHashSet();
            if (_pressedKeys.Count != observedKeys.Count ||
                !_pressedKeys.All(observedKeys.Contains))
            {
                _pressedKeys.Clear();
                _pressedKeys.AddRange(observedKeys);
                changed = true;
            }
        }

        if (_showMouse)
        {
            var observedButtons = CommonMouseButtons
                .Where(button =>
                    (NativeMethods.GetAsyncKeyState((int)button.VirtualKey) & 0x8000) != 0)
                .Select(button => button.Token)
                .ToHashSet(StringComparer.Ordinal);
            if (_pressedMouseButtons.Count != observedButtons.Count ||
                !_pressedMouseButtons.All(observedButtons.Contains))
            {
                _pressedMouseButtons.Clear();
                _pressedMouseButtons.AddRange(observedButtons);
                changed = true;
            }
        }

        if (changed)
        {
            PublishCurrent(isTransient: false);
        }
    }

    private IntPtr OnMouseInput(int code, IntPtr message, IntPtr dataPointer)
    {
        if (code >= 0 && !_isPaused)
        {
            var nativeMessage = unchecked((int)message.ToInt64());
            // Cursor position is sampled by RecordingInputOverlayWindow on
            // the display frame. Parsing every raw move in this low-level
            // hook only adds synchronous work to the system input path and
            // can make high-report-rate mice appear to pause.
            if (nativeMessage == MouseMoveMessage)
            {
                return NativeMethods.CallNextHookEx(
                    _mouseHook,
                    code,
                    message,
                    dataPointer);
            }

            var data = Marshal.PtrToStructure<NativeMethods.LowLevelMouseData>(
                dataPointer);
            if ((data.Flags & InjectedMouseFlag) == 0)
            {
                ProcessMouseMessage(
                    unchecked((int)message.ToInt64()),
                    data.MouseData,
                    data.Point);
            }
        }

        return NativeMethods.CallNextHookEx(
            _mouseHook,
            code,
            message,
            dataPointer);
    }

    private void ProcessMouseMessage(
        int message,
        uint mouseData,
        NativeMethods.NativePoint point)
    {
        switch (message)
        {
            case MouseMoveMessage when _showMouseTrail:
                PublishMouseMove(point);
                break;
            case LeftButtonDownMessage:
                AddMouseButton("鼠标左键");
                break;
            case RightButtonDownMessage:
                AddMouseButton("鼠标右键");
                break;
            case MiddleButtonDownMessage:
                AddMouseButton("鼠标中键");
                break;
            case XButtonDownMessage:
                AddMouseButton(GetXButtonToken(mouseData));
                break;
            case LeftButtonUpMessage:
                RemoveMouseButton("鼠标左键");
                break;
            case RightButtonUpMessage:
                RemoveMouseButton("鼠标右键");
                break;
            case MiddleButtonUpMessage:
                RemoveMouseButton("鼠标中键");
                break;
            case XButtonUpMessage:
                RemoveMouseButton(GetXButtonToken(mouseData));
                break;
            case MouseWheelMessage:
                PublishCurrent(
                    isTransient: true,
                    GetWheelDelta(mouseData) >= 0 ? "滚轮向上" : "滚轮向下");
                break;
            case HorizontalMouseWheelMessage:
                PublishCurrent(
                    isTransient: true,
                    GetWheelDelta(mouseData) >= 0 ? "滚轮向右" : "滚轮向左");
                break;
        }
    }

    private void PublishMouseMove(NativeMethods.NativePoint point)
    {
        var timestamp = Environment.TickCount64;
        var movedEnough = Math.Abs(point.X - _lastMousePoint.X) >= 2 ||
            Math.Abs(point.Y - _lastMousePoint.Y) >= 2;
        if (!movedEnough ||
            timestamp - _lastMouseMoveTimestamp <
            _interactionFrameInterval.TotalMilliseconds)
        {
            return;
        }

        _lastMousePoint = point;
        _lastMouseMoveTimestamp = timestamp;
        MouseMoved?.Invoke(this, new RecordingMouseMovedEventArgs(point.X, point.Y));
    }

    private void AddMouseButton(string token)
    {
        if (!_pressedMouseButtons.Contains(token, StringComparer.Ordinal))
        {
            _pressedMouseButtons.Add(token);
            PublishCurrent(isTransient: false);
        }
    }

    private void RemoveMouseButton(string token)
    {
        _pressedMouseButtons.Remove(token);
        PublishEmptyWhenReleased();
    }

    private void PublishEmptyWhenReleased()
    {
        if (_pressedKeys.Count == 0 && _pressedMouseButtons.Count == 0)
        {
            PublishCurrent(isTransient: true);
        }
    }

    private void PublishCurrent(
        bool isTransient,
        string? transientMouseToken = null)
    {
        var keyboardTokens = _showKeyboard
            ? _pressedKeys.Select(GetKeyboardToken)
            : [];
        var mouseTokens = _showMouse
            ? _pressedMouseButtons.Concat(
                transientMouseToken is null ? [] : [transientMouseToken])
            : [];
        InputChanged?.Invoke(
            this,
            new RecordingInputChangedEventArgs(
                JoinInputTokens(keyboardTokens, mouseTokens),
                isTransient));
    }

    private static string GetXButtonToken(uint mouseData)
    {
        return ((mouseData >> 16) & 0xFFFF) == XButtonForward
            ? "鼠标前进键"
            : "鼠标后退键";
    }

    private static short GetWheelDelta(uint mouseData) =>
        unchecked((short)((mouseData >> 16) & 0xFFFF));

    private static class NativeMethods
    {
        public delegate IntPtr LowLevelKeyboardProcedure(
            int code,
            IntPtr message,
            IntPtr dataPointer);

        public delegate IntPtr LowLevelMouseProcedure(
            int code,
            IntPtr message,
            IntPtr dataPointer);

        [StructLayout(LayoutKind.Sequential)]
        public struct LowLevelKeyboardData
        {
            public uint VirtualKey;
            public uint ScanCode;
            public uint Flags;
            public uint Time;
            public IntPtr ExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct NativePoint
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct LowLevelMouseData
        {
            public NativePoint Point;
            public uint MouseData;
            public uint Flags;
            public uint Time;
            public IntPtr ExtraInfo;
        }

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetWindowsHookEx(
            int hookIdentifier,
            LowLevelKeyboardProcedure procedure,
            IntPtr moduleHandle,
            uint threadId);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetWindowsHookEx(
            int hookIdentifier,
            LowLevelMouseProcedure procedure,
            IntPtr moduleHandle,
            uint threadId);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool UnhookWindowsHookEx(IntPtr hookHandle);

        [DllImport("user32.dll")]
        public static extern IntPtr CallNextHookEx(
            IntPtr hookHandle,
            int code,
            IntPtr message,
            IntPtr dataPointer);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr GetModuleHandle(string? moduleName);

        [DllImport("user32.dll")]
        public static extern short GetAsyncKeyState(int virtualKey);
    }
}
