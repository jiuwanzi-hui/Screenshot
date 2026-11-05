namespace Screenshot.App.Core;

public enum RealtimeTranslationScope { CurrentWindow, Region }
public enum RealtimeTranslationDisplay { Overlay, Subtitle }

public sealed record RealtimeTranslationSettings
{
    public RealtimeTranslationScope Scope { get; init; } = RealtimeTranslationScope.Region;
    public RealtimeTranslationDisplay Display { get; init; }
    public double FontSize { get; init; } = 18;
    public double BackgroundOpacity { get; init; } = .9;
    public double SubtitleX { get; init; } = .5;
    public double SubtitleY { get; init; } = .85;
    public string Glossary { get; init; } = string.Empty;
    public string GlossaryLanguage { get; init; } = "zh-Hans";

    public RealtimeTranslationSettings Normalize() => this with
    {
        Scope = Enum.IsDefined(Scope) ? Scope : RealtimeTranslationScope.Region,
        Display = Enum.IsDefined(Display) ? Display : RealtimeTranslationDisplay.Overlay,
        FontSize = Clamp(FontSize, 12, 48, 18),
        BackgroundOpacity = Clamp(BackgroundOpacity, .35, 1, .9),
        SubtitleX = Clamp(SubtitleX, 0, 1, .5),
        SubtitleY = Clamp(SubtitleY, 0, 1, .85),
        Glossary = (Glossary ?? string.Empty)[..Math.Min(16000, Glossary?.Length ?? 0)],
        GlossaryLanguage = string.IsNullOrWhiteSpace(GlossaryLanguage) ? "zh-Hans" : GlossaryLanguage.Trim(),
    };

    private static double Clamp(double value, double min, double max, double fallback) =>
        double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;
}
