using System.Windows;
using System.Windows.Controls.Primitives;
using WpfButtonBase = System.Windows.Controls.Primitives.ButtonBase;
using WpfTextBoxBase = System.Windows.Controls.Primitives.TextBoxBase;

namespace Screenshot.App.Presentation;

internal static class ToolbarDragInteraction
{
    public static bool IsBlankSurface(
        DependencyObject? source,
        DependencyObject toolbarRoot)
    {
        for (var current = source;
             current is not null;
             current = GetParent(current))
        {
            if (ReferenceEquals(current, toolbarRoot))
            {
                return true;
            }

            if (current is WpfButtonBase or RangeBase or Thumb or
                WpfTextBoxBase or Selector)
            {
                return false;
            }
        }

        return false;
    }

    private static DependencyObject? GetParent(DependencyObject current)
    {
        if (current is FrameworkContentElement contentElement)
        {
            return contentElement.Parent;
        }

        return System.Windows.Media.VisualTreeHelper.GetParent(current);
    }
}
