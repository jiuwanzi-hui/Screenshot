using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using SnapCut.Core;
using SnapCut.Mac.Native;

namespace SnapCut.Mac.Presentation;

internal sealed class CapturePreviewWindow : Window
{
    private readonly Image _imageView;
    private readonly TextBlock _status;
    private readonly string _historyPath;
    private readonly double _naturalWidth;
    private readonly double _naturalHeight;
    private double _zoom = 1;

    public CapturePreviewWindow(PixelImage image, string historyPath, bool isScrollCapture)
    {
        _historyPath = historyPath;
        var bitmap = PixelImageBitmap.Create(image);
        _naturalWidth = bitmap.Size.Width;
        _naturalHeight = bitmap.Size.Height;
        Title = isScrollCapture ? "SnapCut 长截图" : "SnapCut 截图";
        Width = Math.Clamp(_naturalWidth + 42, 520, 1080);
        Height = Math.Clamp(_naturalHeight + 104, 420, 820);
        MinWidth = 480;
        MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush(MacTheme.WindowBackground);

        _imageView = new Image
        {
            Source = bitmap,
            Stretch = Stretch.Fill,
            Width = _naturalWidth,
            Height = _naturalHeight,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
        };
        var viewer = new ScrollViewer
        {
            Content = _imageView,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            Background = Brushes.White,
        };

        var copy = MacTheme.CreateButton("复制");
        copy.Click += (_, _) => Copy();
        var save = MacTheme.CreateButton("另存为", primary: true);
        save.Click += (_, _) => SaveAs();
        var open = MacTheme.CreateButton("在访达中打开");
        open.Click += (_, _) => MacNativeUi.OpenPath(
            Path.GetDirectoryName(_historyPath) ?? _historyPath);
        _status = new TextBlock
        {
            Text = $"{image.Width} × {image.Height} px",
            Foreground = new SolidColorBrush(MacTheme.SecondaryText),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var toolbar = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto"),
            Margin = new Thickness(16, 12),
            ColumnSpacing = 8,
            Children =
            {
                _status,
                copy,
                save,
                open,
            },
        };
        Grid.SetColumn(copy, 1);
        Grid.SetColumn(save, 2);
        Grid.SetColumn(open, 3);

        Content = new Grid
        {
            RowDefinitions = new RowDefinitions("*,Auto"),
            Children =
            {
                viewer,
                toolbar,
            },
        };
        Grid.SetRow(toolbar, 1);
        Opened += (_, _) => MacNativeUi.ExcludeFromScreenCapture(this);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        if ((e.KeyModifiers & KeyModifiers.Control) == 0)
        {
            base.OnPointerWheelChanged(e);
            return;
        }

        _zoom = Math.Clamp(_zoom + (e.Delta.Y > 0 ? 0.1 : -0.1), 0.2, 4);
        _imageView.Width = _naturalWidth * _zoom;
        _imageView.Height = _naturalHeight * _zoom;
        _status.Text = $"{Path.GetFileName(_historyPath)} · {_zoom:P0}";
        e.Handled = true;
    }

    private void Copy()
    {
        _status.Text = MacNativeUi.CopyPngFile(_historyPath)
            ? "已复制到剪贴板"
            : "复制失败，请先使用另存为";
    }

    private void SaveAs()
    {
        var selected = MacNativeUi.SelectPngSavePath(Path.GetFileName(_historyPath));
        if (string.IsNullOrWhiteSpace(selected))
        {
            return;
        }

        if (!selected.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
        {
            selected += ".png";
        }

        File.Copy(_historyPath, selected, overwrite: true);
        _status.Text = $"已保存到 {selected}";
    }
}
