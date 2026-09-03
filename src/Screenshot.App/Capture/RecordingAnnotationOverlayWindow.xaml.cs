using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Screenshot.App.Core;
using Screenshot.App.Editor;
using Screenshot.App.Infrastructure;
using DrawingRectangle = System.Drawing.Rectangle;
using WpfColor = System.Windows.Media.Color;
using WpfPoint = System.Windows.Point;
using WinForms = System.Windows.Forms;

namespace Screenshot.App.Capture;

internal enum RecordingAnnotationTool
{
    Pointer,
    Brush,
    Arrow,
    CurvedArrow,
    Rectangle,
    Ellipse,
    Emoji,
    Number,
    Text,
    Mosaic,
}

public partial class RecordingAnnotationOverlayWindow : Window
{
    private const int ExtendedWindowStyleIndex = -20;
    private const int NonClientHitTestMessage = 0x0084;
    private const int SetCursorMessage = 0x0020;
    private const int HitTestClient = 1;
    private const int HitTestTransparent = -1;
    private const long ExtendedStyleTransparent = 0x00000020L;
    private const long ExtendedStyleToolWindow = 0x00000080L;
    private const long ExtendedStyleNoActivate = 0x08000000L;
    private const int TopmostWindow = -1;
    private const uint DoNotActivate = 0x0010;
    private const uint DoNotChangeOwnerZOrder = 0x0200;
    private const uint FrameChanged = 0x0020;
    private const uint DoNotMove = 0x0002;
    private const uint DoNotResize = 0x0001;
    private const uint DoNotChangeZOrder = 0x0004;

    private readonly DrawingRectangle _windowBounds;
    private RecordingAnnotationTool _tool;
    private bool _isPaused;
    private HwndSource? _windowSource;

    public RecordingAnnotationOverlayWindow(ScreenRegion recordingRegion)
    {
        _windowBounds = new DrawingRectangle(
            recordingRegion.X,
            recordingRegion.Y,
            recordingRegion.Width,
            recordingRegion.Height);
        var dpi = MonitorGeometryService.GetDpiScale(_windowBounds);
        Width = _windowBounds.Width / dpi.X;
        Height = _windowBounds.Height / dpi.Y;
        Left = _windowBounds.X / dpi.X;
        Top = _windowBounds.Y / dpi.Y;
        InitializeComponent();
        DrawingCanvas.InitializeAnnotationOverlay(
            recordingRegion.Width,
            recordingRegion.Height,
            Width,
            Height);
        DrawingCanvas.AnnotationSelectionChanged += OnAnnotationSelectionChanged;
        SourceInitialized += OnSourceInitialized;
    }

    internal event EventHandler? AnnotationSelectionChanged;

    internal bool HasSelectedAnnotation => DrawingCanvas.HasSelectedAnnotation;

    internal WpfColor CurrentSelectedColor => DrawingCanvas.CurrentSelectedColor;

    internal double CurrentStrokeWidth => DrawingCanvas.CurrentStrokeWidth;

    internal double? SelectedAnnotationStrokeWidth =>
        DrawingCanvas.SelectedAnnotationStrokeWidth;

    internal IntPtr EnsureWindowHandle() =>
        new WindowInteropHelper(this).EnsureHandle();

    internal void SelectTool(RecordingAnnotationTool tool)
    {
        DrawingCanvas.CommitPendingText();
        _tool = tool;
        if (tool == RecordingAnnotationTool.Pointer)
        {
            DrawingCanvas.SetAnnotationCreationEnabled(false);
        }
        else
        {
            DrawingCanvas.SelectTool(ToEditorTool(tool));
            DrawingCanvas.SetAnnotationCreationEnabled(true);
        }

        UpdateInputTransparency();
    }

    internal void SetSelectedEmoji(string emoji) =>
        DrawingCanvas.SelectEmoji(emoji);

    internal void SetSelectedColor(WpfColor color) =>
        DrawingCanvas.SelectColor(color);

    internal void SetStrokeWidth(double strokeWidth) =>
        DrawingCanvas.SetStrokeWidth(strokeWidth);

