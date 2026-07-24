using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;

namespace Screenshot.App.Editor;

public static class EmojiStickerRenderer
{
    private const int ImageSize = 64;
    private static readonly ConcurrentDictionary<EmojiSticker, ImageSource> Cache = new();
    private static readonly SolidColorBrush FaceBrush = Brush("#FFD54F");
    private static readonly SolidColorBrush FaceBorderBrush = Brush("#F5A623");
    private static readonly SolidColorBrush DarkBrush = Brush("#372B25");
    private static readonly SolidColorBrush RedBrush = Brush("#F04F67");
    private static readonly SolidColorBrush BlueBrush = Brush("#4CB7F5");

    public static ImageSource GetImage(EmojiSticker sticker)
    {
        return Cache.GetOrAdd(sticker, Render);
    }

    private static ImageSource Render(EmojiSticker sticker)
    {
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            switch (sticker)
            {
                case EmojiSticker.ThumbsUp:
                    DrawThumbsUp(context);
                    break;
                case EmojiSticker.Heart:
                    DrawHeartSticker(context);
                    break;
                case EmojiSticker.Party:
                    DrawParty(context);
                    break;
                case EmojiSticker.Star:
                    DrawStarSticker(context);
                    break;
                default:
                    DrawFace(context, sticker);
                    break;
            }
        }

        var bitmap = new RenderTargetBitmap(
            ImageSize,
            ImageSize,
            96,
            96,
            PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static void DrawFace(DrawingContext context, EmojiSticker sticker)
    {
        context.DrawEllipse(
            FaceBrush,
            new Pen(FaceBorderBrush, 2.5),
            new Point(32, 32),
            27,
            27);

        switch (sticker)
        {
            case EmojiSticker.Laugh:
                DrawClosedEye(context, 20, 25, slopesDown: false);
                DrawClosedEye(context, 44, 25, slopesDown: true);
                context.DrawGeometry(DarkBrush, null, CreateOpenMouth());
                context.DrawEllipse(BlueBrush, null, new Point(13, 35), 4, 8);
                context.DrawEllipse(BlueBrush, null, new Point(51, 35), 4, 8);
                break;
            case EmojiSticker.Wink:
                DrawClosedEye(context, 20, 25, slopesDown: false);
                context.DrawEllipse(DarkBrush, null, new Point(43, 25), 3, 4);
                context.DrawGeometry(null, new Pen(DarkBrush, 3), CreateSmile());
                break;
            case EmojiSticker.Love:
                context.DrawGeometry(RedBrush, null, CreateHeart(new Point(20, 24), 7));
                context.DrawGeometry(RedBrush, null, CreateHeart(new Point(44, 24), 7));
                context.DrawGeometry(null, new Pen(DarkBrush, 3), CreateSmile());
                break;
            case EmojiSticker.Cool:
                context.DrawRoundedRectangle(DarkBrush, null, new Rect(12, 20, 17, 11), 3, 3);
                context.DrawRoundedRectangle(DarkBrush, null, new Rect(35, 20, 17, 11), 3, 3);
                context.DrawLine(new Pen(DarkBrush, 3), new Point(29, 24), new Point(35, 24));
                context.DrawGeometry(null, new Pen(DarkBrush, 3), CreateSmile());
                break;
            case EmojiSticker.Cry:
                context.DrawEllipse(DarkBrush, null, new Point(21, 24), 3, 4);
                context.DrawEllipse(DarkBrush, null, new Point(43, 24), 3, 4);
                context.DrawEllipse(BlueBrush, null, new Point(46, 36), 4, 9);
                context.DrawGeometry(null, new Pen(DarkBrush, 3), CreateSadMouth());
                break;
            case EmojiSticker.Angry:
                context.DrawLine(new Pen(DarkBrush, 3), new Point(15, 19), new Point(26, 23));
                context.DrawLine(new Pen(DarkBrush, 3), new Point(49, 19), new Point(38, 23));
                context.DrawEllipse(DarkBrush, null, new Point(21, 27), 3, 3.5);
                context.DrawEllipse(DarkBrush, null, new Point(43, 27), 3, 3.5);
                context.DrawGeometry(null, new Pen(DarkBrush, 3), CreateSadMouth());
                break;
            case EmojiSticker.Surprised:
                context.DrawEllipse(DarkBrush, null, new Point(21, 24), 3.5, 5);
                context.DrawEllipse(DarkBrush, null, new Point(43, 24), 3.5, 5);
                context.DrawEllipse(DarkBrush, null, new Point(32, 43), 6, 8);
                break;
            default:
                context.DrawEllipse(DarkBrush, null, new Point(21, 24), 3, 4);
                context.DrawEllipse(DarkBrush, null, new Point(43, 24), 3, 4);
                context.DrawGeometry(null, new Pen(DarkBrush, 3), CreateSmile());
                break;
        }
    }

    private static void DrawClosedEye(
        DrawingContext context,
        double x,
        double y,
        bool slopesDown)
    {
        var offset = slopesDown ? -3 : 3;
        context.DrawLine(
            new Pen(DarkBrush, 3),
            new Point(x - 5, y + offset),
            new Point(x + 5, y - offset));
    }

    private static void DrawThumbsUp(DrawingContext context)
    {
        var hand = Geometry(
            new Point(17, 29),
            new Point(27, 29),
            new Point(32, 12),
            new Point(38, 13),
            new Point(39, 27),
            new Point(53, 27),
            new Point(55, 33),
            new Point(49, 52),
            new Point(25, 52),
            new Point(17, 45));
        context.DrawGeometry(Brush("#F2B84B"), new Pen(Brush("#A96A18"), 2.5), hand);
        context.DrawRoundedRectangle(
            Brush("#4CB7F5"),
            new Pen(Brush("#2377A8"), 2),
            new Rect(8, 28, 13, 26),
            4,
            4);
    }

    private static void DrawHeartSticker(DrawingContext context)
    {
        context.DrawGeometry(
            RedBrush,
            new Pen(Brush("#C52E49"), 2.5),
            CreateHeart(new Point(32, 32), 25));
        context.DrawEllipse(Brush("#66FFFFFF"), null, new Point(23, 20), 5, 3);
    }

    private static void DrawParty(DrawingContext context)
    {
        var cone = Geometry(new Point(13, 52), new Point(28, 16), new Point(52, 45));
        context.DrawGeometry(Brush("#F04F67"), new Pen(Brush("#9E2740"), 2), cone);
        context.DrawLine(new Pen(Brush("#FFD54F"), 5), new Point(18, 39), new Point(42, 34));
        context.DrawLine(new Pen(Brush("#4CB7F5"), 4), new Point(22, 28), new Point(36, 25));
        context.DrawEllipse(Brush("#4CB7F5"), null, new Point(12, 15), 3, 3);
        context.DrawEllipse(Brush("#73D26C"), null, new Point(49, 14), 3, 3);
        context.DrawEllipse(Brush("#F04F67"), null, new Point(54, 27), 2.5, 2.5);
        context.DrawLine(new Pen(Brush("#9B6BDF"), 3), new Point(31, 9), new Point(34, 16));
        context.DrawLine(new Pen(Brush("#F5A623"), 3), new Point(43, 6), new Point(40, 14));
    }

    private static void DrawStarSticker(DrawingContext context)
    {
        var points = new List<Point>();
        for (var index = 0; index < 10; index++)
        {
            var radius = index % 2 == 0 ? 27 : 12;
            var angle = (-Math.PI / 2) + (index * Math.PI / 5);
            points.Add(new Point(
                32 + (Math.Cos(angle) * radius),
                32 + (Math.Sin(angle) * radius)));
        }

        context.DrawGeometry(
            Brush("#FFD23F"),
            new Pen(Brush("#E39A13"), 2.5),
            Geometry(points.ToArray()));
    }

    private static StreamGeometry CreateSmile()
    {
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(new Point(20, 38), false, false);
            context.BezierTo(new Point(25, 49), new Point(39, 49), new Point(44, 38), true, false);
        }
        geometry.Freeze();
        return geometry;
    }

