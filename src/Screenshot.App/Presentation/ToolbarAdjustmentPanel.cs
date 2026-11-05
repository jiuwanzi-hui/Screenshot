using System.Windows;
using Size = System.Windows.Size;
using WrapPanel = System.Windows.Controls.WrapPanel;

namespace Screenshot.App.Presentation;

// Share the main button row's width without letting a long options group
// expand that row. Ordinary settings fit side by side; narrow rows wrap.
public sealed class ToolbarAdjustmentPanel : WrapPanel
{
    public static readonly DependencyProperty FillRemainingWidthProperty = DependencyProperty.RegisterAttached(
        "FillRemainingWidth", typeof(bool), typeof(ToolbarAdjustmentPanel),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsParentMeasure));
    public static bool GetFillRemainingWidth(DependencyObject element) => (bool)element.GetValue(FillRemainingWidthProperty);
    public static void SetFillRemainingWidth(DependencyObject element, bool value) => element.SetValue(FillRemainingWidthProperty, value);
    public static readonly DependencyProperty WidthSourceProperty = DependencyProperty.Register(
        nameof(WidthSource), typeof(FrameworkElement), typeof(ToolbarAdjustmentPanel),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsMeasure, OnWidthSourceChanged));

    public static readonly DependencyProperty ReservedWidthSourceProperty = DependencyProperty.Register(
        nameof(ReservedWidthSource), typeof(FrameworkElement), typeof(ToolbarAdjustmentPanel),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsMeasure, OnWidthSourceChanged));

    public FrameworkElement? ReservedWidthSource
    {
        get => (FrameworkElement?)GetValue(ReservedWidthSourceProperty);
        set => SetValue(ReservedWidthSourceProperty, value);
    }

    public FrameworkElement? WidthSource
    {
        get => (FrameworkElement?)GetValue(WidthSourceProperty);
        set => SetValue(WidthSourceProperty, value);
    }

    private static void OnWidthSourceChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        var panel = (ToolbarAdjustmentPanel)target;
        if (args.OldValue is FrameworkElement previous) previous.SizeChanged -= panel.OnSourceSizeChanged;
        if (args.NewValue is FrameworkElement current) current.SizeChanged += panel.OnSourceSizeChanged;
    }

    private void OnSourceSizeChanged(object sender, SizeChangedEventArgs args) => InvalidateMeasure();

    protected override Size MeasureOverride(Size constraint)
    {
        var width = WidthSource?.DesiredSize.Width ?? 0;
        if (width > 0 && double.IsFinite(width)) constraint.Width = Math.Min(constraint.Width, width);
        var flexible = InternalChildren.OfType<FrameworkElement>()
            .FirstOrDefault(child => child.Visibility != Visibility.Collapsed && GetFillRemainingWidth(child));
        if (flexible is not null)
        {
            var available = double.IsFinite(constraint.Width) ? constraint.Width : 600;
            if (ReservedWidthSource is { } reserved)
            {
                reserved.Measure(constraint);
                available -= reserved.DesiredSize.Width;
            }
            foreach (UIElement child in InternalChildren)
            {
                if (ReferenceEquals(child, flexible)) continue;
                child.Measure(constraint);
                available -= child.DesiredSize.Width;
            }
            flexible.MaxWidth = Math.Max(0, available - flexible.Margin.Left - flexible.Margin.Right);
        }
        return base.MeasureOverride(constraint);
    }
}
