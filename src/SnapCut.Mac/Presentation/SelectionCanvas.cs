using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace SnapCut.Mac.Presentation;

internal sealed class SelectionCanvas : Control
{
    private readonly Bitmap _background;
    private readonly Pen _selectionPen = new(MacTheme.AccentBrush, 2);
    private readonly IBrush _mask = new SolidColorBrush(Color.FromArgb(150, 0, 0, 0));
    private Point _anchor;
    private Point _current;
    private bool _selecting;

    public SelectionCanvas(Bitmap background)
    {
        _background = background;
        Cursor = new Cursor(StandardCursorType.Cross);
        Focusable = true;
    }

    public event Action<Rect>? SelectionCompleted;

    public Rect Selection => Normalize(_anchor, _current);

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var bounds = new Rect(Bounds.Size);
        context.DrawImage(_background, bounds);
        var selection = Selection.Intersect(bounds);
        if (!_selecting && selection.Width < 1)
        {
            context.DrawRectangle(_mask, null, bounds);
            return;
        }

        DrawMask(context, bounds, selection);
        if (selection.Width > 0 && selection.Height > 0)
        {
            context.DrawRectangle(null, _selectionPen, selection);
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var properties = e.GetCurrentPoint(this).Properties;
        if (!properties.IsLeftButtonPressed)
        {
            return;
        }

        _anchor = Clamp(e.GetPosition(this));
        _current = _anchor;
        _selecting = true;
        e.Pointer.Capture(this);
        Focus();
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_selecting)
        {
            return;
        }

        _current = Clamp(e.GetPosition(this));
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_selecting)
        {
            return;
        }

        _current = Clamp(e.GetPosition(this));
        _selecting = false;
        e.Pointer.Capture(null);
        var selection = Selection;
        InvalidateVisual();
        if (selection.Width >= 6 && selection.Height >= 6)
        {
            SelectionCompleted?.Invoke(selection);
        }

        e.Handled = true;
    }

    private Point Clamp(Point point)
    {
        return new Point(
            Math.Clamp(point.X, 0, Bounds.Width),
            Math.Clamp(point.Y, 0, Bounds.Height));
    }

    private static Rect Normalize(Point first, Point second)
    {
        var left = Math.Min(first.X, second.X);
        var top = Math.Min(first.Y, second.Y);
        return new Rect(
            left,
            top,
            Math.Abs(second.X - first.X),
            Math.Abs(second.Y - first.Y));
    }

    private void DrawMask(DrawingContext context, Rect bounds, Rect selection)
    {
        if (selection.Width <= 0 || selection.Height <= 0)
        {
            context.DrawRectangle(_mask, null, bounds);
            return;
        }

        context.DrawRectangle(
            _mask,
            null,
            new Rect(bounds.X, bounds.Y, bounds.Width, selection.Top));
        context.DrawRectangle(
            _mask,
            null,
            new Rect(bounds.X, selection.Bottom, bounds.Width, bounds.Bottom - selection.Bottom));
        context.DrawRectangle(
            _mask,
            null,
            new Rect(bounds.X, selection.Top, selection.Left, selection.Height));
        context.DrawRectangle(
            _mask,
            null,
            new Rect(selection.Right, selection.Top, bounds.Right - selection.Right, selection.Height));
    }
}
