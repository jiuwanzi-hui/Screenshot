using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Screenshot.App.Capture;
using Screenshot.App.Core;
using Screenshot.App.Infrastructure;
using DrawingPoint = System.Drawing.Point;

namespace Screenshot.App.Editor;

/// <summary>
/// Lightweight color-keyed WinForms preview used while creating a drawing
/// annotation. It samples the native cursor during the drag and paints only
/// the current shape, leaving the WPF editor tree untouched until commit.
/// </summary>
internal sealed class NativeDrawingPreviewWindow : Form
{
    private const int FramePadding = 8;
    private const int TopmostWindow = -1;
    private const uint DoNotActivate = 0x0010;
    private const uint DoNotChangeOwnerZOrder = 0x0200;
    private const uint AsynchronousWindowPosition = 0x4000;
    private const int ShowNormal = 5;
    private const int HideCommand = 0;

    private readonly object _sync = new();
    private readonly TimeSpan _interactionFrameInterval;
    private bool _disposed;
    private CancellationTokenSource? _trackingCancellation;
    private Thread? _trackingThread;
    private EditorTool _tool;
    private DrawingPoint _start;
    private DrawingPoint _current;
    private readonly List<DrawingPoint> _points = [];
    private int _pathLeft;
    private int _pathTop;
    private int _pathRight;
    private int _pathBottom;
    private Color _color = Color.Red;
    private float _strokeWidth = 3;
    private ArrowStyle _arrowStyle = ArrowStyle.Filled;

    public event EventHandler? Painted;

