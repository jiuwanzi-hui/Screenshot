using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Border = System.Windows.Controls.Border;

namespace Screenshot.App.Presentation;

/// <summary>A compositor-only progress accent which has no idle timer.</summary>
public sealed class ActivityRail : Grid
{
    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.Register(
        nameof(IsActive), typeof(bool), typeof(ActivityRail),
        new PropertyMetadata(false, (owner, _) => ((ActivityRail)owner).UpdateActivity()));
    private readonly TranslateTransform _offset = new();
    private readonly Border _segment;

    public ActivityRail()
    {
        Height = 3;
        ClipToBounds = true;
        IsHitTestVisible = false;
        Visibility = Visibility.Collapsed;
        SetResourceReference(BackgroundProperty, "AppSubtleBorderBrush");
        _segment = new Border { Width = 64, CornerRadius = new CornerRadius(1.5),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left, RenderTransform = _offset };
        _segment.SetResourceReference(Border.BackgroundProperty, "SelectionBorderBrush");
        Children.Add(_segment);
        Loaded += (_, _) => UpdateAnimation();
        Unloaded += (_, _) => Stop();
        IsVisibleChanged += (_, _) => UpdateAnimation();
        SizeChanged += (_, _) => UpdateAnimation();
    }

    public bool IsActive
    {
        get => (bool)GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    private void UpdateActivity()
    {
        Visibility = IsActive ? Visibility.Visible : Visibility.Collapsed;
        UpdateAnimation();
    }

    private void UpdateAnimation()
    {
        Stop();
        if (!IsActive || !IsLoaded || !IsVisible || !UiTransitions.IsEnabled) return;
        var motion = new DoubleAnimation(-64, ActualWidth, TimeSpan.FromMilliseconds(1100))
        { RepeatBehavior = RepeatBehavior.Forever };
        Timeline.SetDesiredFrameRate(motion, 30);
        _offset.BeginAnimation(TranslateTransform.XProperty, motion);
    }

    private void Stop()
    {
        _offset.BeginAnimation(TranslateTransform.XProperty, null);
        _offset.X = 0;
    }
}
