using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using SnapCut.Core;
using SnapCut.Mac.Native;
using SnapCut.Mac.Pin;
using SnapCut.Mac.Editor;
using Avalonia.Threading;

namespace SnapCut.Mac.Presentation;

internal sealed class PinnedImageWindow : Window
{
    private readonly Image _imageView;
    private readonly string _imagePath;
    private readonly double _naturalWidth;
    private readonly double _naturalHeight;
    private double _zoom = 1;
    private readonly Guid _id;
    private bool _hidden;
    private bool _closing;
    private readonly PixelImage _sourceImage;
    private readonly Border _surface;

    public PinnedImageWindow(
        PixelImage image,
        string imagePath,
        MacPinnedImageState? savedState = null)
    {
        savedState ??= new MacPinnedImageState(
            Guid.NewGuid(), imagePath, 100, 100, 1, 1, false);
        _id = savedState.Id;
        _sourceImage = image;
        _zoom = Math.Clamp(savedState.Zoom, 0.2, 4);
        _hidden = savedState.Hidden;
        _imagePath = imagePath;
        var bitmap = PixelImageBitmap.Create(image);
        _naturalWidth = bitmap.Size.Width;
        _naturalHeight = bitmap.Size.Height;
        Title = "SnapCut 钉图";
        SystemDecorations = SystemDecorations.None;
        ShowInTaskbar = false;
        Topmost = true;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Position = new PixelPoint(savedState.X, savedState.Y);
        Opacity = Math.Clamp(savedState.Opacity, 0.2, 1);
        Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];

        _imageView = new Image
        {
            Source = bitmap,
            Stretch = Stretch.Fill,
            Width = _naturalWidth,
            Height = _naturalHeight,
        };
        _surface = new Border
        {
            BorderBrush = new SolidColorBrush(Color.Parse("#566575")),
            BorderThickness = new Thickness(1),
            Background = Brushes.White,
            Child = _imageView,
            ContextMenu = CreateContextMenu(),
        };
        _surface.PointerPressed += HandlePointerPressed;
        _surface.PointerWheelChanged += HandlePointerWheel;
        Content = _surface;
        ApplyZoom();
        Opened += (_, _) => MacNativeUi.ExcludeFromScreenCapture(this);
        PositionChanged += (_, _) => StateChanged?.Invoke();
        Closed += (_, _) =>
        {
            if (_closing)
            {
                PinnedClosed?.Invoke();
            }
        };
    }

    public event Action? StateChanged;

    public event Action? PinnedClosed;

    public event Action? CropRequested;

    public event Action? EditRequested;

    public PixelImage SourceImage => _sourceImage;

    public async Task<PixelImage?> EditAsync()
    {
        var canvas = new SelectionCanvas(
            PixelImageBitmap.Create(_sourceImage),
            _sourceImage)
        {
            AnnotationColor = Color.Parse("#FF3B30"),
            AnnotationWidth = 3,
        };
        _surface.Child = canvas;
        Dispatcher.UIThread.Post(canvas.SetFixedSelection);
        var apply = await new PinnedImageEditorToolbarWindow(canvas, this).WaitAsync();
        _surface.Child = _imageView;
        if (!apply || canvas.Annotations.Count == 0)
        {
            return null;
        }

        return MacAnnotationRenderer.Apply(
            _sourceImage,
            new Rect(0, 0, Math.Max(1, canvas.Bounds.Width), Math.Max(1, canvas.Bounds.Height)),
            canvas.Annotations);
    }

    public MacPinnedImageState GetState() => new(
        _id,
        _imagePath,
        Position.X,
        Position.Y,
        _zoom,
        Opacity,
        _hidden);

    public void HidePinnedImage()
    {
        _hidden = true;
        Hide();
        StateChanged?.Invoke();
    }

    public void ShowPinnedImage()
    {
        _hidden = false;
        if (!IsVisible)
        {
            Show();
        }
        Activate();
        StateChanged?.Invoke();
    }

    private ContextMenu CreateContextMenu()
    {
        var copy = new MenuItem { Header = "复制图片" };
        copy.Click += (_, _) => MacNativeUi.CopyPngFile(_imagePath);
        var save = new MenuItem { Header = "另存为…" };
        save.Click += (_, _) => SaveAs();
        var opacityUp = new MenuItem { Header = "提高透明度" };
        opacityUp.Click += (_, _) =>
        {
            Opacity = Math.Clamp(Opacity + 0.1, 0.2, 1);
            StateChanged?.Invoke();
        };
        var opacityDown = new MenuItem { Header = "降低透明度" };
        opacityDown.Click += (_, _) =>
        {
            Opacity = Math.Clamp(Opacity - 0.1, 0.2, 1);
            StateChanged?.Invoke();
        };
        var actualSize = new MenuItem { Header = "恢复原始大小" };
        actualSize.Click += (_, _) =>
        {
            _zoom = 1;
            ApplyZoom();
        };
        var crop = new MenuItem { Header = "裁剪…" };
        crop.Click += (_, _) => CropRequested?.Invoke();
        var edit = new MenuItem { Header = "编辑" };
        edit.Click += (_, _) => EditRequested?.Invoke();
        var close = new MenuItem { Header = "关闭钉图" };
        close.Click += (_, _) =>
        {
            _closing = true;
            Close();
        };
        return new ContextMenu
        {
            Items =
            {
                copy,
                save,
                new Separator(),
                opacityUp,
                opacityDown,
                actualSize,
                edit,
                crop,
                new Separator(),
                close,
            },
        };
    }

    private void HandlePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var point = e.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (e.ClickCount >= 2)
        {
            _zoom = 1;
            ApplyZoom();
            e.Handled = true;
            return;
        }

        BeginMoveDrag(e);
        e.Handled = true;
    }

    private void HandlePointerWheel(object? sender, PointerWheelEventArgs e)
    {
        _zoom = Math.Clamp(_zoom + (e.Delta.Y > 0 ? 0.1 : -0.1), 0.2, 4);
        ApplyZoom();
        e.Handled = true;
    }

    private void ApplyZoom()
    {
        var targetWidth = Math.Clamp(_naturalWidth * _zoom, 80, 1600);
        var targetHeight = Math.Clamp(_naturalHeight * _zoom, 60, 1200);
        _imageView.Width = targetWidth;
        _imageView.Height = targetHeight;
        Width = targetWidth + 2;
        Height = targetHeight + 2;
        StateChanged?.Invoke();
    }

    private void SaveAs()
    {
        var selected = MacNativeUi.SelectPngSavePath(Path.GetFileName(_imagePath));
        if (string.IsNullOrWhiteSpace(selected))
        {
            return;
        }

        if (!selected.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
        {
            selected += ".png";
        }

        File.Copy(_imagePath, selected, overwrite: true);
    }
}
