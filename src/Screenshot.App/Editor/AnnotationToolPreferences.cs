using System.Globalization;
using WpfColor = System.Windows.Media.Color;
using WpfColorConverter = System.Windows.Media.ColorConverter;
using Screenshot.App.Core;

namespace Screenshot.App.Editor;

/// <summary>
/// Keeps the active stroke color and width independent for each annotation
/// tool while leaving the recent-color palette shared by all tools.
/// </summary>
internal static class AnnotationToolPreferences
{
    private static readonly object SyncRoot = new();
    private static readonly Dictionary<EditorTool, WpfColor> Colors = [];
    private static readonly Dictionary<EditorTool, double> Widths = [];
    private static readonly Dictionary<EditorTool, EditorTool> Variants = [];
    private static bool _textBackground;
    private static Action<AnnotationToolSetting[]>? _persistenceRequested;

    public static void Configure(
        IEnumerable<AnnotationToolSetting>? settings,
        Action<AnnotationToolSetting[]>? persistenceRequested)
    {
        lock (SyncRoot)
        {
            Colors.Clear();
            Widths.Clear();
            Variants.Clear();
            _textBackground = false;
            foreach (var setting in settings ?? [])
            {
                if (!Enum.TryParse<EditorTool>(setting.Tool, true, out var tool) ||
                    !double.IsFinite(setting.StrokeWidth) ||
                    setting.StrokeWidth < 1)
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(setting.Color) &&
                    TryParseColor(setting.Color, out var color))
                {
                    Colors[tool] = color;
                }
                Widths[tool] = Math.Clamp(setting.StrokeWidth, 1, 24);
                if (tool == EditorTool.Text) _textBackground = setting.TextBackground;
                if (Enum.TryParse<EditorTool>(setting.Variant, out var variant) &&
                    IsVariant(tool, variant))
                {
                    Variants[tool] = variant;
                }
            }

            _persistenceRequested = persistenceRequested;
        }
    }

    public static AnnotationToolSetting[] Snapshot()
    {
        lock (SyncRoot)
        {
            return Enum.GetValues<EditorTool>()
                .Where(tool => Colors.ContainsKey(tool) || Widths.ContainsKey(tool) || Variants.ContainsKey(tool) ||
                    (tool == EditorTool.Text && _textBackground))
                .Select(tool => new AnnotationToolSetting(
                    tool.ToString(),
                    Colors.TryGetValue(tool, out var color)
                        ? color.ToString(CultureInfo.InvariantCulture)
                        : string.Empty,
                    Widths.TryGetValue(tool, out var width) ? width : 3,
                    Variants.TryGetValue(tool, out var variant) ? variant.ToString() : null,
                    tool == EditorTool.Text && _textBackground))
                .ToArray();
        }
    }

    public static WpfColor GetColor(EditorTool tool, WpfColor fallback)
    {
        lock (SyncRoot)
        {
            return Colors.TryGetValue(tool, out var color) ? color : fallback;
        }
    }

    internal static bool TextBackground
    {
        get { lock (SyncRoot) return _textBackground; }
    }

    internal static void SetTextBackground(bool enabled, double? fallbackTextWidth = null)
    {
        Action<AnnotationToolSetting[]>? persistence;
        lock (SyncRoot)
        {
            if (_textBackground == enabled) return;
            _textBackground = enabled;
            if (!Widths.ContainsKey(EditorTool.Text) && fallbackTextWidth is { } width && double.IsFinite(width))
                Widths[EditorTool.Text] = Math.Clamp(width, 1, 24);
            persistence = _persistenceRequested;
        }
        persistence?.Invoke(Snapshot());
    }

    public static EditorTool GetVariant(EditorTool tool)
    {
        lock (SyncRoot)
        {
            return Variants.GetValueOrDefault(tool, tool);
        }
    }

    public static void SetVariant(EditorTool tool, EditorTool variant)
    {
        if (!IsVariant(tool, variant))
            throw new ArgumentOutOfRangeException(nameof(variant));

        Action<AnnotationToolSetting[]>? persistence;
        lock (SyncRoot)
        {
            if (Variants.TryGetValue(tool, out var current) && current == variant)
                return;
            Variants[tool] = variant;
            persistence = _persistenceRequested;
        }
        persistence?.Invoke(Snapshot());
    }

    private static bool IsVariant(EditorTool tool, EditorTool variant) => tool switch
    {
        EditorTool.Mosaic => variant is EditorTool.Mosaic or EditorTool.RectangleMosaic,
        EditorTool.DoubleArrow => variant is EditorTool.DoubleArrow or EditorTool.HollowDoubleArrow,
        EditorTool.Line => variant is EditorTool.Line or EditorTool.WavyLine,
        _ => false,
    };

    public static double GetWidth(EditorTool tool, double fallback)
    {
        lock (SyncRoot)
        {
            return Widths.TryGetValue(tool, out var width) ? width : fallback;
        }
    }

    public static void SetColor(EditorTool tool, WpfColor color)
    {
        Action<AnnotationToolSetting[]>? persistence;
        lock (SyncRoot)
        {
            Colors[tool] = color;
            persistence = _persistenceRequested;
        }

        persistence?.Invoke(Snapshot());
    }

    public static void SetWidth(EditorTool tool, double width)
    {
        Action<AnnotationToolSetting[]>? persistence;
        lock (SyncRoot)
        {
            Widths[tool] = width;
            persistence = _persistenceRequested;
        }

        persistence?.Invoke(Snapshot());
    }

    private static bool TryParseColor(string value, out WpfColor color)
    {
        try
        {
            if (WpfColorConverter.ConvertFromString(value) is WpfColor parsed)
            {
                color = parsed;
                return true;
            }
        }
        catch (FormatException)
        {
        }

        color = default;
        return false;
    }
}
