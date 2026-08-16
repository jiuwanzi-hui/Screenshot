using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WpfMouseEventArgs = System.Windows.Input.MouseEventArgs;
using WpfPoint = System.Windows.Point;

namespace Screenshot.App.Editor;

public partial class ImageCropWindow : Window
{
    private readonly BitmapSource _source;
    private Int32Rect _cropRect;
    private Rect _renderedImageBounds;
    private WpfPoint? _dragStart;

    public ImageCropWindow(BitmapSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        _source = source;
        _cropRect = new Int32Rect(0, 0, source.PixelWidth, source.PixelHeight);
        InitializeComponent();
        CropPreviewImage.Source = source;
        CropSizeText.Text = $"{source.PixelWidth} x {source.PixelHeight} px";
    }

    public BitmapSource? CroppedImage { get; private set; }

    internal Int32Rect SelectedCropRect => _cropRect;

    internal static Int32Rect CalculateCropRect(
        int width,
        int height,
        int left,
        int top,
        int right,
        int bottom)
    {
        if (width <= 0 || height <= 0)
        {
            return Int32Rect.Empty;
        }

        left = Math.Clamp(left, 0, width - 1);
        top = Math.Clamp(top, 0, height - 1);
        right = Math.Clamp(right, 0, width - left - 1);
        bottom = Math.Clamp(bottom, 0, height - top - 1);
        return new Int32Rect(
            left,
            top,
            width - left - right,
            height - top - bottom);
    }

    internal static Int32Rect CalculateCropRectFromSelection(
        Rect imageBounds,
        Rect selection,
        int pixelWidth,
        int pixelHeight)
    {
        if (imageBounds.IsEmpty || imageBounds.Width <= 0 || imageBounds.Height <= 0 ||
            pixelWidth <= 0 || pixelHeight <= 0)
        {
            return Int32Rect.Empty;
        }

        var bounded = Rect.Intersect(imageBounds, selection);
        if (bounded.IsEmpty || bounded.Width <= 0 || bounded.Height <= 0)
        {
            var origin = new WpfPoint(
                Math.Clamp(selection.Left, imageBounds.Left, imageBounds.Right),
                Math.Clamp(selection.Top, imageBounds.Top, imageBounds.Bottom));
            bounded = new Rect(origin, new System.Windows.Size(1, 1));
        }
        var scaleX = pixelWidth / imageBounds.Width;
        var scaleY = pixelHeight / imageBounds.Height;
        var left = Math.Clamp(
            (int)Math.Floor((bounded.Left - imageBounds.Left) * scaleX),
            0,
            pixelWidth - 1);
        var top = Math.Clamp(
            (int)Math.Floor((bounded.Top - imageBounds.Top) * scaleY),
            0,
            pixelHeight - 1);
        var right = Math.Clamp(
            (int)Math.Ceiling((bounded.Right - imageBounds.Left) * scaleX),
            left + 1,
            pixelWidth);
        var bottom = Math.Clamp(
            (int)Math.Ceiling((bounded.Bottom - imageBounds.Top) * scaleY),
            top + 1,
            pixelHeight);
        return new Int32Rect(left, top, right - left, bottom - top);
    }

    internal static Rect AdjustSelectionRect(
        Rect selection,
        Rect bounds,
        string direction,
        double horizontalChange,
        double verticalChange,
        double minimumWidth = 1,
        double minimumHeight = 1)
    {
        if (selection.IsEmpty || bounds.IsEmpty)
        {
            return selection;
        }

        if (string.Equals(direction, "Move", StringComparison.Ordinal))
        {
            return new Rect(
                Math.Clamp(
                    selection.Left + horizontalChange,
                    bounds.Left,
                    Math.Max(bounds.Left, bounds.Right - selection.Width)),
                Math.Clamp(
                    selection.Top + verticalChange,
                    bounds.Top,
                    Math.Max(bounds.Top, bounds.Bottom - selection.Height)),
                selection.Width,
                selection.Height);
        }

        var left = selection.Left;
        var top = selection.Top;
        var right = selection.Right;
        var bottom = selection.Bottom;
        if (direction.Contains("Left", StringComparison.Ordinal))
        {
            left = Math.Clamp(
                left + horizontalChange,
                bounds.Left,
                right - minimumWidth);
        }
        if (direction.Contains("Right", StringComparison.Ordinal))
        {
            right = Math.Clamp(
                right + horizontalChange,
                left + minimumWidth,
                bounds.Right);
        }
        if (direction.Contains("Top", StringComparison.Ordinal))
        {
            top = Math.Clamp(
                top + verticalChange,
                bounds.Top,
                bottom - minimumHeight);
        }
        if (direction.Contains("Bottom", StringComparison.Ordinal))
        {
            bottom = Math.Clamp(
                bottom + verticalChange,
                top + minimumHeight,
                bounds.Bottom);
        }
        return new Rect(left, top, right - left, bottom - top);
    }

