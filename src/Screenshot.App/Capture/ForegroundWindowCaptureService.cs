using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Screenshot.App.Capture;

public static class ForegroundWindowCaptureService
{
    public static IntPtr GetForegroundWindowHandle()
    {
        return NativeMethods.GetForegroundWindow();
    }

    public static bool TryGetClientScreenRegion(IntPtr windowHandle, out ScreenRegion region)
    {
        region = default;

        if (windowHandle == IntPtr.Zero ||
            !NativeMethods.GetClientRect(windowHandle, out var clientRect))
        {
            return false;
        }

        var topLeft = new NativePoint
        {
            X = clientRect.Left,
            Y = clientRect.Top,
        };
        var bottomRight = new NativePoint
        {
            X = clientRect.Right,
            Y = clientRect.Bottom,
        };

        if (!NativeMethods.ClientToScreen(windowHandle, ref topLeft) ||
            !NativeMethods.ClientToScreen(windowHandle, ref bottomRight))
        {
            return false;
        }

        region = ScreenRegion.FromCorners(
            topLeft.X,
            topLeft.Y,
            bottomRight.X,
            bottomRight.Y);
        return !region.IsEmpty;
    }

    public static bool TryCreateScrollCaptureTarget(
        IntPtr windowHandle,
        out ScrollCaptureTarget? target)
    {
        target = null;

        if (windowHandle == IntPtr.Zero ||
            !TryGetClientScreenRegion(windowHandle, out var rootClientRegion))
        {
            return false;
        }

        var scrollTargetHandle = FindScrollTargetHandle(windowHandle, rootClientRegion);
        var captureRegion = rootClientRegion;

        if (scrollTargetHandle != windowHandle &&
            TryGetClientScreenRegion(scrollTargetHandle, out var childClientRegion))
        {
            captureRegion = childClientRegion;
        }

        target = new ScrollCaptureTarget(
            windowHandle,
            scrollTargetHandle,
            captureRegion,
            SupportsVerticalScroll(scrollTargetHandle));
        return true;
    }

    /// <summary>
    /// Creates a scroll-capture target for a user selection. The capture region is
    /// always the selection itself (WeChat-style). The window under the selection
    /// center is preferred for focus/scroll discovery; <paramref name="windowHandle"/>
    /// is only a fallback when the point cannot be resolved.
    /// </summary>
    public static bool TryCreateScrollCaptureTarget(
        IntPtr windowHandle,
        ScreenRegion requestedCaptureRegion,
        out ScrollCaptureTarget? target)
    {
        target = null;

        if (requestedCaptureRegion.IsEmpty ||
            requestedCaptureRegion.Width < 32 ||
            requestedCaptureRegion.Height < 64)
        {
            return false;
        }

        // Sampling always follows the user selection. Never shrink to a stale
        // foreground client rectangle — that mismatched the first frame and
        // made later stitch frames drop silently.
        var captureRegion = requestedCaptureRegion;
        var rootWindowHandle = ResolveRootWindowHandle(
            windowHandle,
            captureRegion);

        if (rootWindowHandle == IntPtr.Zero)
        {
            return false;
        }

        var scrollTargetHandle = FindScrollTargetHandle(
            rootWindowHandle,
            captureRegion,
            captureRegion);
        target = new ScrollCaptureTarget(
            rootWindowHandle,
            scrollTargetHandle,
            captureRegion,
            SupportsVerticalScroll(scrollTargetHandle));
        return true;
    }

    /// <summary>
    /// Resolves the scroll target exclusively from the pixels under the selection
    /// (no hotkey-time foreground window required).
    /// </summary>
    public static bool TryCreateScrollCaptureTargetFromSelection(
        ScreenRegion selectionRegion,
        out ScrollCaptureTarget? target)
    {
        return TryCreateScrollCaptureTarget(
            IntPtr.Zero,
            selectionRegion,
            out target);
    }

    public static IntPtr ResolveRootWindowHandle(
        IntPtr preferredWindowHandle,
        ScreenRegion region)
    {
        var underPoint = GetWindowHandleUnderRegionCenter(region);

        if (underPoint != IntPtr.Zero)
        {
            var rootFromPoint = GetRootWindowHandle(underPoint);
            if (rootFromPoint != IntPtr.Zero)
            {
                return rootFromPoint;
            }
        }

        if (preferredWindowHandle != IntPtr.Zero &&
            NativeMethods.IsWindow(preferredWindowHandle))
        {
            var rootPreferred = GetRootWindowHandle(preferredWindowHandle);
            return rootPreferred != IntPtr.Zero
                ? rootPreferred
                : preferredWindowHandle;
        }

        return IntPtr.Zero;
    }

