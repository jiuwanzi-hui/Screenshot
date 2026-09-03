using Avalonia;
using SnapCut.Core;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using DrawingPoint = SixLabors.ImageSharp.PointF;
using DrawingColor = SixLabors.ImageSharp.Color;
using AvaloniaPoint = Avalonia.Point;

namespace SnapCut.Mac.Editor;

internal static class MacAnnotationRenderer
{
    public static PixelImage Apply(
        PixelImage source,
        Rect selectionBounds,
        IReadOnlyList<MacAnnotation> annotations)
    {
        if (annotations.Count == 0 ||
            selectionBounds.Width <= 0 ||
            selectionBounds.Height <= 0)
        {
            return source;
        }

        var result = source.Clone();
        var scaleX = result.Width / selectionBounds.Width;
        var scaleY = result.Height / selectionBounds.Height;
        foreach (var mosaic in annotations.OfType<MacStrokeAnnotation>()
                     .Where(annotation => annotation.Tool == MacEditorTool.Mosaic))
        {
            ApplyMosaic(result, mosaic, selectionBounds, scaleX, scaleY);
        }

        using var image = Image.LoadPixelData<Bgra32>(
            result.Pixels,
            result.Width,
            result.Height);
        image.Mutate(context =>
        {
            foreach (var annotation in annotations)
            {
                DrawAnnotation(
                    context,
                    annotation,
                    selectionBounds,
                    scaleX,
                    scaleY);
            }
        });
        image.CopyPixelDataTo(result.Pixels);
        return result;
    }

    private static void DrawAnnotation(
        IImageProcessingContext context,
        MacAnnotation annotation,
        Rect bounds,
        double scaleX,
        double scaleY)
    {
        switch (annotation)
        {
            case MacShapeAnnotation shape:
                DrawShape(context, shape, bounds, scaleX, scaleY);
                break;
            case MacStrokeAnnotation stroke when stroke.Tool == MacEditorTool.Brush:
                DrawStroke(context, stroke, bounds, scaleX, scaleY);
                break;
            case MacTextAnnotation text:
                DrawText(context, text, bounds, scaleX, scaleY);
                break;
            case MacNumberAnnotation number:
                DrawNumber(context, number, bounds, scaleX, scaleY);
                break;
        }
    }

    private static void DrawShape(
        IImageProcessingContext context,
        MacShapeAnnotation shape,
        Rect bounds,
        double scaleX,
        double scaleY)
    {
        var start = ToPixel(shape.Start, bounds, scaleX, scaleY);
        var end = ToPixel(shape.End, bounds, scaleX, scaleY);
        var color = ToColor(shape.Color);
        var width = (float)Math.Max(1, shape.StrokeWidth * ((scaleX + scaleY) / 2));
        if (shape.Tool == MacEditorTool.Arrow)
        {
            var points = CreateArrowPoints(start, end, width);
            if (points.Length < 3)
            {
                context.DrawLine(color, width, start, end);
                return;
            }

            var polygon = new SixLabors.ImageSharp.Drawing.Polygon(points);
            if (shape.ArrowStyle == MacArrowStyle.Hollow)
            {
                context.Draw(color, Math.Max(1.5f, width * 0.55f), polygon);
            }
            else
            {
                context.Fill(color, polygon);
            }
            return;
        }

        var left = Math.Min(start.X, end.X);
        var top = Math.Min(start.Y, end.Y);
        var rectangle = new SixLabors.ImageSharp.RectangleF(
            left,
            top,
            Math.Max(1, Math.Abs(end.X - start.X)),
            Math.Max(1, Math.Abs(end.Y - start.Y)));
        if (shape.Tool == MacEditorTool.Ellipse)
        {
            context.Draw(
                color,
                width,
                new SixLabors.ImageSharp.Drawing.EllipsePolygon(
                    rectangle.X + (rectangle.Width / 2),
                    rectangle.Y + (rectangle.Height / 2),
                    rectangle.Width / 2,
                    rectangle.Height / 2));
        }
        else
        {
            context.Draw(color, width, rectangle);
        }
    }