    private static StreamGeometry CreateSadMouth()
    {
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(new Point(22, 47), false, false);
            context.BezierTo(new Point(27, 38), new Point(37, 38), new Point(42, 47), true, false);
        }
        geometry.Freeze();
        return geometry;
    }

    private static StreamGeometry CreateOpenMouth()
    {
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(new Point(19, 36), true, true);
            context.BezierTo(new Point(23, 54), new Point(41, 54), new Point(45, 36), true, false);
            context.LineTo(new Point(19, 36), true, false);
        }
        geometry.Freeze();
        return geometry;
    }

    private static StreamGeometry CreateHeart(Point center, double radius)
    {
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(new Point(center.X, center.Y + radius), true, true);
            context.BezierTo(
                new Point(center.X - (radius * 1.5), center.Y),
                new Point(center.X - radius, center.Y - radius),
                new Point(center.X, center.Y - (radius * 0.25)),
                true,
                false);
            context.BezierTo(
                new Point(center.X + radius, center.Y - radius),
                new Point(center.X + (radius * 1.5), center.Y),
                new Point(center.X, center.Y + radius),
                true,
                false);
        }
        geometry.Freeze();
        return geometry;
    }

    private static StreamGeometry Geometry(params Point[] points)
    {
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(points[0], true, true);
            context.PolyLineTo(points.Skip(1).ToArray(), true, false);
        }
        geometry.Freeze();
        return geometry;
    }

    private static SolidColorBrush Brush(string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return brush;
    }
}
