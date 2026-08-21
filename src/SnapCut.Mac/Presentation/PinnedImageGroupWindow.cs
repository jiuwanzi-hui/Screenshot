using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using SnapCut.Core;
using SnapCut.Mac.Native;

namespace SnapCut.Mac.Presentation;

internal sealed class PinnedImageGroupWindow : Window
{
    private readonly Image _image;
    private readonly double _naturalWidth;
    private readonly double _naturalHeight;
    private readonly string _imagePath;
    private double _zoom = 1;

    public PinnedImageGroupWindow(PixelImage image, string imagePath)
    {
        _imagePath = imagePath;
        var bitmap = PixelImageBitmap.Create(image);
        _naturalWidth = bitmap.Size.Width;
        _naturalHeight = bitmap.Size.Height;
        Title = "SnapCut 组合钉图";
        SystemDecorations = SystemDecorations.None;
        ShowInTaskbar = false;
        Topmost = true;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        _image = new Image
        {
            Source = bitmap,
            Stretch = Stretch.Fill,
        };
        var ungroup = new MenuItem { Header = "解除组合" };
        ungroup.Click += (_, _) => UngroupRequested?.Invoke();
        var copy = new MenuItem { Header = "复制组合图片" };
        copy.Click += (_, _) => MacNativeUi.CopyPngFile(_imagePath);
        var close = new MenuItem { Header = "关闭组合" };
        close.Click += (_, _) => CloseRequested?.Invoke();
        var surface = new Border
        {
            BorderBrush = new SolidColorBrush(Color.Parse("#566575")),
            BorderThickness = new Thickness(1),
            Background = Brushes.White,
            Child = _image,
            ContextMenu = new ContextMenu
            {
                Items = { copy, ungroup, new Separator(), close },
            },
        };
        surface.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                BeginMoveDrag(e);
                e.Handled = true;
            }
        };
        surface.PointerWheelChanged += (_, e) =>
        {
            _zoom = Math.Clamp(_zoom + (e.Delta.Y > 0 ? 0.1 : -0.1), 0.2, 4);
            ApplyZoom();
            e.Handled = true;
        };
        Content = surface;
        ApplyZoom();
        Opened += (_, _) => MacNativeUi.ExcludeFromScreenCapture(this);
    }

    public event Action? UngroupRequested;

    public event Action? CloseRequested;

    private void ApplyZoom()
    {
        var width = Math.Clamp(_naturalWidth * _zoom, 100, 1800);
        var height = Math.Clamp(_naturalHeight * _zoom, 80, 1300);
        _image.Width = width;
        _image.Height = height;
        Width = width + 2;
        Height = height + 2;
    }
}
