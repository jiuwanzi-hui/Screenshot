namespace Screenshot.App.Core;

public enum WatermarkElement { Image, Text, Time, Location }
public enum WatermarkLocationDisplay { PlaceName, Coordinates }

public sealed record WatermarkPlacement
{
    public double X { get; init; } = .96;
    public double Y { get; init; } = .96;
    // Image: fraction of the shorter canvas edge. Text: font size / shorter edge.
    public double Size { get; init; }

    internal WatermarkPlacement Normalize(WatermarkElement element) => this with
    {
        X = double.IsFinite(X) ? Math.Clamp(X, 0, 1) : .96,
        Y = double.IsFinite(Y) ? Math.Clamp(Y, 0, 1) : .96,
        Size = double.IsFinite(Size) ? Math.Clamp(Size, element == WatermarkElement.Image ? .03 : .012,
            element == WatermarkElement.Image ? .95 : .3) : Default(element).Size,
    };

    public static WatermarkPlacement Default(WatermarkElement element) => element switch
    {
        WatermarkElement.Image => new() { X = .96, Y = .08, Size = .25 },
        WatermarkElement.Text => new() { X = .96, Y = .76, Size = .058 },
        WatermarkElement.Time => new() { X = .96, Y = .86, Size = .045 },
        _ => new() { X = .96, Y = .96, Size = .045 },
    };
}

public sealed record WatermarkImage
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
}

public sealed record WatermarkSettings
{
    public bool AutoApply { get; init; }
    public string Text { get; init; } = "SnapCut";
    public bool IncludeTime { get; init; }
    public string TimeFormat { get; init; } = "yyyy-MM-dd HH:mm:ss";
    public bool IncludeLocation { get; init; }
    public bool UseSystemLocation { get; init; } = true;
    // Keep existing coordinate-only presets offline until the user selects place names.
    public WatermarkLocationDisplay LocationDisplay { get; init; } = WatermarkLocationDisplay.Coordinates;
    public string ManualLocation { get; init; } = string.Empty;
    public WatermarkImage[] Images { get; init; } = [];
    public string SelectedImageId { get; init; } = string.Empty;
    // Legacy compound-layout fields remain readable; independent placements are
    // initialized once by Normalize and saved separately thereafter.
    public double X { get; init; } = .96;
    public double Y { get; init; } = .96;
    public double WidthRatio { get; init; } = .28;
    public WatermarkPlacement? ImagePlacement { get; init; }
    public WatermarkPlacement? TextPlacement { get; init; }
    public WatermarkPlacement? TimePlacement { get; init; }
    public WatermarkPlacement? LocationPlacement { get; init; }
    public double Opacity { get; init; } = .75;
    public bool DarkText { get; init; }

    public static readonly string[] TimeFormats = ["yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm", "yyyy-MM-dd", "yyyy/MM/dd HH:mm:ss"];

    public WatermarkSettings Normalize()
    {
        var images = (Images ?? []).Where(image => image is not null && IsValidImageId(image.Id))
            .DistinctBy(image => image.Id).Take(3)
            .Select(image => image with { Name = Limit(image.Name, 80) }).ToArray();
        return this with
        {
            Text = Limit(Text, 300), ManualLocation = Limit(ManualLocation, 120), Images = images,
            SelectedImageId = images.Any(image => image.Id == SelectedImageId) ? SelectedImageId : string.Empty,
            TimeFormat = TimeFormats.Contains(TimeFormat) ? TimeFormat : TimeFormats[0],
            X = Clamp(X, 0, 1, .96), Y = Clamp(Y, 0, 1, .96),
            WidthRatio = Clamp(WidthRatio, .05, .9, .28), Opacity = Clamp(Opacity, .1, 1, .75),
            LocationDisplay = Enum.IsDefined(LocationDisplay) ? LocationDisplay : WatermarkLocationDisplay.Coordinates,
            ImagePlacement = GetPlacement(WatermarkElement.Image).Normalize(WatermarkElement.Image),
            TextPlacement = GetPlacement(WatermarkElement.Text).Normalize(WatermarkElement.Text),
            TimePlacement = GetPlacement(WatermarkElement.Time).Normalize(WatermarkElement.Time),
            LocationPlacement = GetPlacement(WatermarkElement.Location).Normalize(WatermarkElement.Location),
        };
    }

    public WatermarkPlacement GetPlacement(WatermarkElement element) => (element switch
    {
        WatermarkElement.Image => ImagePlacement,
        WatermarkElement.Text => TextPlacement,
        WatermarkElement.Time => TimePlacement,
        _ => LocationPlacement,
    }) ?? WatermarkPlacement.Default(element);

    public WatermarkSettings WithPlacement(WatermarkElement element, WatermarkPlacement placement) => element switch
    {
        WatermarkElement.Image => this with { ImagePlacement = placement.Normalize(element) },
        WatermarkElement.Text => this with { TextPlacement = placement.Normalize(element) },
        WatermarkElement.Time => this with { TimePlacement = placement.Normalize(element) },
        _ => this with { LocationPlacement = placement.Normalize(element) },
    };

    public static bool IsValidImageId(string? id) => id?.Length == 32 && Guid.TryParseExact(id, "N", out _);
    private static double Clamp(double value, double min, double max, double fallback) =>
        double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;
    private static string Limit(string? value, int length) => (value ?? string.Empty)[..Math.Min(value?.Length ?? 0, length)];
}
