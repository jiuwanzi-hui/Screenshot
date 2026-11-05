using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace Screenshot.App.Presentation;

// Mouse input may originate from a Run/Span inside a text icon. These are
// content elements, not Visuals, but still belong to the surrounding control.
internal static class InputElementTree
{
    internal static bool IsWithin(DependencyObject? source, DependencyObject root)
    {
        for (var current = source; current is not null; current = GetParent(current))
        {
            if (ReferenceEquals(current, root))
            {
                return true;
            }
        }

        return false;
    }

    internal static DependencyObject? GetParent(DependencyObject element) => element switch
    {
        FrameworkContentElement content => content.Parent ?? ContentOperations.GetParent(content),
        ContentElement content => ContentOperations.GetParent(content),
        Visual or Visual3D => VisualTreeHelper.GetParent(element) ?? (element as FrameworkElement)?.Parent,
        _ => LogicalTreeHelper.GetParent(element),
    };
}
