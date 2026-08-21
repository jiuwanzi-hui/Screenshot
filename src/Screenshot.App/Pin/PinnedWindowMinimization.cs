using System.Windows;
using System.Windows.Media.Animation;

namespace Screenshot.App.Pin;

internal static class PinnedWindowMinimization
{
    internal readonly record struct Bounds(double Left, double Top, double Width, double Height);

    internal static Bounds GetThumbnailBounds(int index, double width = 176, double height = 116)
    {
        var area = SystemParameters.WorkArea;
        return new Bounds(area.Right - 12 - width, area.Bottom - 12 - height - index * (height + 8), width, height);
    }

    internal static void Animate(Window window, Bounds target, Action completed)
    {
        var ease = new SineEase { EasingMode = EasingMode.EaseInOut };
        var storyboard = new Storyboard();
        Add(storyboard, window, Window.LeftProperty, target.Left, ease);
        Add(storyboard, window, Window.TopProperty, target.Top, ease);
        Add(storyboard, window, Window.WidthProperty, target.Width, ease);
        Add(storyboard, window, Window.HeightProperty, target.Height, ease);
        storyboard.Completed += (_, _) => completed();
        storyboard.Begin(window, true);
    }

    internal static void CommitCurrentAnimation(Window window)
    {
        // Storyboards keep an animated dependency-property value above the
        // normal Window.Left/Top values even after they reach the target. A
        // drag must first commit that visible value and release the animation
        // clock, otherwise assigning Top appears to do nothing.
        var left = window.Left;
        var top = window.Top;
        var width = window.Width;
        var height = window.Height;
        window.BeginAnimation(Window.LeftProperty, null);
        window.BeginAnimation(Window.TopProperty, null);
        window.BeginAnimation(Window.WidthProperty, null);
        window.BeginAnimation(Window.HeightProperty, null);
        window.Left = left;
        window.Top = top;
        window.Width = width;
        window.Height = height;
    }

    private static void Add(Storyboard storyboard, Window window, DependencyProperty property, double to, IEasingFunction ease)
    {
        var animation = new DoubleAnimation(to, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease };
        Storyboard.SetTarget(animation, window);
        Storyboard.SetTargetProperty(animation, new PropertyPath(property));
        storyboard.Children.Add(animation);
    }
}