    internal static Int32Rect MoveCropRectWithoutResizing(
        Int32Rect cropRect,
        Rect imageBounds,
        Rect movedSelection,
        int pixelWidth,
        int pixelHeight)
    {
        if (cropRect.IsEmpty || imageBounds.IsEmpty ||
            imageBounds.Width <= 0 || imageBounds.Height <= 0 ||
            pixelWidth <= 0 || pixelHeight <= 0)
        {
            return cropRect;
        }

        var scaleX = pixelWidth / imageBounds.Width;
        var scaleY = pixelHeight / imageBounds.Height;
        var x = Math.Clamp(
            (int)Math.Round((movedSelection.Left - imageBounds.Left) * scaleX),
            0,
            Math.Max(0, pixelWidth - cropRect.Width));
        var y = Math.Clamp(
            (int)Math.Round((movedSelection.Top - imageBounds.Top) * scaleY),
            0,
            Math.Max(0, pixelHeight - cropRect.Height));
        return new Int32Rect(x, y, cropRect.Width, cropRect.Height);
    }

    internal static WpfPoint CalculateVisibleHandlePosition(
        Rect imageBounds,
        double centerX,
        double centerY,
        double handleWidth,
        double handleHeight)
    {
        var left = Math.Clamp(
            centerX - (handleWidth / 2),
            imageBounds.Left,
            Math.Max(imageBounds.Left, imageBounds.Right - handleWidth));
        var top = Math.Clamp(
            centerY - (handleHeight / 2),
            imageBounds.Top,
            Math.Max(imageBounds.Top, imageBounds.Bottom - handleHeight));
        return new WpfPoint(left, top);
    }

