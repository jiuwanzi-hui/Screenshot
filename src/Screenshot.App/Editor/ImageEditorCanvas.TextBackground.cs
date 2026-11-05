using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TextBox = System.Windows.Controls.TextBox;
using Color = System.Windows.Media.Color;
using Control = System.Windows.Controls.Control;
using Brushes = System.Windows.Media.Brushes;

namespace Screenshot.App.Editor;

public partial class ImageEditorCanvas
{
    private TextAnnotationBackground? _activeTextBackground;

    internal bool HasSelectedTextAnnotation => HasSelectedAnnotation &&
        _document.Annotations[_selectedAnnotationIndex] is TextAnnotation;

    internal bool TextBackgroundEnabled => _activeTextInput is not null ? _activeTextBackground is not null :
        HasSelectedAnnotation && _document.Annotations[_selectedAnnotationIndex] is TextAnnotation text
            ? text.Background is not null : AnnotationToolPreferences.TextBackground;

    internal void PrepareTextBackgroundOptions()
    {
        if (_activeTextInput is not { } input || string.IsNullOrWhiteSpace(input.Text)) return;
        var index = _editingTextAnnotationIndex >= 0 ? _editingTextAnnotationIndex : _document.Annotations.Count;
        CommitPendingText();
        if (index < _document.Annotations.Count)
        {
            _selectedAnnotationIndex = index;
            RebuildCanvas();
            AnnotationSelectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    internal void SetTextBackground(bool enabled)
    {
        AnnotationToolPreferences.SetTextBackground(enabled, _strokeWidth);
        var background = enabled ? ResolveTextBackground() : null;
        if (_activeTextInput is not null)
        {
            _activeTextBackground = background;
            StyleActiveTextInput();
        }
        else if (HasSelectedAnnotation && _document.Annotations[_selectedAnnotationIndex] is TextAnnotation text)
        {
            ReplaceSelectedAnnotation(text, text with { Background = background });
        }
    }

    private TextAnnotationBackground ResolveTextBackground()
    {
        var fallback = Color.FromRgb(53, 51, 60);
        var input = ResolveTextColor("AppInputBackgroundBrush", fallback);
        var surface = ResolveTextColor("AppPopupBackgroundBrush", fallback);
        // Glass input brushes assume a themed panel underneath. An annotation
        // sits directly on arbitrary screenshot pixels instead; flatten that
        // panel into its fill so "with background" stays visible after commit
        // and export, independently of the captured page's colours.
        var alpha = input.A / 255d;
        var fill = Color.FromRgb(
            (byte)Math.Round(input.R * alpha + surface.R * (1 - alpha)),
            (byte)Math.Round(input.G * alpha + surface.G * (1 - alpha)),
            (byte)Math.Round(input.B * alpha + surface.B * (1 - alpha)));
        return new(fill, ResolveTextColor("AppControlEdgeBrush", Color.FromRgb(143, 139, 151)));
    }

    private Color ResolveTextColor(string resource, Color fallback) => TryFindResource(resource) switch
    {
        SolidColorBrush brush => Color.FromArgb((byte)Math.Round(brush.Color.A * brush.Opacity),
            brush.Color.R, brush.Color.G, brush.Color.B),
        GradientBrush { GradientStops.Count: > 0 } gradient => Color.FromArgb(
            (byte)Math.Round(gradient.GradientStops[0].Color.A * gradient.Opacity),
            gradient.GradientStops[0].Color.R, gradient.GradientStops[0].Color.G, gradient.GradientStops[0].Color.B),
        _ => fallback,
    };

    private void ConfigureActiveTextInput(TextAnnotationBackground? background)
    {
        _activeTextBackground = background;
        if (_activeTextInput is not { } input) return;
        input.FontFamily = TextAnnotationLayout.Font;
        // A lightweight template keeps the editing and committed background
        // identical; the global focus template would otherwise add a fixed plate.
        var border = new FrameworkElementFactory(typeof(Border), "TextBackgroundBorder");
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(TextAnnotationLayout.CornerRadius(input.FontSize)));
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
        border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
        var host = new FrameworkElementFactory(typeof(ScrollViewer), "PART_ContentHost");
        host.SetValue(MarginProperty, new TemplateBindingExtension(Control.PaddingProperty));
        host.SetValue(FocusableProperty, false);
        border.AppendChild(host);
        input.Template = new ControlTemplate(typeof(TextBox)) { VisualTree = border };
        input.TextChanged += OnActiveTextSizeChanged;
        StyleActiveTextInput();
    }

    private void StyleActiveTextInput()
    {
        if (_activeTextInput is not { } input) return;
        input.Background = _activeTextBackground is { } background ? TextAnnotationLayout.Brush(background.Fill) : Brushes.Transparent;
        input.BorderBrush = TextAnnotationLayout.Brush(_activeTextBackground?.Border ?? _selectedColor);
        input.Padding = _activeTextBackground is not null ? TextAnnotationLayout.Padding(input.FontSize) : new Thickness(0);
        if (input.Template.FindName("TextBackgroundBorder", input) is Border border)
            border.CornerRadius = new CornerRadius(TextAnnotationLayout.CornerRadius(input.FontSize));
        ResizeActiveTextInput();
    }

    private void OnActiveTextSizeChanged(object sender, TextChangedEventArgs e) => ResizeActiveTextInput();

    private void ResizeActiveTextInput()
    {
        if (_activeTextInput is not { } input) return;
        var size = TextAnnotationLayout.Measure(input.Text, input.FontSize, _activeTextBackground is not null);
        input.MinHeight = 0;
        input.Width = Math.Max(24, size.Width + 2); // caret room, independent of the old 420 px editor width
        input.Height = Math.Max(input.FontSize + 2, size.Height + 2);
    }
}
