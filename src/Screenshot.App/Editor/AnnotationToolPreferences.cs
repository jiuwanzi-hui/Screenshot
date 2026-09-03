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
    private static Action<AnnotationToolSetting[]>? _persistenceRequested;

    public static void Configure(
        IEnumerable<AnnotationToolSetting>? settings,
        Action<AnnotationToolSetting[]>? persistenceRequested)
    {
        lock (SyncRoot)
        {
            Colors.Clear();
            Widths.Clear();
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
            }

            _persistenceRequested = persistenceRequested;
        }
    }

    public static AnnotationToolSetting[] Snapshot()
    {
        lock (SyncRoot)
        {
            return Enum.GetValues<EditorTool>()
                .Where(tool => Colors.ContainsKey(tool) || Widths.ContainsKey(tool))
                .Select(tool => new AnnotationToolSetting(
                    tool.ToString(),
                    Colors.TryGetValue(tool, out var color)
                        ? color.ToString(CultureInfo.InvariantCulture)
                        : string.Empty,
                    Widths.TryGetValue(tool, out var width) ? width : 3))
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