    private void OnSurfaceSizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateRenderedImageBounds();
        UpdateSelectionFromCropRect();
    }

    private void OnSurfaceMouseLeftButtonDown(
        object sender,
        MouseButtonEventArgs e)
    {
        if (_renderedImageBounds.IsEmpty || IsCropHandleSource(e.OriginalSource))
        {
            return;
        }

        var point = ClampToImage(e.GetPosition(CropInteractionSurface));
        _dragStart = point;
        CropInteractionSurface.CaptureMouse();
        UpdateSelectionFromDisplayRect(new Rect(point, point));
        e.Handled = true;
    }

    private void OnSurfaceMouseMove(object sender, WpfMouseEventArgs e)
    {
        if (_dragStart is not { } start ||
            e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var current = ClampToImage(e.GetPosition(CropInteractionSurface));
        UpdateSelectionFromDisplayRect(new Rect(start, current));
        e.Handled = true;
    }

    private void OnSurfaceMouseLeftButtonUp(
        object sender,
        MouseButtonEventArgs e)
    {
        if (_dragStart is null)
        {
            return;
        }

        var current = ClampToImage(e.GetPosition(CropInteractionSurface));
        UpdateSelectionFromDisplayRect(new Rect(_dragStart.Value, current));
        _dragStart = null;
        CropInteractionSurface.ReleaseMouseCapture();
        e.Handled = true;
    }

    private void UpdateRenderedImageBounds()
    {
        var availableWidth = CropInteractionSurface.ActualWidth;
        var availableHeight = CropInteractionSurface.ActualHeight;
        if (availableWidth <= 0 || availableHeight <= 0 ||
            _source.PixelWidth <= 0 || _source.PixelHeight <= 0)
        {
            _renderedImageBounds = Rect.Empty;
            return;
        }

        var scale = Math.Min(
            availableWidth / _source.PixelWidth,
            availableHeight / _source.PixelHeight);
        var width = _source.PixelWidth * scale;
        var height = _source.PixelHeight * scale;
        _renderedImageBounds = new Rect(
            (availableWidth - width) / 2,
            (availableHeight - height) / 2,
            width,
            height);
    }

    private WpfPoint ClampToImage(WpfPoint point) => new(
        Math.Clamp(point.X, _renderedImageBounds.Left, _renderedImageBounds.Right),
        Math.Clamp(point.Y, _renderedImageBounds.Top, _renderedImageBounds.Bottom));

    private void UpdateSelectionFromDisplayRect(Rect selection)
    {
        if (_renderedImageBounds.IsEmpty)
        {
            return;
        }

        _cropRect = CalculateCropRectFromSelection(
            _renderedImageBounds,
            selection,
            _source.PixelWidth,
            _source.PixelHeight);
        UpdateSelectionFromCropRect();
    }

    private void UpdateSelectionFromCropRect()
    {
        if (_renderedImageBounds.IsEmpty)
        {
            return;
        }

        var scaleX = _renderedImageBounds.Width / _source.PixelWidth;
        var scaleY = _renderedImageBounds.Height / _source.PixelHeight;
        var selection = new Rect(
            _renderedImageBounds.Left + (_cropRect.X * scaleX),
            _renderedImageBounds.Top + (_cropRect.Y * scaleY),
            Math.Max(1, _cropRect.Width * scaleX),
            Math.Max(1, _cropRect.Height * scaleY));
        Canvas.SetLeft(CropSelection, selection.Left);
        Canvas.SetTop(CropSelection, selection.Top);
        CropSelection.Width = selection.Width;
        CropSelection.Height = selection.Height;
        PositionCropHandles(selection);
        CropMask.Data = new CombinedGeometry(
            GeometryCombineMode.Exclude,
            new RectangleGeometry(_renderedImageBounds),
            new RectangleGeometry(selection));
        CropSizeText.Text = $"{_cropRect.Width} x {_cropRect.Height} px";
        CropPositionText.Text = $"选区 X {_cropRect.X} · Y {_cropRect.Y}";
    }

    private void OnCropHandleDragDelta(object sender, DragDeltaEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string direction } ||
            _renderedImageBounds.IsEmpty)
        {
            return;
        }

        var selection = GetSelectionDisplayRect();
        var minimumWidth = Math.Max(1, _renderedImageBounds.Width / _source.PixelWidth);
        var minimumHeight = Math.Max(1, _renderedImageBounds.Height / _source.PixelHeight);
        var adjusted = AdjustSelectionRect(
            selection,
            _renderedImageBounds,
            direction,
            e.HorizontalChange,
            e.VerticalChange,
            minimumWidth,
            minimumHeight);
        if (string.Equals(direction, "Move", StringComparison.Ordinal))
        {
            _cropRect = MoveCropRectWithoutResizing(
                _cropRect,
                _renderedImageBounds,
                adjusted,
                _source.PixelWidth,
                _source.PixelHeight);
            UpdateSelectionFromCropRect();
        }
        else
        {
            UpdateSelectionFromDisplayRect(adjusted);
        }
    }

    private Rect GetSelectionDisplayRect()
    {
        var scaleX = _renderedImageBounds.Width / _source.PixelWidth;
        var scaleY = _renderedImageBounds.Height / _source.PixelHeight;
        return new Rect(
            _renderedImageBounds.Left + (_cropRect.X * scaleX),
            _renderedImageBounds.Top + (_cropRect.Y * scaleY),
            Math.Max(1, _cropRect.Width * scaleX),
            Math.Max(1, _cropRect.Height * scaleY));
    }

    private void PositionCropHandles(Rect selection)
    {
        SetThumbBounds(
            CropMoveThumb,
            selection.Left,
            selection.Top,
            selection.Width,
            selection.Height);
        PositionResizeThumb(CropTopLeftThumb, selection.Left, selection.Top);
        PositionResizeThumb(CropTopThumb, selection.Left + (selection.Width / 2), selection.Top);
        PositionResizeThumb(CropTopRightThumb, selection.Right, selection.Top);
        PositionResizeThumb(CropRightThumb, selection.Right, selection.Top + (selection.Height / 2));
        PositionResizeThumb(CropBottomRightThumb, selection.Right, selection.Bottom);
        PositionResizeThumb(CropBottomThumb, selection.Left + (selection.Width / 2), selection.Bottom);
        PositionResizeThumb(CropBottomLeftThumb, selection.Left, selection.Bottom);
        PositionResizeThumb(CropLeftThumb, selection.Left, selection.Top + (selection.Height / 2));
    }

    private static void SetThumbBounds(
        FrameworkElement thumb,
        double left,
        double top,
        double width,
        double height)
    {
        Canvas.SetLeft(thumb, left);
        Canvas.SetTop(thumb, top);
        thumb.Width = width;
        thumb.Height = height;
    }

    private void PositionResizeThumb(
        FrameworkElement thumb,
        double centerX,
        double centerY)
    {
        var position = CalculateVisibleHandlePosition(
            _renderedImageBounds,
            centerX,
            centerY,
            thumb.Width,
            thumb.Height);
        Canvas.SetLeft(thumb, position.X);
        Canvas.SetTop(thumb, position.Y);
    }

    private static bool IsCropHandleSource(object? source)
    {
        var current = source as DependencyObject;
        while (current is not null)
        {
            if (current is Thumb)
            {
                return true;
            }
            current = current is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }
        return false;
    }

    private void OnResetClick(object sender, RoutedEventArgs e)
    {
        _cropRect = new Int32Rect(
            0,
            0,
            _source.PixelWidth,
            _source.PixelHeight);
        UpdateSelectionFromCropRect();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void OnConfirmClick(object sender, RoutedEventArgs e)
    {
        var cropped = new CroppedBitmap(_source, _cropRect);
        cropped.Freeze();
        CroppedImage = cropped;
        DialogResult = true;
    }
}