    public static IntPtr GetWindowHandleUnderRegionCenter(ScreenRegion region)
    {
        if (region.IsEmpty)
        {
            return IntPtr.Zero;
        }

        var screenPoint = new NativePoint
        {
            X = region.X + (region.Width / 2),
            Y = region.Y + (region.Height / 2),
        };
        return NativeMethods.WindowFromPoint(screenPoint);
    }

    public static IntPtr GetRootWindowHandle(IntPtr windowHandle)
    {
        if (windowHandle == IntPtr.Zero || !NativeMethods.IsWindow(windowHandle))
        {
            return IntPtr.Zero;
        }

        var root = NativeMethods.GetAncestor(windowHandle, NativeMethods.GetAncestorRoot);
        return root != IntPtr.Zero ? root : windowHandle;
    }

    public static Bitmap CaptureRegion(ScreenRegion region)
    {
        var bitmap = new Bitmap(region.Width, region.Height, PixelFormat.Format32bppPArgb);

        try
        {
            // A BitBlt that races the compositor can span a refresh and return
            // a frame whose upper and lower halves sit at different scroll
            // positions. Such a torn frame matches nothing: the band-agreement
            // rule rejects every candidate, and during a fast scroll a run of
            // torn frames is what lets the viewport escape the matchable
            // range. Waiting for the composition pass aligns the copy with a
            // stable desktop image; it also naturally paces sampling to the
            // refresh rate, which is as fast as new content can appear anyway.
            _ = NativeMethods.DwmFlush();
            using var graphics = Graphics.FromImage(bitmap);
            graphics.CopyFromScreen(region.X, region.Y, 0, 0, bitmap.Size);
            return bitmap;
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }

    public static bool TryFocusScrollTarget(ScrollCaptureTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);

        if (!NativeMethods.IsWindow(target.WindowHandle) ||
            !NativeMethods.IsWindow(target.ScrollTargetHandle))
        {
            return false;
        }

        // The live preview is a topmost, non-activating window, but a render
        // pass or an incidental click can still leave it as the foreground
        // window.  Re-focus the actual scroll child before the next wheel
        // message; otherwise the global hook records the wheel while the
        // editor remains stationary and the stitcher cannot cross its edge.
        if (NativeMethods.GetForegroundWindow() == target.WindowHandle)
        {
            return NativeMethods.SetFocus(target.ScrollTargetHandle) !=
                   IntPtr.Zero;
        }

        var attached = FocusScrollTarget(
            target,
            out var currentThreadId,
            out var targetThreadId);

        try
        {
            return NativeMethods.GetForegroundWindow() == target.WindowHandle;
        }
        finally
        {
            if (attached)
            {
                _ = NativeMethods.AttachThreadInput(
                    currentThreadId,
                    targetThreadId,
                    attach: false);
            }
        }
    }

    public static bool Scroll(ScrollCaptureTarget target, int delta)
    {
        if (!CanScroll(target, delta))
        {
            return false;
        }

        if (target.SupportsVerticalScroll &&
            NativeMethods.PostMessage(
                target.ScrollTargetHandle,
                NativeMethods.WindowMessageVerticalScroll,
                new IntPtr(GetVerticalScrollCommand(delta)),
                IntPtr.Zero))
        {
            return true;
        }

        return ScrollWithWheelInput(target, delta);
    }

    /// <summary>
    /// Sends exactly one conventional wheel step even when the target exposes
    /// a native vertical scrollbar.
    /// </summary>
    public static bool ScrollWithWheelInput(ScrollCaptureTarget target, int delta)
    {
        return ScrollWithWheelInputCore(
            target,
            NormalizeWheelDelta(delta));
    }

