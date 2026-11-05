using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Screenshot.App.Core;
using Point = System.Windows.Point;
using Size = System.Windows.Size;
using Brushes = System.Windows.Media.Brushes;
using Pen = System.Windows.Media.Pen;
using FlowDirection = System.Windows.FlowDirection;

namespace Screenshot.App.Watermarks;

public sealed record WatermarkPart(WatermarkElement Element, ImageSource Image);

/// <summary>Immutable capture content; each element uses the same layout for preview and export.</summary>
public sealed class WatermarkComposition
{
    private const double ReferenceFontSize = 64;
    public IReadOnlyList<WatermarkPart> Parts { get; }
    public bool HasContent => Parts.Count > 0;
    public WatermarkSettings Settings { get; }
    public string Text { get; }

    public WatermarkComposition(WatermarkSettings settings, DateTimeOffset timestamp, string? location, BitmapSource? image)
    {
        Settings = settings.Normalize();
        Text = ComposeText(Settings, timestamp, location);
        var parts = new List<WatermarkPart>(4);
        if (image is not null) parts.Add(new(WatermarkElement.Image, image));
        AddText(WatermarkElement.Text, Settings.Text.Trim());
        if (Settings.IncludeTime) AddText(WatermarkElement.Time, timestamp.ToString(Settings.TimeFormat, CultureInfo.InvariantCulture));
        if (Settings.IncludeLocation) AddText(WatermarkElement.Location, LocationText(Settings, location));
        Parts = parts.AsReadOnly();

        void AddText(WatermarkElement element, string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            var text = new FormattedText(value, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface("Microsoft YaHei UI"), ReferenceFontSize, Settings.DarkText ? Brushes.Black : Brushes.White, 1)
            { MaxTextWidth = 1500, Trimming = TextTrimming.None };
            var drawing = new DrawingGroup();
            using (var dc = drawing.Open())
                dc.DrawGeometry(Settings.DarkText ? Brushes.Black : Brushes.White,
                    new Pen(Settings.DarkText ? Brushes.White : Brushes.Black, 2), text.BuildGeometry(new Point()));
            drawing.Freeze();
            var rendered = new DrawingImage(drawing);
            rendered.Freeze();
            if (rendered.Width > 0 && rendered.Height > 0) parts.Add(new(element, rendered));
        }
    }

    private static string LocationText(WatermarkSettings settings, string? location) =>
        (settings.UseSystemLocation && !string.IsNullOrWhiteSpace(location) ? location : settings.ManualLocation).Trim();

    public static string ComposeText(WatermarkSettings settings, DateTimeOffset timestamp, string? location)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(settings.Text)) parts.Add(settings.Text.Trim());
        if (settings.IncludeTime) parts.Add(timestamp.ToString(settings.TimeFormat, CultureInfo.InvariantCulture));
        if (settings.IncludeLocation && LocationText(settings, location) is { Length: > 0 } value) parts.Add(value);
        return string.Join("\n", parts);
    }

    public Rect GetBounds(Size size) => GetBounds(size, Settings);
    public Rect GetBounds(Size size, WatermarkSettings placement)
    {
        var bounds = Rect.Empty;
        foreach (var part in Parts) bounds.Union(GetBounds(part.Element, size, placement));
        return bounds;
    }

    public Rect GetBounds(WatermarkElement element, Size size, WatermarkSettings? settings = null)
    {
        var part = Parts.FirstOrDefault(part => part.Element == element);
        if (part is null || size.Width <= 0 || size.Height <= 0) return Rect.Empty;
        var placement = (settings ?? Settings).GetPlacement(element).Normalize(element);
        var scale = Math.Min(size.Width, size.Height) * placement.Size /
            (element == WatermarkElement.Image ? part.Image.Width : ReferenceFontSize);
        var width = part.Image.Width * scale;
        var height = part.Image.Height * scale;
        var fit = Math.Min(1, Math.Min(size.Width * .96 / width, size.Height * .96 / height));
        width *= fit; height *= fit;
        return new Rect((size.Width - width) * placement.X, (size.Height - height) * placement.Y, width, height);
    }

    public void Draw(DrawingContext context, Size size, WatermarkSettings? placement = null)
    {
        if (size.Width <= 0 || size.Height <= 0) return;
        placement ??= Settings;
        context.PushClip(new RectangleGeometry(new Rect(size)));
        context.PushOpacity(placement.Opacity);
        foreach (var part in Parts)
        {
            var bounds = GetBounds(part.Element, size, placement);
            if (!bounds.IsEmpty) context.DrawImage(part.Image, bounds);
        }
        context.Pop();
        context.Pop();
    }

    public RenderTargetBitmap RenderOn(BitmapSource original)
    {
        var size = new Size(original.PixelWidth, original.PixelHeight);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawImage(original, new Rect(size));
        }
        // Use a separate overlay visual, matching the editor's composition order.
        var overlay = new DrawingVisual();
        using (var dc = overlay.RenderOpen()) Draw(dc, size);
        visual.Children.Add(overlay);
        var result = new RenderTargetBitmap(original.PixelWidth, original.PixelHeight, 96, 96, PixelFormats.Pbgra32);
        result.Render(visual);
        result.Freeze();
        return result;
    }
}
