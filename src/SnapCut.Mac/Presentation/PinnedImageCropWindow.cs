using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using SnapCut.Core;
using SnapCut.Mac.Native;

namespace SnapCut.Mac.Presentation;

internal sealed class PinnedImageCropWindow : Window
{
    private readonly TaskCompletionSource<Rect?> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly SelectionCanvas _canvas;

    public PinnedImageCropWindow(PixelImage image)
    {
        Title = "SnapCut 钉图裁剪";
        Width = Math.Clamp(image.Width + 40, 520, 1100);
        Height = Math.Clamp(image.Height + 100, 400, 820);
        MinWidth = 460;
        MinHeight = 340;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush(MacTheme.WindowBackground);
        _canvas = new SelectionCanvas(PixelImageBitmap.Create(image), image);
        _canvas.CancelRequested += () => _completion.TrySetResult(null);
        var apply = MacTheme.CreateButton("应用裁剪", primary: true);
        apply.Click += (_, _) =>
        {
            if (_canvas.HasSelection)
            {
                _completion.TrySetResult(_canvas.Selection);
            }
        };
        var cancel = MacTheme.CreateButton("取消");
        cancel.Click += (_, _) => _completion.TrySetResult(null);
        var toolbar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Children = { cancel, apply },
        };
        Content = new Grid
        {
            Margin = new Thickness(12),
            RowDefinitions = new RowDefinitions("*,Auto"),
            RowSpacing = 10,
            Children = { _canvas, toolbar },
        };
        Grid.SetRow(toolbar, 1);
        Closed += (_, _) => _completion.TrySetResult(null);
        Opened += (_, _) => MacNativeUi.ExcludeFromScreenCapture(this);
    }

    public async Task<(Rect Selection, Size SurfaceSize)?> ShowAsync()
    {
        Show();
        Activate();
        var selection = await _completion.Task;
        var surface = _canvas.Bounds.Size;
        Close();
        return selection is null ? null : (selection.Value, surface);
    }
}
