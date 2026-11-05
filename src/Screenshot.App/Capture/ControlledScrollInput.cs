using System.Runtime.InteropServices;

namespace Screenshot.App.Capture;

// Automatic only. Focus changes are needed when switching windows, not on every
// wheel packet. Real wheel input is required by WPF and Chromium hit testing.
internal static class ControlledScrollInput
{
    internal static bool Send(ScrollCaptureTarget target, int delta)
    {
        if (delta == 0 || !IsWindow(target.ScrollTargetHandle) || target.CaptureRegion.IsEmpty) return false;
        var inputRegion = GetRoutingRegion(target);
        var x = inputRegion.X + inputRegion.Width / 2;
        var y = inputRegion.Y + inputRegion.Height / 2;
        if (GetForegroundWindow() != target.WindowHandle)
            _ = ForegroundWindowCaptureService.TryFocusScrollTarget(target);
        if (!GetCursorPos(out var original)) return false;
        var route = !inputRegion.Contains(original.X, original.Y);
        if (route && !SetCursorPos(x, y)) return false;
        try
        {
            if (route) Thread.Sleep(1);
            var input = new Input
            {
                Type = 0,
                Mouse = new MouseInput { MouseData = unchecked((uint)delta), Flags = 0x0800,
                    ExtraInfo = new IntPtr(ForegroundWindowCaptureService.ControlledWheelInputSignature) },
            };
            return SendInput(1, [input], Marshal.SizeOf<Input>()) == 1;
        }
        finally
        {
            if (route)
            {
                Thread.Sleep(1);
                if (GetCursorPos(out var current) && current.X == x && current.Y == y)
                    _ = SetCursorPos(original.X, original.Y);
            }
        }
    }

    internal static ScreenRegion GetRoutingRegion(ScrollCaptureTarget target)
    {
        var body = target.ScrollInputRegionProvider?.Invoke() ?? target.CaptureRegion;
        body = ScreenRegion.Intersect(body, target.CaptureRegion);
        if (body.IsEmpty) body = target.CaptureRegion;
        // Before the first movement reveals fixed bars, use the middle of the
        // selection. Merely being inside the selection is insufficient: wheel
        // input over a chat title or composer never reaches its message list.
        var horizontalInset = body.Width / 8;
        var verticalInset = body.Height / 4;
        return new ScreenRegion(body.X + horizontalInset, body.Y + verticalInset,
            body.Width - horizontalInset * 2, body.Height - verticalInset * 2);
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCursorPos(int x, int y);
    [StructLayout(LayoutKind.Sequential)]
    private struct Point { public int X; public int Y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct Input { public uint Type; public MouseInput Mouse; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int X;
        public int Y;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }
}