    /// <summary>
    /// Injects one conventional wheel packet at the user's current point when
    /// it lies inside the viewport. An outside pointer is routed through the
    /// viewport only until Windows consumes that packet, then restored unless
    /// the user moved it in the meantime.
    /// </summary>
    public static bool ScrollWithWheelMessage(
        ScrollCaptureTarget target,
        int delta)
    {
        if (!CanScroll(target, delta))
        {
            return false;
        }

        var normalizedDelta = Math.Clamp(delta, short.MinValue, short.MaxValue);
        var attached = FocusScrollTarget(
            target,
            out var currentThreadId,
            out var targetThreadId);
        try
        {
            if (!NativeMethods.GetCursorPos(out var cursorPosition))
            {
                return false;
            }

            var routeThroughViewport = !target.CaptureRegion.Contains(
                cursorPosition.X,
                cursorPosition.Y);
            var routeX = target.CaptureRegion.X +
                (target.CaptureRegion.Width / 2);
            var routeY = target.CaptureRegion.Y +
                (target.CaptureRegion.Height / 2);
            if (routeThroughViewport &&
                !NativeMethods.SetCursorPos(routeX, routeY))
            {
                return false;
            }

            if (routeThroughViewport)
            {
                Thread.Sleep(1);
            }

            var wheelInput = new NativeInput
            {
                Type = NativeMethods.InputMouse,
                Data = new NativeInputUnion
                {
                    Mouse = new NativeMouseInput
                    {
                        MouseData = unchecked((uint)normalizedDelta),
                        Flags = NativeMethods.MouseEventWheel,
                    },
                },
            };
            var wheelSent = NativeMethods.SendInput(
                1,
                [wheelInput],
                Marshal.SizeOf<NativeInput>()) == 1;

            if (routeThroughViewport)
            {
                Thread.Sleep(1);
                // Restore only when the user did not move while the packet was
                // routed. This preserves an intentional pointer move during a
                // live automatic capture instead of fighting it every tick.
                if (!NativeMethods.GetCursorPos(out var positionAfterWheel) ||
                    (positionAfterWheel.X == routeX &&
                     positionAfterWheel.Y == routeY))
                {
                    _ = NativeMethods.SetCursorPos(
                        cursorPosition.X,
                        cursorPosition.Y);
                }
            }

            return wheelSent;
        }
        finally
        {
            if (attached)
            {
                _ = NativeMethods.AttachThreadInput(
                    currentThreadId,
                    targetThreadId,
                    attach: false);
            }
        }
    }

    private static bool ScrollWithWheelInputCore(
        ScrollCaptureTarget target,
        int wheelDelta)
    {
        if (!CanScroll(target, wheelDelta))
        {
            return false;
        }

        // Framework-hosted scroll viewers (including WPF and Chromium) often accept a
        // posted WM_MOUSEWHEEL but do not actually change their content. SendInput
        // produces the same real pointer input as a user wheel action.
        var attached = FocusScrollTarget(
            target,
            out var currentThreadId,
            out var targetThreadId);

        try
        {
            var centerPoint = new NativePoint
            {
                X = target.CaptureRegion.X + (target.CaptureRegion.Width / 2),
                Y = target.CaptureRegion.Y + (target.CaptureRegion.Height / 2),
            };
            var virtualScreen = VirtualScreen.GetBounds();
            var normalizedX = NormalizeAbsolutePointerCoordinate(
                centerPoint.X,
                virtualScreen.X,
                virtualScreen.Width);
            var normalizedY = NormalizeAbsolutePointerCoordinate(
                centerPoint.Y,
                virtualScreen.Y,
                virtualScreen.Height);
            var pointerInput = new NativeInput
            {
                Type = NativeMethods.InputMouse,
                Data = new NativeInputUnion
                {
                    Mouse = new NativeMouseInput
                    {
                        DeltaX = normalizedX,
                        DeltaY = normalizedY,
                        Flags = NativeMethods.MouseEventMove |
                                NativeMethods.MouseEventAbsolute |
                                NativeMethods.MouseEventVirtualDesktop,
                    },
                },
            };
            var wheelInput = new NativeInput
            {
                Type = NativeMethods.InputMouse,
                Data = new NativeInputUnion
                {
                    Mouse = new NativeMouseInput
                    {
                        MouseData = unchecked((uint)wheelDelta),
                        Flags = NativeMethods.MouseEventWheel,
                    },
                },
            };

            return NativeMethods.SendInput(
                2,
                [pointerInput, wheelInput],
                Marshal.SizeOf<NativeInput>()) == 2;
        }
        finally
        {
            if (attached)
            {
                _ = NativeMethods.AttachThreadInput(
                    currentThreadId,
                    targetThreadId,
                    attach: false);
            }
        }
    }

