using System.Windows.Controls;
using System.Windows.Input;

namespace Screenshot.App.Presentation;

public sealed class ToolbarEmojiStrip : ScrollViewer
{
    public ToolbarEmojiStrip()
    {
        SetResourceReference(StyleProperty, typeof(ScrollViewer));
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        Focusable = false;
        Height = MaxHeight = 3 * 34;
    }

    protected override void OnPreviewMouseWheel(MouseWheelEventArgs e)
    {
        // Whole rows keep all three visible rows readable after wheel input.
        ScrollToVerticalOffset(Math.Round(VerticalOffset / 34) * 34 - Math.Sign(e.Delta) * 3 * 34);
        e.Handled = true;
    }
}