    internal void SetArrowStyle(ArrowStyle arrowStyle) =>
        DrawingCanvas.SelectArrowStyle(arrowStyle);

    internal void DeleteSelectedAnnotation() =>
        _ = DrawingCanvas.DeleteSelectedAnnotation();

    public void Undo() => DrawingCanvas.Undo();

    public void Redo() => DrawingCanvas.Redo();

    public void Clear() => DrawingCanvas.ClearAnnotations();

    public void SetPaused(bool isPaused)
    {
        _isPaused = isPaused;
        if (isPaused)
        {
            _ = DrawingCanvas.CancelActiveOperation();
        }

        UpdateInputTransparency();
    }

    internal void EnsureTopmost()
    {
        if (!IsLoaded)
        {
            return;
        }

        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        _ = NativeMethods.SetWindowPos(
            handle,
            new IntPtr(TopmostWindow),
            0,
            0,
            0,
            0,
            DoNotMove | DoNotResize | DoNotActivate | DoNotChangeOwnerZOrder);
    }

    protected override void OnClosed(EventArgs e)
    {
        SourceInitialized -= OnSourceInitialized;
        DrawingCanvas.AnnotationSelectionChanged -= OnAnnotationSelectionChanged;
        DrawingCanvas.Reset();
        _windowSource?.RemoveHook(OnWindowMessage);
        _windowSource = null;
        base.OnClosed(e);
    }

