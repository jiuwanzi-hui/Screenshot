using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using RadioButton = System.Windows.Controls.RadioButton;

namespace Screenshot.App.Presentation;

internal static class UiTransitions
{
    internal static bool IsEnabled => SystemParameters.ClientAreaAnimation &&
        (RenderCapability.Tier >> 16) > 0 && !SystemParameters.IsRemoteSession;

    private static readonly DependencyProperty RevealOffsetProperty = DependencyProperty.RegisterAttached(
        "RevealOffset", typeof(TranslateTransform), typeof(UiTransitions));
    private static readonly DependencyProperty SelectionOffsetProperty = DependencyProperty.RegisterAttached(
        "SelectionOffset", typeof(TranslateTransform), typeof(UiTransitions));

    internal static void NudgeSelection(FrameworkElement surface, bool horizontal)
    {
        // A small page entrance, independent of the travelling selection plate.
        var offset = surface.GetValue(SelectionOffsetProperty) as TranslateTransform;
        if (offset is null)
        {
            if (!surface.RenderTransform.Value.IsIdentity) return;
            offset = new TranslateTransform();
            surface.SetValue(SelectionOffsetProperty, offset);
            surface.RenderTransform = offset;
        }
        else if (!ReferenceEquals(surface.RenderTransform, offset)) return;
        offset.BeginAnimation(TranslateTransform.XProperty, null);
        offset.BeginAnimation(TranslateTransform.YProperty, null);
        offset.BeginAnimation(horizontal ? TranslateTransform.XProperty : TranslateTransform.YProperty,
            new DoubleAnimation(horizontal ? -3 : 2, 0, TimeSpan.FromMilliseconds(140))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop });
    }

    internal static void RevealPage(FrameworkElement surface)
    {
        Finish(surface);
        if (!IsEnabled) return;
        surface.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(.84, 1, TimeSpan.FromMilliseconds(160))
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop });
        // Keep the page entrance short and all inputs live.
        NudgeSelection(surface, horizontal: false);
    }

    internal static void Reveal(FrameworkElement surface, bool slide = false, int milliseconds = 160)
    {
        surface.BeginAnimation(UIElement.OpacityProperty, null);
        surface.Opacity = 1;
        if (!IsEnabled) return;
        var duration = new Duration(TimeSpan.FromMilliseconds(milliseconds));
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        surface.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, duration)
        {
            EasingFunction = easing, FillBehavior = FillBehavior.Stop,
        });
        // Render transforms leave layout and hit-test bounds unchanged at rest.
        // Never replace an existing zoom, rotation or editor transform.
        if (slide && surface.RenderTransform.Value.IsIdentity)
        {
            var offset = new TranslateTransform();
            surface.RenderTransform = offset;
            surface.SetValue(RevealOffsetProperty, offset);
            offset.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(8, 0, duration)
            {
                EasingFunction = easing, FillBehavior = FillBehavior.Stop,
            });
        }
    }

    internal static void PrepareWindow(Window window, FrameworkElement surface)
    {
        if (!IsEnabled || window.IsLoaded) return;
        surface.Opacity = 0;
        void Loaded(object sender, RoutedEventArgs args)
        {
            window.Loaded -= Loaded;
            Reveal(surface);
        }
        window.Loaded += Loaded;
        // User input always wins over an appearance transition. This also
        // removes opacity composition work as soon as a gesture begins.
        void BeginInteraction(object sender, System.Windows.Input.MouseButtonEventArgs args) =>
            Finish(surface);
        window.PreviewMouseDown += BeginInteraction;
        window.Closed += (_, _) => window.PreviewMouseDown -= BeginInteraction;
    }

    internal static void Finish(FrameworkElement surface)
    {
        surface.BeginAnimation(UIElement.OpacityProperty, null);
        surface.Opacity = 1;
        if (surface.GetValue(RevealOffsetProperty) is TranslateTransform offset)
        {
            offset.BeginAnimation(TranslateTransform.YProperty, null);
            offset.Y = 0;
        }
        if (surface.GetValue(SelectionOffsetProperty) is TranslateTransform selectionOffset)
        {
            selectionOffset.BeginAnimation(TranslateTransform.XProperty, null);
            selectionOffset.BeginAnimation(TranslateTransform.YProperty, null);
            selectionOffset.X = 0;
            selectionOffset.Y = 0;
        }
    }
}
