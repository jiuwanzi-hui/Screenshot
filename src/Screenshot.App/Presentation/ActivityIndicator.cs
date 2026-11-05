using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Point = System.Windows.Point;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Pen = System.Windows.Media.Pen;

namespace Screenshot.App.Presentation;

/// <summary>A small compositor animation; no timer or idle rendering subscription.</summary>
public sealed class ActivityIndicator : FrameworkElement
{
    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.Register(
        nameof(IsActive), typeof(bool), typeof(ActivityIndicator),
        new PropertyMetadata(false, (sender, _) => ((ActivityIndicator)sender).UpdateActivity()));
    private readonly RotateTransform _rotation = new();
    private static readonly Geometry Arc = Geometry.Parse("M 7,1 A 6,6 0 1 1 1,7").GetAsFrozen() as Geometry
        ?? Geometry.Empty;

    public ActivityIndicator()
    {
        Visibility = Visibility.Collapsed;
        IsHitTestVisible = false;
        RenderTransformOrigin = new Point(0.5, 0.5);
        RenderTransform = _rotation;
        Loaded += (_, _) => UpdateAnimation();
        Unloaded += (_, _) => StopAnimation();
        IsVisibleChanged += (_, _) => UpdateAnimation();
    }

    public bool IsActive
    {
        get => (bool)GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var brush = TryFindResource("AppTextSecondaryBrush") as Brush ?? Brushes.Teal;
        drawingContext.DrawGeometry(null, new Pen(brush, 2) { StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round }, Arc);
    }

    private void UpdateActivity()
    {
        Visibility = IsActive ? Visibility.Visible : Visibility.Collapsed;
        UpdateAnimation();
        if (IsActive) UiTransitions.Reveal(this);
    }

    private void UpdateAnimation()
    {
        StopAnimation();
        if (!IsActive || !IsVisible || !IsLoaded || !UiTransitions.IsEnabled) return;
        var animation = new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(900))
        {
            RepeatBehavior = RepeatBehavior.Forever,
        };
        Timeline.SetDesiredFrameRate(animation, 30);
        _rotation.BeginAnimation(RotateTransform.AngleProperty, animation);
    }

    private void StopAnimation() => _rotation.BeginAnimation(RotateTransform.AngleProperty, null);
}
