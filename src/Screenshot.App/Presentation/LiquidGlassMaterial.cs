using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;
using Pen = System.Windows.Media.Pen;
using ColorConverter = System.Windows.Media.ColorConverter;

namespace Screenshot.App.Presentation;

/// <summary>Cached glass surfaces, with a translucent pink-lilac ribbon material.</summary>
internal static class LiquidGlassMaterial
{
    // Live windows/resources own their frozen textures. Previewing a different
    // theme must not keep its full-size render targets alive for the process.
    private static readonly WeakReference<ImageBrush>?[] Backgrounds = new WeakReference<ImageBrush>?[4];
    private static readonly object BackgroundGate = new();

    internal static ImageBrush CreateBackground(bool dark, bool glassLayer = false)
    {
        lock (BackgroundGate)
        {
            var index = (dark ? 2 : 0) + (glassLayer ? 1 : 0);
            if (Backgrounds[index]?.TryGetTarget(out var existing) == true) return existing;
            var brush = RenderBackground(dark, glassLayer);
            Backgrounds[index] = new(brush);
            return brush;
        }
    }

    internal static Brush CreateFloatingBackground(bool dark)
    {
        if (dark) return CreateBackground(true);
        var brush = new ImageBrush(CreateBackground(false).ImageSource) { Stretch = Stretch.Fill, Opacity = .72 };
        brush.Freeze();
        return brush;
    }