    private static DrawingPoint[] CreateArrowPoints(
        DrawingPoint start,
        DrawingPoint end,
        float strokeWidth)
    {
        var deltaX = end.X - start.X;
        var deltaY = end.Y - start.Y;
        var length = Math.Sqrt((deltaX * deltaX) + (deltaY * deltaY));
        if (length < 1)
        {
            return [start, end];
        }

        var directionX = deltaX / length;
        var directionY = deltaY / length;
        var perpendicularX = -directionY;
        var perpendicularY = directionX;
        var headLength = Math.Min(
            Math.Max((length * 0.11) + (strokeWidth * 1.6), 9),
            Math.Min(44, length * 0.45));
        var headHalfWidth = headLength * 0.36;
        var baseHalfWidth = Math.Max(
            1.4,
            Math.Max(strokeWidth * 0.9, headHalfWidth * 0.22));
        var tailHalfWidth = Math.Max(0.6, strokeWidth * 0.22);
        var baseX = end.X - (directionX * headLength);
        var baseY = end.Y - (directionY * headLength);
        return
        [
            new DrawingPoint(
                start.X + (float)(perpendicularX * tailHalfWidth),
                start.Y + (float)(perpendicularY * tailHalfWidth)),
            new DrawingPoint(
                (float)(baseX + (perpendicularX * baseHalfWidth)),
                (float)(baseY + (perpendicularY * baseHalfWidth))),
            new DrawingPoint(
                (float)(baseX + (perpendicularX * headHalfWidth)),
                (float)(baseY + (perpendicularY * headHalfWidth))),
            end,
            new DrawingPoint(
                (float)(baseX - (perpendicularX * headHalfWidth)),
                (float)(baseY - (perpendicularY * headHalfWidth))),
            new DrawingPoint(
                (float)(baseX - (perpendicularX * baseHalfWidth)),
                (float)(baseY - (perpendicularY * baseHalfWidth))),
            new DrawingPoint(
                start.X - (float)(perpendicularX * tailHalfWidth),
                start.Y - (float)(perpendicularY * tailHalfWidth)),
        ];
    }

    private static void DrawStroke(
        IImageProcessingContext context,
        MacStrokeAnnotation stroke,
        Rect bounds,
        double scaleX,
        double scaleY)
    {
        if (stroke.Points.Count < 2)
        {
            return;
        }

        var points = stroke.Points
            .Select(point => ToPixel(point, bounds, scaleX, scaleY))
            .ToArray();
        var width = (float)Math.Max(1, stroke.StrokeWidth * ((scaleX + scaleY) / 2));
        context.DrawLine(ToColor(stroke.Color), width, points);
    }

    private static void DrawText(
        IImageProcessingContext context,
        MacTextAnnotation text,
        Rect bounds,
        double scaleX,
        double scaleY)
    {
        var font = CreateFont((float)Math.Max(8, text.FontSize * scaleY));
        if (font is null)
        {
            return;
        }

        context.DrawText(
            text.Text,
            font,
            ToColor(text.Color),
            ToPixel(text.Position, bounds, scaleX, scaleY));
    }

    private static void DrawNumber(
        IImageProcessingContext context,
        MacNumberAnnotation number,
        Rect bounds,
        double scaleX,
        double scaleY)
    {
        var center = ToPixel(number.Position, bounds, scaleX, scaleY);
        var size = (float)Math.Max(18, number.Size * ((scaleX + scaleY) / 2));
        var rectangle = new SixLabors.ImageSharp.RectangleF(
            center.X - (size / 2),
            center.Y - (size / 2),
            size,
            size);
        context.Fill(
            ToColor(number.Color),
            new SixLabors.ImageSharp.Drawing.EllipsePolygon(
                rectangle.X + (rectangle.Width / 2),
                rectangle.Y + (rectangle.Height / 2),
                rectangle.Width / 2,
                rectangle.Height / 2));
        var font = CreateFont(size * 0.55f, FontStyle.Bold);
        if (font is null)
        {
            return;
        }

        var value = number.Number.ToString();
        var measured = TextMeasurer.MeasureSize(value, new TextOptions(font));
        context.DrawText(
            value,
            font,
            DrawingColor.White,
            new DrawingPoint(
                center.X - (measured.Width / 2),
                center.Y - (measured.Height / 2)));
    }

