using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Brush = System.Windows.Media.Brush;
using Control = System.Windows.Controls.Control;

namespace Screenshot.App.Infrastructure;

/// <summary>Small shared glyph effects; never applied to a window or image.</summary>
internal static class ClearGlassRelief
{
    private sealed record Original(object RenderingMode);
    private static readonly ConditionalWeakTable<UIElement, Original> Originals = new();
    private static readonly DropShadowEffect LightInk = Create(Colors.Black, 1, 2, .9);
    private static readonly DropShadowEffect DarkInk = Create(Colors.White, .8, .65, .85);

    private static DropShadowEffect Create(System.Windows.Media.Color color, double depth, double blur, double opacity)
    {
        var effect = new DropShadowEffect
        {
            Color = color, ShadowDepth = depth, BlurRadius = blur, Opacity = opacity,
            Direction = 315, RenderingBias = RenderingBias.Performance,
        };
        effect.Freeze();
        return effect;
    }

    internal static void Apply(UIElement element, bool enabled, Brush? ink)
    {
        if (Originals.TryGetValue(element, out var original))
        {
            if (ReferenceEquals(element.Effect, LightInk) || ReferenceEquals(element.Effect, DarkInk))
                element.ClearValue(UIElement.EffectProperty);
            if (original.RenderingMode == DependencyProperty.UnsetValue) element.ClearValue(TextOptions.TextRenderingModeProperty);
            else element.SetValue(TextOptions.TextRenderingModeProperty, original.RenderingMode);
            Originals.Remove(element);
        }
        if (!enabled || element.Effect is not null || ink is not SolidColorBrush solid || solid.Color.A == 0) return;
        Originals.Add(element, new Original(element.ReadLocalValue(TextOptions.TextRenderingModeProperty)));
        var color = solid.Color;
        element.Effect = .2126 * color.R + .7152 * color.G + .0722 * color.B >= 150 ? LightInk : DarkInk;
        TextOptions.SetTextRenderingMode(element, TextRenderingMode.Grayscale);
    }

    internal static void ApplyControlPart(FrameworkElement element, bool enabled)
    {
        switch (element)
        {
            case System.Windows.Shapes.Path path:
                Apply(path, enabled, path.Stroke is SolidColorBrush { Color.A: > 0 } ? path.Stroke : path.Fill);
                break;
            case ScrollViewer { Name: "PART_ContentHost", TemplatedParent: Control input } viewer
                when input is System.Windows.Controls.TextBox or PasswordBox:
                Apply(viewer, enabled, input.Foreground);
                break;
        }
    }
}