    internal static LinearGradientBrush CreateClearSurface(bool floating)
    {
        // Broad transparent centre, narrow reflected light at each lip. These
        // frozen brushes are shared by controls; no backdrop copies or timers.
        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(.2, 1) };
        foreach (var (offset, color) in floating
            ? new[] { (0d, "#B2FFFFFF"), (.06, "#A2FCFEFF"), (.38, "#9CF6FBFD"), (.82, "#9CF0F7FA"), (1d, "#AAFFFFFF") }
            : new[] { (0d, "#38FFFFFF"), (.14, "#0CFFFFFF"), (.42, "#00FFFFFF"), (.82, "#00FFFFFF"), (1d, "#18F1F8FC") })
            brush.GradientStops.Add(new GradientStop(Parse(color), offset));
        brush.Freeze();
        return brush;
    }

    private static SolidColorBrush Solid(string color)
    {
        var brush = new SolidColorBrush(Parse(color));
        brush.Freeze();
        return brush;
    }

    // The resident only paints a tiny floating button/menu. Retaining two
    // 1000px textures there costs megabytes without improving those surfaces.
    internal static DrawingBrush CreateVectorBackground(bool dark, bool glassLayer = false)
    {
        var brush = new DrawingBrush(CreateBackgroundDrawing(dark, glassLayer)) { Stretch = Stretch.Fill };
        brush.Freeze();
        return brush;
    }

    private static DrawingGroup CreateBackgroundDrawing(bool dark, bool glassLayer)
    {
        var group = new DrawingGroup { ClipGeometry = new RectangleGeometry(new Rect(0, 0, 1000, 1000)) };
        using (var dc = group.Open())
        {
            if (!dark)
            {
                // One pink-lilac tint lets the system-blurred software behind
                // the window show through. Only the ribbon carries a highlight.
                dc.DrawRectangle(Solid(glassLayer ? "#99F7F2FA" : "#F7F2FA"), null,
                    new Rect(0, 0, 1000, 1000));
                var ribbon = Geometry.Parse("M -120,360 C 160,540 200,40 500,120 C 760,190 840,480 1120,250");
                dc.DrawGeometry(null, new Pen(Solid("#0970428F"), 32), ribbon);
                dc.DrawGeometry(null, new Pen(Solid("#20F2D7EF"), 20), ribbon);
                dc.DrawGeometry(null, new Pen(Solid("#46FFFFFF"), 7), ribbon);
                dc.DrawGeometry(null, new Pen(Solid("#80FFFFFF"), 1.5), ribbon);
            }
            else
            {
            dc.DrawRectangle(Gradient(
                dark ? glassLayer ? "#581D3255" : "#142646" : glassLayer ? "#85F5FBFF" : "#D8EAF7",
                dark ? glassLayer ? "#302E3E69" : "#29375D" : glassLayer ? "#42DCEFFF" : "#D6DAF4"), null,
                new Rect(0, 0, 1000, 1000));
            dc.PushOpacity(dark ? glassLayer ? .30 : .65 : glassLayer ? .45 : .85);
            Glow(dc, dark ? "#8C356E9B" : "#B8B0EAF8", new Point(180, 140), 510, 490);
            Glow(dc, dark ? "#785D5398" : "#B5C2B7F3", new Point(930, 360), 540, 650);
            Glow(dc, dark ? "#703C889C" : "#B4AFF4EA", new Point(350, 960), 590, 430);
            Glow(dc, dark ? "#306FBFCB" : "#C4FFFFFF", new Point(450, 410), 340, 400);
            // Bright curved caustics plus a displaced cool shadow suggest a
            // refracting lens. Text and captured pixels are never distorted.
            var ribbon = Geometry.Parse("M -180,600 C 125,820 105,145 430,100 C 775,55 870,410 1160,85");
            dc.DrawGeometry(null, new Pen(Gradient("#00FFFFFF", dark ? "#1668ABDD" : "#1A63A6E0"), 32), ribbon);
            dc.DrawGeometry(null, new Pen(Gradient("#00FFFFFF", dark ? "#2268BED0" : "#25FFFFFF"), 18), ribbon);
            dc.DrawGeometry(null, new Pen(Gradient("#00FFFFFF", dark ? "#3868BED0" : "#50FFFFFF"), 7), ribbon);
            dc.DrawGeometry(null, new Pen(Gradient(dark ? "#20699AB8" : "#305391C7", "#00FFFFFF"), 1.2), ribbon);
            dc.Pop();
            if (glassLayer)
                dc.DrawRectangle(Gradient(dark ? "#16233051" : "#30FFFFFF", "#00FFFFFF"), null, new Rect(0, 0, 1000, 1000));
            }
        }
        group.Freeze();
        return group;
    }

    private static ImageBrush RenderBackground(bool dark, bool glassLayer)
    {
        var group = CreateBackgroundDrawing(dark, glassLayer);
        // Rasterize the soft optical layers once. A frozen DrawingBrush still
        // rerenders its gradients/opacity groups at every new window size.
        // Resizing now samples one shared texture, without rebuilding a bitmap.
        const int size = 1000;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen()) dc.DrawDrawing(group);
        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        var brush = new ImageBrush(bitmap) { Stretch = Stretch.Fill };
        brush.Freeze();
        return brush;
    }

    internal static LinearGradientBrush CreateEdge(bool dark)
    {
        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
        brush.GradientStops.Add(new GradientStop(Parse(dark ? "#9EDCEBFF" : "#F4FFFFFF"), 0));
        brush.GradientStops.Add(new GradientStop(Parse(dark ? "#35579BAC" : "#8CF1F7FA"), dark ? .35 : .28));
        brush.GradientStops.Add(new GradientStop(Parse(dark ? "#528C84C9" : "#80636F77"), dark ? .68 : .55));
        if (!dark) brush.GradientStops.Add(new GradientStop(Parse("#68B9C9D3"), .72));
        brush.GradientStops.Add(new GradientStop(Parse(dark ? "#B08DC7E9" : "#E8FFFFFF"), 1));
        brush.Freeze();
        return brush;
    }

    private static void Glow(DrawingContext dc, string color, Point center, double width, double height)
    {
        var glow = new RadialGradientBrush(Parse(color), Colors.Transparent)
        { GradientOrigin = new Point(.45, .35), Center = new Point(.5, .5) };
        glow.Freeze();
        dc.DrawEllipse(glow, null, center, width, height);
    }
    private static LinearGradientBrush Gradient(string start, string end)
    {
        var brush = new LinearGradientBrush(Parse(start), Parse(end), 35);
        brush.Freeze();
        return brush;
    }
    private static Color Parse(string value) => (Color)ColorConverter.ConvertFromString(value);
}