    private static void ApplyMosaic(
        PixelImage image,
        MacStrokeAnnotation stroke,
        Rect bounds,
        double scaleX,
        double scaleY)
    {
        if (stroke.Points.Count == 0)
        {
            return;
        }

        var blockSize = Math.Max(4, (int)Math.Round(8 * ((scaleX + scaleY) / 2)));
        var radius = Math.Max(blockSize, (int)Math.Round(stroke.StrokeWidth * scaleX));
        foreach (var point in Interpolate(stroke.Points))
        {
            var pixel = ToPixel(point, bounds, scaleX, scaleY);
            for (var y = (int)pixel.Y - radius; y <= pixel.Y + radius; y += blockSize)
            {
                for (var x = (int)pixel.X - radius; x <= pixel.X + radius; x += blockSize)
                {
                    PixelateBlock(image, x, y, blockSize);
                }
            }
        }
    }

    private static IEnumerable<AvaloniaPoint> Interpolate(
        IReadOnlyList<AvaloniaPoint> points)
    {
        yield return points[0];
        for (var index = 1; index < points.Count; index++)
        {
            var start = points[index - 1];
            var end = points[index];
            var steps = Math.Max(1, (int)Math.Ceiling(Math.Max(
                Math.Abs(end.X - start.X),
                Math.Abs(end.Y - start.Y)) / 4));
            for (var step = 1; step <= steps; step++)
            {
                var progress = step / (double)steps;
                yield return new AvaloniaPoint(
                    start.X + ((end.X - start.X) * progress),
                    start.Y + ((end.Y - start.Y) * progress));
            }
        }
    }

    private static void PixelateBlock(PixelImage image, int left, int top, int size)
    {
        var right = Math.Min(image.Width, left + size);
        var bottom = Math.Min(image.Height, top + size);
        left = Math.Max(0, left);
        top = Math.Max(0, top);
        if (left >= right || top >= bottom)
        {
            return;
        }

        long blue = 0, green = 0, red = 0;
        var count = 0;
        for (var y = top; y < bottom; y++)
        {
            for (var x = left; x < right; x++)
            {
                var offset = (y * image.Stride) + (x * 4);
                blue += image.Pixels[offset];
                green += image.Pixels[offset + 1];
                red += image.Pixels[offset + 2];
                count++;
            }
        }

        image.FillRect(
            left,
            top,
            right - left,
            bottom - top,
            (byte)(blue / count),
            (byte)(green / count),
            (byte)(red / count));
    }

    private static DrawingPoint ToPixel(
        AvaloniaPoint point,
        Rect bounds,
        double scaleX,
        double scaleY)
    {
        return new DrawingPoint(
            (float)((point.X - bounds.Left) * scaleX),
            (float)((point.Y - bounds.Top) * scaleY));
    }

    private static DrawingColor ToColor(Avalonia.Media.Color color) =>
        DrawingColor.FromRgba(color.R, color.G, color.B, color.A);

    private static Font? CreateFont(float size, FontStyle style = FontStyle.Regular)
    {
        var preferred = new[] { "PingFang SC", "Helvetica", "Arial", "DejaVu Sans" };
        foreach (var name in preferred)
        {
            if (SystemFonts.TryGet(name, out var family))
            {
                return family.CreateFont(size, style);
            }
        }

        foreach (var family in SystemFonts.Collection.Families)
        {
            return family.CreateFont(size, style);
        }

        return null;
    }
}
