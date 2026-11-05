using System.Windows;
using System.Windows.Media;
using Point = System.Windows.Point;
using Pen = System.Windows.Media.Pen;
using Size = System.Windows.Size;

namespace Screenshot.App.Presentation;

/// <summary>A convex optical lip. Its centre paints nothing over text or desktop content.</summary>
public sealed class LiquidGlassLens : FrameworkElement
{
    private static readonly Pen BroadRim = MakePen(4.5,
        (0, "#80FFFFFF"), (.19, "#1CFFFFFF"), (.46, "#38545F68"), (.72, "#08FFFFFF"), (1, "#80F8FCFF"));
    private static readonly Pen InnerRim = MakePen(1,
        (0, "#18FFFFFF"), (.30, "#384D5962"), (.65, "#04FFFFFF"), (1, "#A8FFFFFF"));
    private static readonly Pen SurfaceLight = MakePen(1.15,
        (0, "#F0FFFFFF"), (.25, "#48FFFFFF"), (.53, "#084E5B65"), (.78, "#78E7F3FA"), (1, "#E8FFFFFF"));

    public static readonly DependencyProperty IsEmphasizedProperty = DependencyProperty.Register(
        nameof(IsEmphasized), typeof(bool), typeof(LiquidGlassLens),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public bool IsEmphasized { get => (bool)GetValue(IsEmphasizedProperty); set => SetValue(IsEmphasizedProperty, value); }

    public LiquidGlassLens() { IsHitTestVisible = false; }

    protected override void OnRender(DrawingContext drawingContext)
    {
        drawingContext.PushOpacity(IsEmphasized ? 1 : .82);
        DrawLip(drawingContext, RenderSize, 11);
        drawingContext.Pop();
    }

    internal static void DrawLip(DrawingContext dc, Size size, double radius)
    {
        if (size.Width < 14 || size.Height < 14) return;
        // Paired light/dark curved rims suggest a thick meniscus. They remain
        // inside the outline, leave the entire reading centre unpainted and
        // use shared frozen pens without timers, captures or bitmap filters.
        dc.DrawRoundedRectangle(null, BroadRim, new Rect(3, 3, size.Width - 6, size.Height - 6), radius - 1, radius - 1);
        dc.DrawRoundedRectangle(null, InnerRim, new Rect(5.5, 5.5, size.Width - 11, size.Height - 11), radius - 3, radius - 3);
        dc.DrawRoundedRectangle(null, SurfaceLight, new Rect(1, 1, size.Width - 2, size.Height - 2), radius, radius);
    }

    private static Pen MakePen(double width, params (double Offset, string Color)[] stops)
    {
        var brush = new LinearGradientBrush { StartPoint = new Point(.05, 0), EndPoint = new Point(.95, 1) };
        foreach (var stop in stops)
            brush.GradientStops.Add(new GradientStop((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(stop.Color), stop.Offset));
        var pen = new Pen(brush, width);
        pen.Freeze();
        return pen;
    }
}