    public static bool ScrollWithWindowMessage(ScrollCaptureTarget target, int delta)
    {
        if (!CanScroll(target, delta))
        {
            return false;
        }

        if (target.SupportsVerticalScroll)
        {
            return NativeMethods.PostMessage(
                target.ScrollTargetHandle,
                NativeMethods.WindowMessageVerticalScroll,
                new IntPtr(GetVerticalScrollCommand(delta)),
                IntPtr.Zero);
        }

        var virtualKey = delta < 0
            ? NativeMethods.VirtualKeyPageDown
            : NativeMethods.VirtualKeyPageUp;
        var keyDownSent = NativeMethods.SendMessageTimeout(
                target.ScrollTargetHandle,
                NativeMethods.WindowMessageKeyDown,
                new IntPtr(virtualKey),
                IntPtr.Zero,
                NativeMethods.SendMessageAbortIfHung,
                500,
                out _) != IntPtr.Zero;
        var keyUpSent = NativeMethods.SendMessageTimeout(
                target.ScrollTargetHandle,
                NativeMethods.WindowMessageKeyUp,
                new IntPtr(virtualKey),
                IntPtr.Zero,
                NativeMethods.SendMessageAbortIfHung,
                500,
                out _) != IntPtr.Zero;
        return keyDownSent && keyUpSent;
    }

    public static bool TryGetCursorPosition(out ScreenPoint position)
    {
        position = default;

        if (!NativeMethods.GetCursorPos(out var nativePoint))
        {
            return false;
        }

        position = new ScreenPoint(nativePoint.X, nativePoint.Y);
        return true;
    }

    public static void RestoreCursorPosition(ScreenPoint position)
    {
        _ = NativeMethods.SetCursorPos(position.X, position.Y);
    }

    private static IntPtr FindScrollTargetHandle(
        IntPtr rootWindowHandle,
        ScreenRegion rootClientRegion)
    {
        return FindScrollTargetHandle(
            rootWindowHandle,
            rootClientRegion,
            rootClientRegion);
    }

    private static IntPtr FindScrollTargetHandle(
        IntPtr rootWindowHandle,
        ScreenRegion searchRegion,
        ScreenRegion viewportReferenceRegion)
    {
        var screenPoint = new NativePoint
        {
            X = searchRegion.X + (searchRegion.Width / 2),
            Y = searchRegion.Y + (searchRegion.Height / 2),
        };
        var candidate = NativeMethods.WindowFromPoint(screenPoint);
        IntPtr fallbackCandidate = IntPtr.Zero;

        while (candidate != IntPtr.Zero && candidate != rootWindowHandle)
        {
            if (NativeMethods.GetAncestor(candidate, NativeMethods.GetAncestorRoot) ==
                    rootWindowHandle &&
                TryGetClientScreenRegion(candidate, out var candidateRegion) &&
                IsSubstantialViewport(candidateRegion, viewportReferenceRegion))
            {
                if (SupportsVerticalScroll(candidate))
                {
                    return candidate;
                }

                if (fallbackCandidate == IntPtr.Zero)
                {
                    fallbackCandidate = candidate;
                }
            }

            candidate = NativeMethods.GetParent(candidate);
        }

        return fallbackCandidate != IntPtr.Zero
            ? fallbackCandidate
            : rootWindowHandle;
    }

    private static bool IsSubstantialViewport(
        ScreenRegion candidateRegion,
        ScreenRegion rootClientRegion)
    {
        return candidateRegion.Width >= rootClientRegion.Width / 2 &&
               candidateRegion.Height >= rootClientRegion.Height / 2;
    }

    private static bool SupportsVerticalScroll(IntPtr windowHandle)
    {
        var style = NativeMethods.GetWindowLongPtr(
            windowHandle,
            NativeMethods.WindowStyleIndex);
        return (style.ToInt64() & NativeMethods.VerticalScrollStyle) != 0;
    }

    private static int GetVerticalScrollCommand(int delta)
    {
        return delta < 0
            ? NativeMethods.VerticalScrollPageDown
            : NativeMethods.VerticalScrollPageUp;
    }

    private static bool CanScroll(ScrollCaptureTarget target, int delta)
    {
        ArgumentNullException.ThrowIfNull(target);
        return target.ScrollTargetHandle != IntPtr.Zero &&
               NativeMethods.IsWindow(target.ScrollTargetHandle) &&
               !target.CaptureRegion.IsEmpty &&
               delta != 0;
    }

    private static int NormalizeWheelDelta(int delta)
    {
        const int wheelStep = 120;
        const int maximumWheelSteps = 2;
        var magnitude = Math.Clamp(
            Math.Abs((long)delta),
            wheelStep,
            wheelStep * maximumWheelSteps);
        return Math.Sign(delta) * (int)magnitude;
    }

    private static int NormalizeAbsolutePointerCoordinate(
        int coordinate,
        int virtualOrigin,
        int virtualLength)
    {
        if (virtualLength <= 1)
        {
            return 0;
        }

        var offset = Math.Clamp(coordinate - virtualOrigin, 0, virtualLength - 1);
        return (int)Math.Round(offset * 65535d / (virtualLength - 1));
    }

