using System.Drawing;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Screenshot.App.Core;
using Screenshot.App.Infrastructure;

namespace Screenshot.App.Capture;

internal sealed record ResidentSelectionResult(ScreenRegion? Region, string? Color = null);

// The resident's first input surface contains no WPF visual tree or image.
// It stays usable while the feature process starts, and transfers the original
// pixels and final physical selection to the existing editor on mouse release.
internal sealed class ResidentSelectionWindow : NativeWindow, IDisposable
{
    private readonly NativeDesktopFrame _frame;
    private readonly CaptureAspectRatio _aspectRatio;
    private readonly Action _cancelled;
    private readonly bool _allowColorPicker;
    private readonly TaskCompletionSource<ResidentSelectionResult?> _selection = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Point _cursor, _anchor;
    private ScreenRegion _region;
    private bool _dragging, _moved, _finished, _disposed;
    private ResidentSelectionRenderer? _renderer;
    private CapturePickerKeyboardFallback? _keyboardFallback;
    internal bool IsCancelled { get; private set; }
    internal Task<ResidentSelectionResult?> Selection => _selection.Task;
    internal long PresentedTimestamp { get; private set; }

    internal ResidentSelectionWindow(NativeDesktopFrame frame, AppSettings settings, Action cancelled, bool allowColorPicker = true)
    {
        _frame = frame;
        _aspectRatio = settings.CaptureAspectRatio;
        _cancelled = cancelled;
        _allowColorPicker = allowColorPicker;
        try
        {
            var scale = MonitorGeometryService.GetDpiScale(new Rectangle(frame.Region.X, frame.Region.Y, frame.Region.Width, frame.Region.Height)).X;
            _renderer = new ResidentSelectionRenderer(frame, settings.Theme, scale);
            CreateHandle(new CreateParams { Caption = Presentation.UiLocalization.Get("SnapCut 快速框选"), X = frame.Region.X, Y = frame.Region.Y,
                Width = frame.Region.Width, Height = frame.Region.Height, Style = unchecked((int)0x80000000),
                ExStyle = 0x00000088 }); // WS_POPUP, TOOLWINDOW | TOPMOST
            // This HWND has no text input. Detach only its IME context, rather
            // than changing the user's input language or disabling IME on the
            // UI thread. C must reach WM_KEYDOWN even with Chinese IME enabled.
            _ = ImmAssociateContextEx(Handle, IntPtr.Zero, 0);
            OverlayShellBehavior.Apply(Handle);
            UpdatePointer();
        }
        catch { Dispose(); throw; }
    }

    internal async Task ShowAsync(CancellationToken token)
    {
        // The DIB was captured before this HWND existed. Preserve transient UI
        // pixels even when Windows changes focus/hover routing on activation.
        _ = ShowWindow(Handle, 5);
        _ = UpdateWindow(Handle);
        var activation = await CaptureInputActivation.AcquireAsync(Handle,
            () => CaptureInputReadiness.SurfaceReceivesPointer(Handle, _frame.Region), token);
        if (activation == CaptureInputActivation.Result.Interrupted) { Cancel(); return; }
        if (!await CaptureInputReadiness.WaitAsync(
            () => CaptureInputReadiness.SurfaceReceivesPointer(Handle, _frame.Region),
            () => CaptureInputReadiness.OwnsForeground(Handle), () => { }, token,
            recoverInput: () => CaptureInputActivation.RaiseSurface(Handle)))
        {
            var failure = CaptureInputReadiness.BlockedSurfaceFailure(Handle);
            CaptureStartupFailure.Report(failure);
            throw failure;
        }
        if (!CaptureInputReadiness.OwnsForeground(Handle))
            _keyboardFallback = new CapturePickerKeyboardFallback(Handle, () => _allowColorPicker && !_finished && !_dragging);
        PresentedTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
    }

    internal void Cancel()
    {
        if (_disposed || IsCancelled) return;
        IsCancelled = true;
        Hide();
        _selection.TrySetResult(null);
        _cancelled();
    }

    internal void Hide()
    {
        _keyboardFallback?.Dispose();
        _keyboardFallback = null;
        if (Handle != IntPtr.Zero)
            _ = SetWindowPos(Handle, IntPtr.Zero, 0, 0, 0, 0,
                0x0001 | 0x0002 | 0x0004 | 0x0010 | 0x0080 | 0x0200);
    }

