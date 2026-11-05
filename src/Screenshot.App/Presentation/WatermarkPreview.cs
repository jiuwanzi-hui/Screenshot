using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Screenshot.App.Core;
using Screenshot.App.Watermarks;
using Point = System.Windows.Point;
using Brushes = System.Windows.Media.Brushes;
using Pen = System.Windows.Media.Pen;
using Cursors = System.Windows.Input.Cursors;
using Brush = System.Windows.Media.Brush;

namespace Screenshot.App.Presentation;

internal sealed class WatermarkPreview : FrameworkElement
{
    private readonly DrawingVisual _drawing = new();
    private static readonly DependencyProperty SurfaceProperty = DependencyProperty.Register("Surface", typeof(Brush), typeof(WatermarkPreview), new FrameworkPropertyMetadata(Brushes.Transparent, FrameworkPropertyMetadataOptions.AffectsRender));
    private static readonly DependencyProperty GridBrushProperty = DependencyProperty.Register("GridBrush", typeof(Brush), typeof(WatermarkPreview), new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));
    private static readonly DependencyProperty LabelBrushProperty = DependencyProperty.Register("LabelBrush", typeof(Brush), typeof(WatermarkPreview), new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));
    private static readonly DependencyProperty AccentProperty = DependencyProperty.Register("Accent", typeof(Brush), typeof(WatermarkPreview), new FrameworkPropertyMetadata(Brushes.SteelBlue, FrameworkPropertyMetadataOptions.AffectsRender));
    internal WatermarkComposition? Composition { get; set; }
    internal WatermarkSettings Placement { get; set; } = new();
    internal WatermarkElement SelectedElement { get; private set; } = WatermarkElement.Text;
    internal event Action<WatermarkSettings>? PlacementChanged;
    internal event Action<WatermarkElement>? SelectionChanged;
    private Point _start;
    private Rect _original;
    private bool _resizing;
    private WatermarkSettings? _beforeDrag;

    public WatermarkPreview()
    {
        AddVisualChild(_drawing);
        Focusable = true;
        ClipToBounds = true;
        MinHeight = 240;
        SetResourceReference(SurfaceProperty, "AppPanelBackgroundBrush");
        SetResourceReference(GridBrushProperty, "AppSubtleBorderBrush");
        SetResourceReference(LabelBrushProperty, "AppTextSecondaryBrush");
        SetResourceReference(AccentProperty, "AppAccentBrush");
        System.Windows.Automation.AutomationProperties.SetName(this, "水印预览：分别选中图片、文字、时间或地点，拖动移动，右下角缩放；方向键微调，加减键缩放");
    }

    internal void SelectElement(WatermarkElement element)
    {
        SelectedElement = element;
        InvalidateVisual();
        SelectionChanged?.Invoke(element);
    }

    protected override int VisualChildrenCount => 1;
    protected override Visual GetVisualChild(int index) => index == 0 ? _drawing : throw new ArgumentOutOfRangeException(nameof(index));

    internal void ClearContent()
    {
        EndManipulation();
        Composition = null;
        // A collapsed element may never receive OnRender. Drop retained drawing
        // commands now so the last frame cannot keep imported pixels alive.
        using (_drawing.RenderOpen()) { }
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        using var dc = _drawing.RenderOpen();
        DrawPreview(dc);
    }

    private void DrawPreview(DrawingContext dc)
    {
        var size = RenderSize;
        dc.DrawRoundedRectangle((Brush)GetValue(SurfaceProperty), new Pen((Brush)GetValue(GridBrushProperty), 1), new Rect(size), 10, 10);
        var pen = new Pen((Brush)GetValue(GridBrushProperty), 1);
        dc.PushOpacity(.3);
        for (double x = 24; x < size.Width; x += 32) dc.DrawLine(pen, new Point(x, 0), new Point(x, size.Height));
        for (double y = 24; y < size.Height; y += 32) dc.DrawLine(pen, new Point(0, y), new Point(size.Width, y));
        dc.Pop();
        var label = new FormattedText("截图预览", CultureInfo.CurrentCulture, System.Windows.FlowDirection.LeftToRight,
            new Typeface("Microsoft YaHei UI"), 18, (Brush)GetValue(LabelBrushProperty), 1);
        dc.DrawText(label, new Point(20, 18));
        if (Composition is null) return;
        Composition.Draw(dc, size, Placement);
        var bounds = Composition.GetBounds(SelectedElement, size, Placement);
        if (bounds.IsEmpty) return;
        dc.DrawRectangle(null, new Pen((Brush)GetValue(AccentProperty), 1.5), bounds);
        dc.DrawRectangle((Brush)GetValue(LabelBrushProperty), new Pen((Brush)GetValue(AccentProperty), 1), Handle(bounds));
    }

    private static Rect Handle(Rect bounds) => new(bounds.Right - 6, bounds.Bottom - 6, 12, 12);

    internal bool BeginManipulation(Point point)
    {
        if (Composition is null) return false;
        var bounds = Composition.GetBounds(SelectedElement, RenderSize, Placement);
        _resizing = !bounds.IsEmpty && Handle(bounds).Contains(point);
        if (!_resizing)
        {
            // Prefer the selected part, then the topmost visible part. The selector above
            // the preview also makes fully overlapped/tiny elements reachable.
            var hit = !bounds.IsEmpty && bounds.Contains(point) ? SelectedElement :
                Composition.Parts.Reverse().Where(part => Composition.GetBounds(part.Element, RenderSize, Placement).Contains(point))
                    .Select(part => (WatermarkElement?)part.Element).FirstOrDefault();
            if (hit is null) return false;
            SelectElement(hit.Value);
            bounds = Composition.GetBounds(SelectedElement, RenderSize, Placement);
        }
        _beforeDrag = Placement;
        _start = point;
        _original = bounds;
        return true;
    }

    internal void MoveManipulation(Point point)
    {
        if (_beforeDrag is null || Composition is null) return;
        var original = _beforeDrag.GetPlacement(SelectedElement);
        var dx = point.X - _start.X;
        var dy = point.Y - _start.Y;
        // Project onto the diagonal: either horizontal or vertical handle movement scales uniformly.
        var factor = _resizing ? Math.Max(.01, 1 + (dx * _original.Width + dy * _original.Height) /
            (_original.Width * _original.Width + _original.Height * _original.Height)) : 1;
        var provisional = _beforeDrag.WithPlacement(SelectedElement, original with { Size = original.Size * factor });
        var bounds = Composition.GetBounds(SelectedElement, RenderSize, provisional);
        var position = provisional.GetPlacement(SelectedElement) with
        {
            X = Ratio(_original.Left + (_resizing ? 0 : dx), ActualWidth - bounds.Width),
            Y = Ratio(_original.Top + (_resizing ? 0 : dy), ActualHeight - bounds.Height),
        };
        Change(provisional.WithPlacement(SelectedElement, position));
    }

    internal void EndManipulation(bool cancel = false)
    {
        var restore = _beforeDrag;
        _beforeDrag = null;
        if (cancel && restore is not null) Change(restore);
        if (IsMouseCaptured) ReleaseMouseCapture();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (!BeginManipulation(e.GetPosition(this))) return;
        Focus();
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(System.Windows.Input.MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var point = e.GetPosition(this);
        if (IsMouseCaptured)
        {
            MoveManipulation(point);
            e.Handled = true;
        }
        else if (Composition is not null)
        {
            var bounds = Composition.GetBounds(SelectedElement, RenderSize, Placement);
            Cursor = !bounds.IsEmpty && Handle(bounds).Contains(point) ? Cursors.SizeNWSE :
                Composition.Parts.Any(part => Composition.GetBounds(part.Element, RenderSize, Placement).Contains(point)) ? Cursors.SizeAll : Cursors.Arrow;
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (IsMouseCaptured) { EndManipulation(); e.Handled = true; }
        base.OnMouseLeftButtonUp(e);
    }

    protected override void OnLostMouseCapture(System.Windows.Input.MouseEventArgs e)
    {
        _beforeDrag = null;
        base.OnLostMouseCapture(e);
    }

    protected override void OnKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _beforeDrag is not null) { EndManipulation(cancel: true); e.Handled = true; return; }
        if (Composition is null || Composition.GetBounds(SelectedElement, RenderSize, Placement).IsEmpty) return;
        var value = Placement.GetPlacement(SelectedElement);
        switch (e.Key)
        {
            case Key.Left: value = value with { X = value.X - .01 }; break;
            case Key.Right: value = value with { X = value.X + .01 }; break;
            case Key.Up: value = value with { Y = value.Y - .01 }; break;
            case Key.Down: value = value with { Y = value.Y + .01 }; break;
            case Key.Add: case Key.OemPlus: value = value with { Size = value.Size * 1.05 }; break;
            case Key.Subtract: case Key.OemMinus: value = value with { Size = value.Size / 1.05 }; break;
            default: return;
        }
        Change(Placement.WithPlacement(SelectedElement, value));
        e.Handled = true;
    }

    private static double Ratio(double offset, double available) => available <= 0 ? 0 : Math.Clamp(offset / available, 0, 1);
    private void Change(WatermarkSettings value) { Placement = value; InvalidateVisual(); PlacementChanged?.Invoke(value); }
}