    private static NativeInput CreateAbsolutePointerInput(
        int x,
        int y,
        ScreenRegion virtualScreen)
    {
        return new NativeInput
        {
            Type = NativeMethods.InputMouse,
            Data = new NativeInputUnion
            {
                Mouse = new NativeMouseInput
                {
                    DeltaX = NormalizeAbsolutePointerCoordinate(
                        x,
                        virtualScreen.X,
                        virtualScreen.Width),
                    DeltaY = NormalizeAbsolutePointerCoordinate(
                        y,
                        virtualScreen.Y,
                        virtualScreen.Height),
                    Flags = NativeMethods.MouseEventMove |
                            NativeMethods.MouseEventAbsolute |
                            NativeMethods.MouseEventVirtualDesktop,
                },
            },
        };
    }

    private static bool FocusScrollTarget(
        ScrollCaptureTarget target,
        out uint currentThreadId,
        out uint targetThreadId)
    {
        currentThreadId = NativeMethods.GetCurrentThreadId();
        targetThreadId = NativeMethods.GetWindowThreadProcessId(
            target.ScrollTargetHandle,
            out _);
        var attached = currentThreadId != targetThreadId &&
                       targetThreadId != 0 &&
                       NativeMethods.AttachThreadInput(
                           currentThreadId,
                           targetThreadId,
                           attach: true);

        _ = NativeMethods.BringWindowToTop(target.WindowHandle);
        _ = NativeMethods.SetForegroundWindow(target.WindowHandle);
        _ = NativeMethods.SetFocus(target.ScrollTargetHandle);
        return attached;
    }

    private static class NativeMethods
    {
        public const uint WindowMessageVerticalScroll = 0x0115;
        public const uint WindowMessageKeyDown = 0x0100;
        public const uint WindowMessageKeyUp = 0x0101;
        public const uint SendMessageAbortIfHung = 0x0002;
        public const uint GetAncestorRoot = 2;
        public const int WindowStyleIndex = -16;
        public const long VerticalScrollStyle = 0x00200000L;
        public const int VerticalScrollPageUp = 2;
        public const int VerticalScrollPageDown = 3;
        public const int VirtualKeyPageUp = 0x21;
        public const int VirtualKeyPageDown = 0x22;
        public const uint InputMouse = 0;
        public const uint MouseEventMove = 0x0001;
        public const uint MouseEventWheel = 0x0800;
        public const uint MouseEventVirtualDesktop = 0x4000;
        public const uint MouseEventAbsolute = 0x8000;

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("dwmapi.dll")]
        public static extern int DwmFlush();

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetClientRect(IntPtr windowHandle, out NativeRect rectangle);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ClientToScreen(IntPtr windowHandle, ref NativePoint point);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool PostMessage(
            IntPtr windowHandle,
            uint message,
            IntPtr wParam,
            IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SendMessageTimeout(
            IntPtr windowHandle,
            uint message,
            IntPtr wParam,
            IntPtr lParam,
            uint flags,
            uint timeoutMilliseconds,
            out IntPtr result);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetForegroundWindow(IntPtr windowHandle);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool BringWindowToTop(IntPtr windowHandle);

        [DllImport("user32.dll")]
        public static extern IntPtr SetFocus(IntPtr windowHandle);

        [DllImport("kernel32.dll")]
        public static extern uint GetCurrentThreadId();

        [DllImport("user32.dll", SetLastError = true)]
        public static extern uint GetWindowThreadProcessId(
            IntPtr windowHandle,
            out uint processId);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool AttachThreadInput(
            uint sourceThreadId,
            uint targetThreadId,
            [MarshalAs(UnmanagedType.Bool)] bool attach);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetCursorPos(out NativePoint point);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetCursorPos(int x, int y);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern uint SendInput(
            uint inputCount,
            [In] NativeInput[] inputs,
            int size);

        [DllImport("user32.dll")]
        public static extern IntPtr WindowFromPoint(NativePoint point);

        [DllImport("user32.dll")]
        public static extern IntPtr GetParent(IntPtr windowHandle);

        [DllImport("user32.dll")]
        public static extern IntPtr GetAncestor(IntPtr windowHandle, uint flags);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsWindow(IntPtr windowHandle);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
        public static extern IntPtr GetWindowLongPtr(IntPtr windowHandle, int index);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeInput
    {
        public uint Type;
        public NativeInputUnion Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct NativeInputUnion
    {
        [FieldOffset(0)]
        public NativeMouseInput Mouse;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMouseInput
    {
        public int DeltaX;
        public int DeltaY;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }
}

public readonly record struct ScreenPoint(int X, int Y);