    public NativeDrawingPreviewWindow()
    {
        var virtualBounds = VirtualScreen.GetBounds();
        _interactionFrameInterval =
            DisplayRefreshRateService.GetInteractionFrameInterval(
                new Rectangle(
                    virtualBounds.X,
                    virtualBounds.Y,
                    virtualBounds.Width,
                    virtualBounds.Height));
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        ShowIcon = false;
        TopMost = true;
        ControlBox = false;
        BackColor = Color.Magenta;
        TransparencyKey = Color.Magenta;
        Width = 1;
        Height = 1;
        SetStyle(
            ControlStyles.UserPaint |
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer,
            true);
        CreateControl();
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.ExStyle |= 0x00000020 | 0x00000080 | 0x08000000;
            return parameters;
        }
    }

    public void Start(
        EditorTool tool,
        DrawingPoint start,
        Color color,
        float strokeWidth,
        ArrowStyle arrowStyle)
    {
        Stop();
        if (_disposed || !IsHandleCreated)
        {
            return;
        }

        lock (_sync)
        {
            _tool = tool;
            _start = start;
            _current = start;
            _points.Clear();
            _points.Add(start);
            _pathLeft = _pathRight = start.X;
            _pathTop = _pathBottom = start.Y;
            _color = Color.FromArgb(color.R, color.G, color.B);
            _strokeWidth = Math.Max(1, strokeWidth);
            _arrowStyle = arrowStyle;
        }

        var cancellation = new CancellationTokenSource();
        _trackingCancellation = cancellation;
        _trackingThread = new Thread(() =>
        {
            _ = timeBeginPeriod(1);
            try
            {
                var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                var last = DrawingPoint.Empty;
                while (!cancellation.IsCancellationRequested && !_disposed)
                {
                    if (stopwatch.Elapsed >= _interactionFrameInterval &&
                        GetCursorPos(out var cursor))
                    {
                        stopwatch.Restart();
                        if (cursor.X != last.X || cursor.Y != last.Y)
                        {
                            last = cursor;
                            UpdateCursor(cursor);
                        }
                    }

                    var remaining = _interactionFrameInterval - stopwatch.Elapsed;
                    Thread.Sleep(remaining > TimeSpan.Zero
                        ? remaining
                        : TimeSpan.FromMilliseconds(1));
                }
            }
            finally
            {
                _ = timeEndPeriod(1);
            }
        })
        {
            IsBackground = true,
            Name = "SnapCut native drawing preview",
            Priority = ThreadPriority.BelowNormal,
        };
        _trackingThread.Start();
        UpdateCursor(start);
    }

    public void Stop()
    {
        var cancellation = _trackingCancellation;
        var trackingThread = _trackingThread;
        _trackingThread = null;
        _trackingCancellation = null;
        cancellation?.Cancel();
        if (trackingThread is not null &&
            trackingThread != Thread.CurrentThread &&
            trackingThread.IsAlive)
        {
            _ = trackingThread.Join(100);
        }
        cancellation?.Dispose();
        if (!_disposed && IsHandleCreated)
        {
            _ = ShowWindow(Handle, HideCommand);
        }
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.Clear(Color.Magenta);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Snapshot snapshot;
        lock (_sync)
        {
            snapshot = new Snapshot(
                _tool,
                _start,
                _current,
                _points.ToArray(),
                _color,
                _strokeWidth,
                _arrowStyle);
        }

        var origin = new DrawingPoint(-Left + FramePadding, -Top + FramePadding);
        using var pen = new Pen(snapshot.Color, snapshot.StrokeWidth)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round,
        };

        var start = Offset(snapshot.Start, origin);
        var current = Offset(snapshot.Current, origin);
        switch (snapshot.Tool)
        {
            case EditorTool.Rectangle:
                e.Graphics.DrawRectangle(pen, FromPoints(start, current));
                break;
            case EditorTool.Ellipse:
                e.Graphics.DrawEllipse(pen, FromPoints(start, current));
                break;
            case EditorTool.Arrow:
                DrawArrow(e.Graphics, pen, start, current, snapshot.ArrowStyle);
                break;
            case EditorTool.CurvedArrow:
                DrawCurvedArrow(e.Graphics, pen, snapshot.Points, origin, snapshot.ArrowStyle);
                break;
            case EditorTool.Brush:
                DrawPolyline(e.Graphics, pen, snapshot.Points, origin);
                break;
            case EditorTool.Mosaic:
                using (var mosaicPen = new Pen(
                           Color.FromArgb(210, snapshot.Color.R,
                               snapshot.Color.G, snapshot.Color.B),
                           snapshot.StrokeWidth)
                       {
                           StartCap = LineCap.Round,
                           EndCap = LineCap.Round,
                           LineJoin = LineJoin.Round,
                       })
                {
                    DrawPolyline(e.Graphics, mosaicPen, snapshot.Points, origin);
                }
                break;
        }

        Painted?.Invoke(this, EventArgs.Empty);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _disposed = true;
            Stop();
        }

        base.Dispose(disposing);
    }

    private void UpdateCursor(DrawingPoint cursor)
    {
        lock (_sync)
        {
            _current = cursor;
            if (_tool is EditorTool.Brush or EditorTool.CurvedArrow or EditorTool.Mosaic)
            {
                if (_points.Count == 0 ||
                    DistanceSquared(_points[^1], cursor) >= 1)
                {
                    _points.Add(cursor);
                    _pathLeft = Math.Min(_pathLeft, cursor.X);
                    _pathTop = Math.Min(_pathTop, cursor.Y);
                    _pathRight = Math.Max(_pathRight, cursor.X);
                    _pathBottom = Math.Max(_pathBottom, cursor.Y);
                }
            }
        }

        var bounds = CalculateBounds();
        _ = SetWindowPos(
            Handle,
            new IntPtr(TopmostWindow),
            bounds.X,
            bounds.Y,
            bounds.Width,
            bounds.Height,
            DoNotActivate | DoNotChangeOwnerZOrder |
            AsynchronousWindowPosition);
        _ = ShowWindow(Handle, ShowNormal);
        _ = InvalidateRect(Handle, IntPtr.Zero, false);
        _ = UpdateWindow(Handle);
    }

    public DrawingPoint[] GetPoints()
    {
        lock (_sync)
        {
            return _points.ToArray();
        }
    }

    private Rectangle CalculateBounds()
    {
        lock (_sync)
        {
            var left = Math.Min(_start.X, _current.X);
            var top = Math.Min(_start.Y, _current.Y);
            var right = Math.Max(_start.X, _current.X);
            var bottom = Math.Max(_start.Y, _current.Y);
            left = Math.Min(left, _pathLeft);
            top = Math.Min(top, _pathTop);
            right = Math.Max(right, _pathRight);
            bottom = Math.Max(bottom, _pathBottom);

            return new Rectangle(
                left - FramePadding,
                top - FramePadding,
                Math.Max(1, right - left + (FramePadding * 2)),
                Math.Max(1, bottom - top + (FramePadding * 2)));
        }
    }

    private static void DrawPolyline(
        Graphics graphics,
        Pen pen,
        IReadOnlyList<DrawingPoint> points,
        DrawingPoint origin)
    {
        if (points.Count < 2)
        {
            return;
        }

        var local = points.Select(point => Offset(point, origin)).ToArray();
        graphics.DrawLines(pen, local);
    }

    private static void DrawCurvedArrow(
        Graphics graphics,
        Pen pen,
        IReadOnlyList<DrawingPoint> points,
        DrawingPoint origin,
        ArrowStyle style)
    {
        if (points.Count < 2)
        {
            return;
        }

        DrawArrowTail(
            graphics,
            pen,
            Offset(points[0], origin),
            Offset(points[1], origin),
            style);
        var startCap = pen.StartCap;
        pen.StartCap = LineCap.Flat;
        DrawPolyline(graphics, pen, points, origin);
        pen.StartCap = startCap;
        var end = Offset(points[^1], origin);
        var previous = Offset(points[^2], origin);
        DrawArrowHead(graphics, pen, previous, end, style);
    }

    private static void DrawArrow(
        Graphics graphics,
        Pen pen,
        DrawingPoint start,
        DrawingPoint end,
        ArrowStyle style)
    {
        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        var length = Math.Sqrt((dx * dx) + (dy * dy));
        if (length < 2)
        {
            return;
        }

        var metrics = ArrowGeometryMetrics.For(length, pen.Width);
        var ux = dx / length;
        var uy = dy / length;
        var perpendicularX = -uy;
        var perpendicularY = ux;
        var tailBaseX = start.X + (ux * Math.Min(
            Math.Max(pen.Width * 1.5, metrics.HeadLength * 0.16),
            length * 0.12));
        var tailBaseY = start.Y + (uy * Math.Min(
            Math.Max(pen.Width * 1.5, metrics.HeadLength * 0.16),
            length * 0.12));
        var baseX = end.X - (ux * metrics.HeadLength);
        var baseY = end.Y - (uy * metrics.HeadLength);
        var polygon = new[]
        {
            new PointF((float)start.X, (float)start.Y),
            new PointF(
                (float)(tailBaseX + (perpendicularX * metrics.TailHalfWidth)),
                (float)(tailBaseY + (perpendicularY * metrics.TailHalfWidth))),
            new PointF(
                (float)(baseX + (perpendicularX * metrics.BaseHalfWidth)),
                (float)(baseY + (perpendicularY * metrics.BaseHalfWidth))),
            new PointF(
                (float)(baseX + (perpendicularX * metrics.HeadHalfWidth)),
                (float)(baseY + (perpendicularY * metrics.HeadHalfWidth))),
            new PointF((float)end.X, (float)end.Y),
            new PointF(
                (float)(baseX - (perpendicularX * metrics.HeadHalfWidth)),
                (float)(baseY - (perpendicularY * metrics.HeadHalfWidth))),
            new PointF(
                (float)(baseX - (perpendicularX * metrics.BaseHalfWidth)),
                (float)(baseY - (perpendicularY * metrics.BaseHalfWidth))),
            new PointF(
                (float)(tailBaseX - (perpendicularX * metrics.TailHalfWidth)),
                (float)(tailBaseY - (perpendicularY * metrics.TailHalfWidth))),
        };

        if (style == ArrowStyle.Hollow)
        {
            graphics.DrawPolygon(pen, polygon);
        }
        else
        {
            using var brush = new SolidBrush(pen.Color);
            graphics.FillPolygon(brush, polygon);
        }
    }

    private static void DrawArrowTail(
        Graphics graphics,
        Pen pen,
        DrawingPoint start,
        DrawingPoint next,
        ArrowStyle style)
    {
        var dx = next.X - start.X;
        var dy = next.Y - start.Y;
        var length = Math.Sqrt((dx * dx) + (dy * dy));
        if (length < 2)
        {
            return;
        }

        var metrics = ArrowGeometryMetrics.For(length, pen.Width);
        var transition = Math.Min(
            Math.Max(pen.Width * 1.5, metrics.HeadLength * 0.16),
            length * 0.12);
        var ux = dx / length;
        var uy = dy / length;
        var baseX = start.X + (ux * transition);
        var baseY = start.Y + (uy * transition);
        var halfWidth = metrics.TailHalfWidth;
        var polygon = new[]
        {
            new PointF(start.X, start.Y),
            new PointF((float)(baseX - (uy * halfWidth)), (float)(baseY + (ux * halfWidth))),
            new PointF((float)(baseX + (uy * halfWidth)), (float)(baseY - (ux * halfWidth))),
        };

        if (style == ArrowStyle.Hollow)
        {
            graphics.DrawPolygon(pen, polygon);
        }
        else
        {
            using var brush = new SolidBrush(pen.Color);
            graphics.FillPolygon(brush, polygon);
        }
    }

    private static void DrawArrowHead(
        Graphics graphics,
        Pen pen,
        DrawingPoint start,
        DrawingPoint end,
        ArrowStyle style)
    {
        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        var length = Math.Sqrt((dx * dx) + (dy * dy));
        if (length < 2)
        {
            return;
        }

        var ux = dx / length;
        var uy = dy / length;
        var metrics = ArrowGeometryMetrics.For(length, pen.Width);
        var headLength = metrics.HeadLength;
        var halfWidth = metrics.HeadHalfWidth;
        var baseX = end.X - (ux * headLength);
        var baseY = end.Y - (uy * headLength);
        var left = new PointF(
            (float)(baseX - (uy * halfWidth)),
            (float)(baseY + (ux * halfWidth)));
        var right = new PointF(
            (float)(baseX + (uy * halfWidth)),
            (float)(baseY - (ux * halfWidth)));
        var tip = new PointF(end.X, end.Y);
        if (style == ArrowStyle.Hollow)
        {
            graphics.DrawPolygon(pen, [left, tip, right]);
        }
        else
        {
            using var brush = new SolidBrush(pen.Color);
            graphics.FillPolygon(brush, [left, tip, right]);
        }
    }

    private static Rectangle FromPoints(DrawingPoint first, DrawingPoint second)
    {
        return new Rectangle(
            Math.Min(first.X, second.X),
            Math.Min(first.Y, second.Y),
            Math.Max(1, Math.Abs(first.X - second.X)),
            Math.Max(1, Math.Abs(first.Y - second.Y)));
    }

    private static DrawingPoint Offset(DrawingPoint point, DrawingPoint origin) =>
        new(point.X + origin.X, point.Y + origin.Y);

    private static int DistanceSquared(DrawingPoint first, DrawingPoint second)
    {
        var dx = first.X - second.X;
        var dy = first.Y - second.Y;
        return (dx * dx) + (dy * dy);
    }

    private readonly record struct Snapshot(
        EditorTool Tool,
        DrawingPoint Start,
        DrawingPoint Current,
        DrawingPoint[] Points,
        Color Color,
        float StrokeWidth,
        ArrowStyle ArrowStyle);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(
        IntPtr window,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr window, int command);

    [DllImport("user32.dll")]
    private static extern bool InvalidateRect(
        IntPtr window,
        IntPtr updateRectangle,
        bool erase);

    [DllImport("user32.dll")]
    private static extern bool UpdateWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("winmm.dll")]
    private static extern uint timeBeginPeriod(uint periodMilliseconds);

    [DllImport("winmm.dll")]
    private static extern uint timeEndPeriod(uint periodMilliseconds);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;

        public static implicit operator DrawingPoint(NativePoint point) =>
            new(point.X, point.Y);
    }
}
