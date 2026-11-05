using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Panel = System.Windows.Controls.Panel;

namespace Screenshot.App.Infrastructure;

/// <summary>Readable ink on clear areas; controls retain their authored ink.</summary>
internal static class ClearGlassText
{
    private sealed record InkState(object LocalInk, string? ResourceKey);
    private static readonly ConditionalWeakTable<TextBlock, InkState> Ink = new();
    private static readonly string[] InkKeys = ["AppTextPrimaryBrush", "AppTextSecondaryBrush", "AppMutedTextBrush",
        "AppControlForegroundBrush", "AppAccentForegroundBrush", "ImageEditorTitleBrush", "ImageEditorMutedTextBrush",
        "AppNavigationForegroundBrush", "AppCaptionForegroundBrush", "AppAccentTextBrush", "AppOnAccentBrush"];
    internal static void Apply(TextBlock text, bool enabled, bool protectContrast = true)
    {
        // Restore before evaluating the current navigation/selection surface.
        if (Ink.TryGetValue(text, out var original))
        {
            if (ReferenceEquals(text.Foreground, Brushes.White))
            {
                if (original.LocalInk == DependencyProperty.UnsetValue) text.ClearValue(TextBlock.ForegroundProperty);
                else if (original.ResourceKey is not null) text.SetResourceReference(TextBlock.ForegroundProperty, original.ResourceKey);
                else text.SetValue(TextBlock.ForegroundProperty, original.LocalInk);
            }
            Ink.Remove(text);
        }
        ClearGlassRelief.Apply(text, false, text.Foreground);
        if (!enabled || text.Effect is not null) return;
        var key = InkKeys.FirstOrDefault(key => ReferenceEquals(text.TryFindResource(key), text.Foreground));
        var localInk = text.ReadLocalValue(TextBlock.ForegroundProperty);
        var canProtect = key is not null || (text.Foreground is SolidColorBrush ink && ink.Color.R < 128 &&
            ink.Color.G < 128 && ink.Color.B < 128 &&
            (localInk == DependencyProperty.UnsetValue || localInk is SolidColorBrush));
        if (protectContrast && canProtect && !HasReadingSurface(text))
        {
            Ink.Add(text, new InkState(localInk, key));
            text.Foreground = Brushes.White;
        }
        // Relief is independent of contrast protection: dark input/button
        // lettering retains its colour and receives a fine reflected edge.
        ClearGlassRelief.Apply(text, true, text.Foreground);
    }

    private static bool HasReadingSurface(DependencyObject child)
    {
        if (child is not TextBlock { Foreground: SolidColorBrush ink }) return false;
        // Account for the real stack of translucent surfaces. An input over
        // the window tint can protect dark ink without an opaque white fill.
        // Count painted borders/panels only, not their templated control again.
        var low = new double[3];
        var high = new double[3];
        double remainingLow = 1, remainingHigh = 1;
        for (var parent = Parent(child); parent is not null && parent is not Window; parent = Parent(parent))
        {
            var brush = parent switch
            {
                Border border => border.Background,
                Panel panel => panel.Background,
                // The selected item sits beside the ToggleButton, not inside
                // its painted border, so its own background is a real layer.
                System.Windows.Controls.ComboBox combo => combo.Background,
                System.Windows.Controls.Control control when Presentation.SelectionMotion.GetIsHosted(control) &&
                    (control is System.Windows.Controls.Primitives.ToggleButton { IsChecked: true } or ListBoxItem { IsSelected: true })
                    => Presentation.SelectionMotion.GetFill(control),
                _ => null,
            };
            if (brush is null) continue;
            if (brush is ImageBrush) return true;
            var colors = brush switch
            {
                SolidColorBrush solid => new[] { solid.Color },
                GradientBrush gradient => gradient.GradientStops.Select(stop => stop.Color).ToArray(),
                _ => [],
            };
            if (colors.Length == 0) continue;
            var alphaMin = colors.Min(color => color.A / 255d) * brush.Opacity;
            var alphaMax = colors.Max(color => color.A / 255d) * brush.Opacity;
            for (var channel = 0; channel < 3; channel++)
            {
                double Component(Color c) => (channel == 0 ? c.R : channel == 1 ? c.G : c.B) / 255d * c.A / 255d * brush.Opacity;
                low[channel] += remainingLow * colors.Min(Component);
                high[channel] += remainingHigh * colors.Max(Component);
            }
            remainingLow *= 1 - alphaMax;
            remainingHigh *= 1 - alphaMin;
        }
        static double Channel(double c) => c <= .04045 ? c / 12.92 : Math.Pow(c / 1.055 + .055 / 1.055, 2.4);
        static double Luminance(double r, double g, double b) => .2126 * Channel(r) + .7152 * Channel(g) + .0722 * Channel(b);
        var minimum = Luminance(low[0], low[1], low[2]);
        var maximum = Luminance(Math.Min(1, high[0] + remainingHigh), Math.Min(1, high[1] + remainingHigh), Math.Min(1, high[2] + remainingHigh));
        var foreground = Luminance(ink.Color.R / 255d, ink.Color.G / 255d, ink.Color.B / 255d);
        return foreground < minimum ? (minimum + .05) / (foreground + .05) >= 4.5
            : foreground > maximum && (foreground + .05) / (maximum + .05) >= 4.5;
    }

    private static DependencyObject? Parent(DependencyObject child) =>
        (child is Visual ? VisualTreeHelper.GetParent(child) : null) ?? LogicalTreeHelper.GetParent(child);
}
