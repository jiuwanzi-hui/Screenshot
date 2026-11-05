using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;

namespace Screenshot.App.Presentation;

// The former Grid + highlight Border + LiquidGlassLens painted only decoration.
// Draw those layers on the existing Border instead of creating three visuals,
// layout slots and resource consumers per button. Button input/templates stay intact.
public sealed class ToolbarButtonSurface : Border
{
    private Drawing? _reflection;
    private System.Windows.Size _reflectionSize;
    private Brush? _lastHighlight, _lastReflection;
    private CornerRadius _lastRadius;
    public static readonly DependencyProperty LensVisibilityProperty = DependencyProperty.Register(
        nameof(LensVisibility), typeof(Visibility), typeof(ToolbarButtonSurface),
        new FrameworkPropertyMetadata(Visibility.Collapsed, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty IsEmphasizedProperty = DependencyProperty.Register(
        nameof(IsEmphasized), typeof(bool), typeof(ToolbarButtonSurface),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty HighlightBrushProperty = DependencyProperty.Register(
        nameof(HighlightBrush), typeof(Brush), typeof(ToolbarButtonSurface),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ReflectionBrushProperty = DependencyProperty.Register(
        nameof(ReflectionBrush), typeof(Brush), typeof(ToolbarButtonSurface),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ReflectionRadiusProperty = DependencyProperty.Register(
        nameof(ReflectionRadius), typeof(CornerRadius), typeof(ToolbarButtonSurface),
        new FrameworkPropertyMetadata(new CornerRadius(8), FrameworkPropertyMetadataOptions.AffectsRender));

    public Visibility LensVisibility { get => (Visibility)GetValue(LensVisibilityProperty); set => SetValue(LensVisibilityProperty, value); }
    public bool IsEmphasized { get => (bool)GetValue(IsEmphasizedProperty); set => SetValue(IsEmphasizedProperty, value); }
    public Brush? HighlightBrush { get => (Brush?)GetValue(HighlightBrushProperty); set => SetValue(HighlightBrushProperty, value); }
    public Brush? ReflectionBrush { get => (Brush?)GetValue(ReflectionBrushProperty); set => SetValue(ReflectionBrushProperty, value); }
    public CornerRadius ReflectionRadius { get => (CornerRadius)GetValue(ReflectionRadiusProperty); set => SetValue(ReflectionRadiusProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if (LensVisibility != Visibility.Visible) return;
        var width = ActualWidth - BorderThickness.Left - BorderThickness.Right;
        var height = ActualHeight - BorderThickness.Top - BorderThickness.Bottom;
        if (width <= 3 || height <= 3) return;
        dc.PushTransform(new TranslateTransform(BorderThickness.Left, BorderThickness.Top));
        var reflectionSize = new System.Windows.Size(width - 2, height - 2);
        if (_reflection is null || _reflectionSize != reflectionSize ||
            !ReferenceEquals(_lastHighlight, HighlightBrush) || !ReferenceEquals(_lastReflection, ReflectionBrush) ||
            _lastRadius != ReflectionRadius)
        {
            // Reuse WPF's exact border geometry, including translucent gradient
            // edges. Retain only the drawing, never the temporary layout tree.
            var surface = new Border { BorderBrush = HighlightBrush, Background = ReflectionBrush,
                BorderThickness = new Thickness(1), CornerRadius = ReflectionRadius };
            surface.Measure(reflectionSize);
            surface.Arrange(new Rect(reflectionSize));
            surface.UpdateLayout();
            _reflection = VisualTreeHelper.GetDrawing(surface);
            _reflectionSize = reflectionSize;
            _lastHighlight = HighlightBrush;
            _lastReflection = ReflectionBrush;
            _lastRadius = ReflectionRadius;
        }
        dc.PushTransform(new TranslateTransform(1, 1));
        if (_reflection is not null) dc.DrawDrawing(_reflection);
        dc.Pop();
        dc.PushOpacity(IsEmphasized ? 1 : .82);
        LiquidGlassLens.DrawLip(dc, new System.Windows.Size(width, height), 11);
        dc.Pop();
        dc.Pop();
    }
}