    protected override void WndProc(ref Message message)
    {
        try
        {
            switch (message.Msg)
            {
                case 0x14: message.Result = new IntPtr(1); return; // no empty background frame
                case 0x20: _ = SetCursor(LoadCursorW(IntPtr.Zero, new IntPtr(32515))); message.Result = new IntPtr(1); return;
                case 0x0F: Paint(); message.Result = IntPtr.Zero; return;
                case 0x200:
                    if (!_finished) { UpdatePointer(); InvalidateVisual(); }
                    return;
                case 0x201:
                    if (!_finished)
                    {
                        UpdatePointer(); _anchor = _cursor; _dragging = true; _moved = false;
                        CaptureTimingDiagnostics.Mark("resident-selection-mouse-down", $"window={Handle.ToInt64()}");
                        _ = SetCapture(Handle);
                        InvalidateVisual();
                    }
                    return;
                case 0x202:
                    if (!_finished && _dragging)
                    {
                        CaptureTimingDiagnostics.Mark("resident-selection-mouse-up", $"window={Handle.ToInt64()} width={_region.Width} height={_region.Height}");
                        UpdatePointer(); _dragging = false; _ = ReleaseCapture();
                        if (!_region.IsEmpty)
                        {
                            _finished = true;
                            _selection.TrySetResult(new(_region));
                        }
                        InvalidateVisual();
                    }
                    return;
                case 0x204: return; // Consume both halves; do not leak the release into the desktop.
                case 0x205: Cancel(); return;
                case 0x100: case 0x104: case CapturePickerKeyboardFallback.ForwardedKeyMessage:
                    var key = message.WParam.ToInt32();
                    if (key == 0xE5) key = (int)ImmGetVirtualKey(Handle); // VK_PROCESSKEY from a queued IME key
                    if (key == 0x1B) { Cancel(); return; }
                    if (_allowColorPicker && !_finished && !_dragging && key == 0x43)
                    {
                        UpdatePointer(); _finished = true;
                        _selection.TrySetResult(new(null, "#" + _frame.PixelAt(_cursor.X, _cursor.Y).ToString("X6", CultureInfo.InvariantCulture)));
                        return;
                    }
                    break;
                case 0x102: case 0x106: return; // No text entry on the selection surface.
                case 0x7E: Cancel(); return; // topology changed: never reuse old coordinates
                case 0x10: Cancel(); return;
            }
            base.WndProc(ref message);
        }
        catch (Exception exception)
        {
            Hide();
            _selection.TrySetException(exception);
            IsCancelled = true;
            _cancelled();
        }
    }

    private void UpdatePointer()
    {
        _ = GetCursorPos(out _cursor);
        if (_dragging && (_moved || Math.Abs(_cursor.X - _anchor.X) >= 3 || Math.Abs(_cursor.Y - _anchor.Y) >= 3))
        {
            _moved = true;
            _region = GetSelection(_anchor, _cursor, _frame.Region, _aspectRatio);
        }
        else if (!_dragging)
            _region = WindowSnapService.TryGetWindowRegionAt(_cursor.X, _cursor.Y, Handle, _frame.Region, out var region)
                ? region : default;
    }

    internal static ScreenRegion GetSelection(Point start, Point end, ScreenRegion surface, CaptureAspectRatio aspect)
    {
        var bounds = new Rectangle(surface.X, surface.Y, surface.Width, surface.Height);
        if (CaptureAspectRatioHelper.GetValue(aspect) is { } ratio)
        {
            var constrained = CaptureAspectRatioHelper.ConstrainFromAnchor(start, end, bounds, ratio);
            return new(constrained.X, constrained.Y, constrained.Width, constrained.Height);
        }
        return ScreenRegion.Intersect(ScreenRegion.FromCorners(start.X, start.Y, end.X, end.Y), surface);
    }

    private ResidentSelectionVisual Visual => new(_region, _cursor, Screen.FromPoint(_cursor).Bounds,
        _dragging && _moved || _finished, !_allowColorPicker || _dragging || _finished);

    private void InvalidateVisual()
    {
        var damage = _renderer!.GetDamage(Visual);
        if (damage.IsEmpty) return;
        var rectangle = new NativeRect(damage.Left, damage.Top, damage.Right, damage.Bottom);
        _ = InvalidateRect(Handle, ref rectangle, false);
    }

    private void Paint()
    {
        var dc = BeginPaint(Handle, out var paint);
        try
        {
            var renderer = _renderer;
            if (renderer is null) return;
            renderer.Render(Visual);
            var rectangle = paint.Rect;
            if (rectangle.Right <= rectangle.Left || rectangle.Bottom <= rectangle.Top) return;
            if (!NativeDesktopFrame.BitBlt(dc, rectangle.Left, rectangle.Top,
                rectangle.Right - rectangle.Left, rectangle.Bottom - rectangle.Top,
                renderer.Buffer.DeviceContext, rectangle.Left, rectangle.Top, 0x00CC0020))
                throw new System.ComponentModel.Win32Exception("无法显示截图框选画面。");
        }
        finally { _ = EndPaint(Handle, ref paint); }
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _selection.TrySetResult(null);
        if (Handle != IntPtr.Zero)
        {
            if (GetCapture() == Handle) _ = ReleaseCapture();
            Hide(); // Do not reactivate/reorder the editor's owned mask on teardown.
            DestroyHandle();
        }
        _renderer?.Dispose(); _renderer = null;
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativeRect(int left, int top, int right, int bottom)
    { public int Left = left, Top = top, Right = right, Bottom = bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct PaintStruct
    {
        public IntPtr Dc; public int Erase; public NativeRect Rect; public int Restore, Update;
        private long _reserved0, _reserved1, _reserved2, _reserved3;
    }
    [DllImport("user32.dll")] private static extern IntPtr BeginPaint(IntPtr window, out PaintStruct paint);
    [DllImport("user32.dll")] private static extern bool EndPaint(IntPtr window, ref PaintStruct paint);
    [DllImport("user32.dll")] private static extern bool InvalidateRect(IntPtr window, ref NativeRect rect, bool erase);
    [DllImport("user32.dll")] private static extern bool UpdateWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr SetCapture(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr GetCapture();
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern IntPtr SetCursor(IntPtr cursor);
    [DllImport("user32.dll")] private static extern IntPtr LoadCursorW(IntPtr instance, IntPtr name);
    [DllImport("imm32.dll")] private static extern bool ImmAssociateContextEx(IntPtr window, IntPtr context, uint flags);
    [DllImport("imm32.dll")] private static extern uint ImmGetVirtualKey(IntPtr window);
}
