using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Screenshot.App.Infrastructure;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;
using Pen = System.Windows.Media.Pen;
using Size = System.Windows.Size;

namespace Screenshot.App.Presentation;

internal sealed class LiquidGlassHighlight : FrameworkElement, IDisposable
{
    private readonly Window _owner;
    private readonly PointerFramePump _frames;
    private readonly RadialGradientBrush _shine = new(Colors.White, Colors.Transparent);
    private static readonly Pen RibbonTint = RimPen(Color.FromArgb(0x0C, 0xDC, 0xEB, 0xF2), 9);
    private static readonly Pen RibbonLight = RimPen(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF), 1.2);
    private StreamGeometry? _ribbon;
    private Geometry? _reflectionRim;
    private Size _ribbonSize;
    private Pen _edge = new(LiquidGlassMaterial.CreateEdge(false), 1.6);
    private Point _pointer;
    private bool _hasPointer;
    private bool _dark;

    internal LiquidGlassHighlight(Window owner)
    {
        _owner = owner;
        IsHitTestVisible = false;
        ClipToBounds = true;
        _shine.Freeze();
        _frames = new PointerFramePump(Present);
        owner.PreviewMouseMove += OnPointerMove;
        owner.MouseLeave += OnPointerLeave;
        owner.SizeChanged += OnResize;
        owner.Closed += OnClosed;
    }
    private void Present() { _frames.Stop(); InvalidateVisual(); }
    internal void SetTheme(bool enabled, bool dark)
    {
        _dark = dark;
        _edge = new Pen(LiquidGlassMaterial.CreateEdge(dark), 1.6);
        _edge.Freeze();
        Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        _frames.Stop();
        _hasPointer = false;
        InvalidateVisual();
    }
    private void OnPointerMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (Visibility != Visibility.Visible || (!_dark && !UiTransitions.IsEnabled) || e.LeftButton == MouseButtonState.Pressed) return;
        _pointer = e.GetPosition(this);
        _hasPointer = true;
        _frames.Start(); // One render per batch; no idle animation or mouse-message queue.
    }
    private void OnPointerLeave(object sender, System.Windows.Input.MouseEventArgs e) => ClearPointer();
    private void OnResize(object sender, SizeChangedEventArgs e)
    {
        _reflectionRim = null;
        ClearPointer();
    }
    private void ClearPointer()
    {
        _frames.Stop();
        if (!_hasPointer) return;
        _hasPointer = false;
        InvalidateVisual();
    }
    protected override void OnRender(DrawingContext dc)
    {
        if (ActualWidth < 2 || ActualHeight < 2) return;
        if (!_dark && ActualWidth > 8 && ActualHeight > 8)
        {
            if (_ribbon is null || _ribbonSize != RenderSize)
            {
                _ribbonSize = RenderSize;
                _ribbon = new StreamGeometry();
                using (var path = _ribbon.Open())
                {
                    path.BeginFigure(new Point(-10, ActualHeight * .22), false, false);
                    path.BezierTo(new Point(ActualWidth * .4, -10), new Point(ActualWidth * .65, ActualHeight * .36),
                        new Point(ActualWidth + 10, ActualHeight * .18), true, false);
                }
                _ribbon.Freeze();
            }
            dc.DrawGeometry(null, RibbonTint, _ribbon);
            dc.DrawGeometry(null, RibbonLight, _ribbon);
            // A narrow shadow and an inner highlight give the glass rim
            // thickness without filtering or distorting the window's content.
            LiquidGlassLens.DrawLip(dc, RenderSize, 13);
        }
        dc.DrawRoundedRectangle(null, _edge, new Rect(.8, .8, ActualWidth - 1.6, ActualHeight - 1.6), 12, 12);
        if (!_hasPointer) return;
        dc.PushOpacity(_dark ? .10 : .035);
        dc.DrawEllipse(_shine, null, _pointer, _dark ? 170 : 150, _dark ? 120 : 100);
        dc.Pop();
        if (_dark || ActualWidth <= 16 || ActualHeight <= 16) return;
        _reflectionRim ??= CreateReflectionRim();
        // Concentrate the moving reflection in the curved lip. Keeping it out
        // of the reading area prevents pointer movement from washing out text.
        dc.PushClip(_reflectionRim);
        // Reflections follow the pointer along each lip even while the
        // pointer is in the middle. Smooth weights avoid jumping between edges.
        var x = Math.Clamp(_pointer.X / ActualWidth, 0, 1);
        var y = Math.Clamp(_pointer.Y / ActualHeight, 0, 1);
        DrawReflection(dc, new Point(_pointer.X, 0), .25 + .6 * (1 - y), 190, 28);
        DrawReflection(dc, new Point(_pointer.X, ActualHeight), .2 + .5 * y, 190, 28);
        DrawReflection(dc, new Point(0, _pointer.Y), .15 + .45 * (1 - x), 28, 140);
        DrawReflection(dc, new Point(ActualWidth, _pointer.Y), .15 + .45 * x, 28, 140);
        dc.Pop();
    }
    private GeometryGroup CreateReflectionRim()
    {
        var rim = new GeometryGroup { FillRule = FillRule.EvenOdd };
        rim.Children.Add(new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight), 13, 13));
        rim.Children.Add(new RectangleGeometry(new Rect(8, 8, ActualWidth - 16, ActualHeight - 16), 6, 6));
        rim.Freeze();
        return rim;
    }
    private void DrawReflection(DrawingContext dc, Point centre, double opacity, double width, double height)
    {
        dc.PushOpacity(opacity);
        dc.DrawEllipse(_shine, null, centre, width, height);
        dc.Pop();
    }
    private static Pen RimPen(Color color, double thickness)
    {
        var pen = new Pen(new SolidColorBrush(color), thickness);
        pen.Freeze();
        return pen;
    }
    private void OnClosed(object? sender, EventArgs e) => Dispose();
    public void Dispose()
    {
        _frames.Dispose();
        _owner.PreviewMouseMove -= OnPointerMove;
        _owner.MouseLeave -= OnPointerLeave;
        _owner.SizeChanged -= OnResize;
        _owner.Closed -= OnClosed;
    }
}