    private void OnAnnotationSelectionChanged(object? sender, EventArgs e) =>
        AnnotationSelectionChanged?.Invoke(this, EventArgs.Empty);

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        _windowSource = HwndSource.FromHwnd(handle);
        _windowSource?.AddHook(OnWindowMessage);
        var style = NativeMethods.GetWindowLongPtr(
            handle,
            ExtendedWindowStyleIndex).ToInt64();
        _ = NativeMethods.SetWindowLongPtr(
            handle,
            ExtendedWindowStyleIndex,
            new IntPtr(
                style |
                ExtendedStyleTransparent |
                ExtendedStyleToolWindow |
                ExtendedStyleNoActivate));
        _ = NativeMethods.SetWindowPos(
            handle,
            new IntPtr(TopmostWindow),
            _windowBounds.X,
            _windowBounds.Y,
            _windowBounds.Width,
            _windowBounds.Height,
            DoNotActivate | DoNotChangeOwnerZOrder);
        UpdateInputTransparency();
    }

    private void UpdateInputTransparency()
    {
        var shouldPassThrough = _isPaused || _tool == RecordingAnnotationTool.Pointer;
        // Pointer mode displays annotations only. Make the complete WPF
        // window input-transparent as well as its HWND, so it cannot retain
        // an editor cursor after recording begins.
        IsHitTestVisible = !shouldPassThrough;
        DrawingCanvas.IsHitTestVisible = !shouldPassThrough;
        DrawingCanvas.Background = shouldPassThrough
            ? System.Windows.Media.Brushes.Transparent
            : new SolidColorBrush(WpfColor.FromArgb(1, 255, 255, 255));
        if (shouldPassThrough)
        {
            // A WPF canvas retains its previous cursor until the next input
            // hit test. Reset it before returning the complete overlay to
            // Windows, otherwise a former annotation can leave a Hand cursor
            // visible during recording.
            DrawingCanvas.Cursor = System.Windows.Input.Cursors.Arrow;
            Cursor = System.Windows.Input.Cursors.Arrow;
            System.Windows.Input.Mouse.UpdateCursor();
        }

        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var style = NativeMethods.GetWindowLongPtr(
            handle,
            ExtendedWindowStyleIndex).ToInt64();
        style = shouldPassThrough
            ? style | ExtendedStyleTransparent
            : style & ~ExtendedStyleTransparent;
        style = _tool == RecordingAnnotationTool.Text && !_isPaused
            ? style & ~ExtendedStyleNoActivate
            : style | ExtendedStyleNoActivate;
        _ = NativeMethods.SetWindowLongPtr(
            handle,
            ExtendedWindowStyleIndex,
            new IntPtr(style | ExtendedStyleToolWindow));
        _ = NativeMethods.SetWindowPos(
            handle,
            shouldPassThrough ? IntPtr.Zero : new IntPtr(TopmostWindow),
            0,
            0,
            0,
            0,
            DoNotMove |
            DoNotResize |
            (shouldPassThrough ? DoNotChangeZOrder : 0) |
            DoNotActivate |
            FrameChanged);
    }

    private IntPtr OnWindowMessage(
        IntPtr window,
        int message,
        IntPtr wordParameter,
        IntPtr longParameter,
        ref bool handled)
    {
        if (message == SetCursorMessage &&
            (_isPaused || _tool == RecordingAnnotationTool.Pointer))
        {
            SetCursor(WinForms.Cursors.Arrow.Handle);
            handled = true;
            return IntPtr.Zero;
        }

        if (message != NonClientHitTestMessage)
        {
            return IntPtr.Zero;
        }

        handled = true;
        return new IntPtr(
            _isPaused || _tool == RecordingAnnotationTool.Pointer
                ? HitTestTransparent
                : HitTestClient);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SetCursor(IntPtr cursor);

    private static EditorTool ToEditorTool(RecordingAnnotationTool tool) => tool switch
    {
        RecordingAnnotationTool.Ellipse => EditorTool.Ellipse,
        RecordingAnnotationTool.Arrow => EditorTool.Arrow,
        RecordingAnnotationTool.CurvedArrow => EditorTool.CurvedArrow,
        RecordingAnnotationTool.Emoji => EditorTool.Emoji,
        RecordingAnnotationTool.Number => EditorTool.Number,
        RecordingAnnotationTool.Brush => EditorTool.Brush,
        RecordingAnnotationTool.Mosaic => EditorTool.Mosaic,
        RecordingAnnotationTool.Text => EditorTool.Text,
        _ => EditorTool.Rectangle,
    };

    internal static Geometry CreateArrowGeometry(WpfPoint start, WpfPoint end)
    {
        var direction = end - start;
        if (direction.Length < 1)
        {
            return new LineGeometry(start, end);
        }

        direction.Normalize();
        var perpendicular = new Vector(-direction.Y, direction.X);
        var metrics = ArrowGeometryMetrics.For((end - start).Length, 3);
        var headLength = metrics.HeadLength;
        var headWidth = metrics.HeadHalfWidth;
        var headBase = end - (direction * headLength);
        var first = headBase + (perpendicular * headWidth);
        var second = headBase - (perpendicular * headWidth);
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(start, isFilled: false, isClosed: false);
            context.LineTo(end, isStroked: true, isSmoothJoin: false);
            context.BeginFigure(first, isFilled: false, isClosed: false);
            context.LineTo(end, isStroked: true, isSmoothJoin: false);
            context.LineTo(second, isStroked: true, isSmoothJoin: false);
        }
        geometry.Freeze();
        return geometry;
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetWindowPos(
            IntPtr window,
            IntPtr insertAfter,
            int x,
            int y,
            int width,
            int height,
            uint flags);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
        private static extern IntPtr GetWindowLongPtr64(IntPtr window, int index);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
        private static extern int GetWindowLong32(IntPtr window, int index);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
        private static extern IntPtr SetWindowLongPtr64(
            IntPtr window,
            int index,
            IntPtr value);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
        private static extern int SetWindowLong32(
            IntPtr window,
            int index,
            int value);

        public static IntPtr GetWindowLongPtr(IntPtr window, int index) =>
            IntPtr.Size == 8
                ? GetWindowLongPtr64(window, index)
                : new IntPtr(GetWindowLong32(window, index));

        public static IntPtr SetWindowLongPtr(
            IntPtr window,
            int index,
            IntPtr value) =>
            IntPtr.Size == 8
                ? SetWindowLongPtr64(window, index, value)
                : new IntPtr(SetWindowLong32(window, index, value.ToInt32()));
    }
}
