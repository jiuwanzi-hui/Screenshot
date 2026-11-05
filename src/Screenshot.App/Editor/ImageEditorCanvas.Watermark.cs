using System.Windows;
using System.Windows.Media;
using Screenshot.App.Watermarks;

namespace Screenshot.App.Editor;

public sealed partial class ImageEditorCanvas
{
    private WatermarkComposition? _watermark;
    private WatermarkVisual? _watermarkVisual;
    private bool _watermarkSuppressed;

    internal void SetWatermarkSuppressed(bool suppressed)
    {
        _watermarkSuppressed = suppressed;
        if (_watermarkVisual is not null)
            _watermarkVisual.Visibility = suppressed ? Visibility.Hidden : Visibility.Visible;
    }

    public void SetWatermark(WatermarkComposition? watermark)
    {
        _watermark = watermark;
        // A location/image can finish loading while the user is typing an annotation.
        // Replace only this passive layer; rebuilding the canvas would detach the live editor.
        if (_watermarkVisual is not null) Children.Remove(_watermarkVisual);
        AddWatermarkVisual();
    }

    private void AddWatermarkVisual()
    {
        _watermarkVisual = null;
        if (_watermark is null || !_isInitialized) return;
        var visual = new WatermarkVisual(_watermark)
        {
            Width = Width, Height = Height, IsHitTestVisible = false,
            Visibility = _watermarkSuppressed ? Visibility.Hidden : Visibility.Visible,
        };
        _watermarkVisual = visual;
        SetZIndex(visual, 1);
        Children.Add(visual);
    }

    private sealed class WatermarkVisual(WatermarkComposition composition) : FrameworkElement
    {
        protected override void OnRender(DrawingContext drawingContext) => composition.Draw(drawingContext, RenderSize);
    }
}
