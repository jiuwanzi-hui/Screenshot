using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Screenshot.App.Capture;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfColor = System.Windows.Media.Color;
using WpfCursors = System.Windows.Input.Cursors;
using WpfImage = System.Windows.Controls.Image;
using WpfKeyEventArgs = System.Windows.Input.KeyEventArgs;
using WpfMouseEventArgs = System.Windows.Input.MouseEventArgs;
using WpfPen = System.Windows.Media.Pen;
using WpfPoint = System.Windows.Point;
using WpfRectangle = System.Windows.Shapes.Rectangle;
using WpfSize = System.Windows.Size;
using WpfTextBox = System.Windows.Controls.TextBox;

namespace Screenshot.App.Editor;

public sealed class ImageEditorCanvas : Canvas
{
    private CapturedImage? _capturedImage;
    private EditorDocument _document = new();
    private EditorTool _selectedTool = EditorTool.Rectangle;
    private WpfColor _selectedColor = WpfColor.FromRgb(214, 69, 69);
    private EmojiSticker _selectedEmoji = EmojiSticker.Smile;
    private double _strokeWidth = 3;
    private WpfPoint _drawingStartPoint;
    private List<WpfPoint>? _brushPoints;
    private UIElement? _drawingPreview;
    private WpfTextBox? _activeTextInput;
    private WpfPoint _activeTextPosition;
    private bool _isDrawing;
    private double _baseDisplayWidth;
    private double _baseDisplayHeight;
    private double _zoom = 1;

    public ImageEditorCanvas()
    {
        Background = WpfBrushes.Transparent;
        ClipToBounds = true;
        Cursor = WpfCursors.Cross;
        Focusable = true;
    }

    public event EventHandler? HistoryChanged;

    public bool HasImage => _capturedImage is not null;

    public bool CanUndo => _document.CanUndo;

    public bool CanRedo => _document.CanRedo;

    public bool HasTranslationOverlay =>
        _document.Annotations.Any(annotation =>
            annotation is TranslationOverlayAnnotation);

    public double Zoom => _zoom;

    public double DisplayWidth => _baseDisplayWidth * _zoom;

    public double DisplayHeight => _baseDisplayHeight * _zoom;

    public void Initialize(
        CapturedImage capturedImage,
        double displayWidth,
        double displayHeight)
    {
        ArgumentNullException.ThrowIfNull(capturedImage);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(displayWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(displayHeight);

        _capturedImage = capturedImage;
        _document = new EditorDocument();
        Width = capturedImage.Preview.PixelWidth;
        Height = capturedImage.Preview.PixelHeight;
        _baseDisplayWidth = displayWidth;
        _baseDisplayHeight = displayHeight;
        _zoom = 1;
        RenderTransformOrigin = new WpfPoint(0, 0);
        ApplyDisplayTransform();
        RebuildCanvas();
        RaiseHistoryChanged();
    }

    public Rect? GetAnnotationBounds()
    {
        CommitPendingText();
        Rect? combinedBounds = null;

        foreach (var annotation in _document.Annotations)
        {
            var bounds = GetAnnotationBounds(annotation);
            combinedBounds = combinedBounds.HasValue
                ? Rect.Union(combinedBounds.Value, bounds)
                : bounds;
        }

        return combinedBounds;
    }

    public void Reframe(
        CapturedImage capturedImage,
        double displayWidth,
        double displayHeight,
        Vector annotationOffset)
    {
        ArgumentNullException.ThrowIfNull(capturedImage);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(displayWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(displayHeight);

        CommitPendingText();
        if (annotationOffset.X != 0 || annotationOffset.Y != 0)
        {
            _document.TransformAnnotations(annotation =>
                TranslateAnnotation(annotation, annotationOffset));
        }

        _capturedImage = capturedImage;
        Width = capturedImage.Preview.PixelWidth;
        Height = capturedImage.Preview.PixelHeight;
        _baseDisplayWidth = displayWidth;
        _baseDisplayHeight = displayHeight;
        _zoom = 1;
        RenderTransformOrigin = new WpfPoint(0, 0);
        ApplyDisplayTransform();
        RebuildCanvas();
        RaiseHistoryChanged();
    }

    public void SetZoom(double zoom)
    {
        if (_capturedImage is null)
        {
            return;
        }

        _zoom = Math.Clamp(zoom, 0.25, 4);
        ApplyDisplayTransform();
    }

    public void SelectTool(EditorTool tool)
    {
        CommitPendingText();
        _selectedTool = tool;
    }

    public void SelectColor(WpfColor color)
    {
        _selectedColor = color;
    }

    public void SelectEmoji(EmojiSticker emoji)
    {
        _selectedEmoji = emoji;
    }

    public void SetStrokeWidth(double strokeWidth)
    {
        _strokeWidth = Math.Clamp(strokeWidth, 1, 24);
    }

    public void AddTranslationOverlay(
        IReadOnlyList<TranslatedTextAnnotationRegion> regions)
    {
        ArgumentNullException.ThrowIfNull(regions);
        var validRegions = regions
            .Where(region =>
                !string.IsNullOrWhiteSpace(region.Text) &&
                region.Bounds.Width > 0 &&
                region.Bounds.Height > 0)
            .ToArray();
        if (validRegions.Length == 0)
        {
            return;
        }

        CommitPendingText();
        _document.Add(new TranslationOverlayAnnotation(validRegions));
        RebuildCanvas();
        RaiseHistoryChanged();
    }

    public void Undo()
    {
        CommitPendingText();
        _document.Undo();
        RebuildCanvas();
        RaiseHistoryChanged();
    }

    public void Redo()
    {
        CommitPendingText();
        _document.Redo();
        RebuildCanvas();
        RaiseHistoryChanged();
    }

    public bool TryUndoPreviousOperation()
    {
        if (CancelActiveOperation())
        {
            Focus();
            return true;
        }

        if (!CanUndo)
        {
            return false;
        }

        Undo();
        Focus();
        return true;
    }

    public void CommitPendingText()
    {
        CommitActiveTextInput(keepText: true);
    }

    /// <summary>
    /// Cancels an annotation that is still being drawn or typed without
    /// changing the document's completed annotations.
    /// </summary>
    public bool CancelActiveOperation()
    {
        var wasCanceled = false;

        if (_isDrawing)
        {
            _isDrawing = false;
            ReleaseMouseCapture();
            _brushPoints = null;

            if (_drawingPreview is not null)
            {
                Children.Remove(_drawingPreview);
                _drawingPreview = null;
            }

            wasCanceled = true;
        }

        if (_activeTextInput is not null)
        {
            CommitActiveTextInput(keepText: false);
            wasCanceled = true;
        }

        return wasCanceled;
    }

    /// <summary>
    /// Removes the current editing session while leaving ownership of the
    /// captured image with the caller that initialized this canvas.
    /// </summary>
    public void Reset()
    {
        _ = CancelActiveOperation();
        _capturedImage = null;
        _document = new EditorDocument();
        _drawingPreview = null;
        _brushPoints = null;
        _isDrawing = false;
        _baseDisplayWidth = 0;
        _baseDisplayHeight = 0;
        _zoom = 1;
        Children.Clear();
        Width = double.NaN;
        Height = double.NaN;
        RenderTransform = Transform.Identity;
        RaiseHistoryChanged();
    }

    public RenderTargetBitmap RenderEditedImage()
    {
        if (_capturedImage is null)
        {
            throw new InvalidOperationException("截图编辑画布尚未初始化。");
        }

        CommitPendingText();
        var displayTransform = RenderTransform;
        RenderTransform = Transform.Identity;

        try
        {
            Measure(new WpfSize(Width, Height));
            Arrange(new Rect(0, 0, Width, Height));
            var renderedImage = new RenderTargetBitmap(
                _capturedImage.Preview.PixelWidth,
                _capturedImage.Preview.PixelHeight,
                96,
                96,
                PixelFormats.Pbgra32);
            renderedImage.Render(this);
            renderedImage.Freeze();
            return renderedImage;
        }
        finally
        {
            RenderTransform = displayTransform;
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);

        if (_capturedImage is null)
        {
            return;
        }

        CommitPendingText();
        var point = ClampPoint(e.GetPosition(this));

        if (_selectedTool == EditorTool.Emoji)
        {
            _document.Add(new EmojiAnnotation(
                point,
                _selectedEmoji,
                Math.Max(24, _strokeWidth * 9)));
            RebuildCanvas();
            RaiseHistoryChanged();
            e.Handled = true;
            return;
        }

        if (_selectedTool == EditorTool.Text)
        {
            StartTextInput(point);
            e.Handled = true;
            return;
        }

        _isDrawing = true;
        _drawingStartPoint = point;
        CaptureMouse();
        CreateDrawingPreview(point);
        e.Handled = true;
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonDown(e);

        if (TryUndoPreviousOperation())
        {
            e.Handled = true;
        }
    }

    protected override void OnMouseMove(WpfMouseEventArgs e)
    {
        base.OnMouseMove(e);

        if (!_isDrawing || _drawingPreview is null)
        {
            return;
        }

        var point = ClampPoint(e.GetPosition(this));

        switch (_selectedTool)
        {
            case EditorTool.Rectangle:
                UpdateRectanglePreview((WpfRectangle)_drawingPreview, point);
                break;
            case EditorTool.Arrow:
                UpdateArrowPreview((Line)_drawingPreview, point);
                break;
            case EditorTool.Brush:
            case EditorTool.Mosaic:
                UpdateBrushPreview((Polyline)_drawingPreview, point);
                break;
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);

        if (!_isDrawing)
        {
            return;
        }

        _isDrawing = false;
        ReleaseMouseCapture();
        var endPoint = ClampPoint(e.GetPosition(this));

        if (_drawingPreview is not null)
        {
            Children.Remove(_drawingPreview);
            _drawingPreview = null;
        }

        AddDrawingAnnotation(endPoint);
        e.Handled = true;
    }

    private void CreateDrawingPreview(WpfPoint point)
    {
        _drawingPreview = _selectedTool switch
        {
            EditorTool.Rectangle => new WpfRectangle
            {
                Stroke = new SolidColorBrush(_selectedColor),
                StrokeThickness = _strokeWidth,
                Fill = WpfBrushes.Transparent,
            },
            EditorTool.Arrow => new Line
            {
                X1 = point.X,
                Y1 = point.Y,
                X2 = point.X,
                Y2 = point.Y,
                Stroke = new SolidColorBrush(_selectedColor),
                StrokeThickness = _strokeWidth,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
            },
            EditorTool.Brush => CreateBrushPreview(point),
            EditorTool.Mosaic => CreateMosaicBrushPreview(point),
            _ => null,
        };

        if (_drawingPreview is not null)
        {
            Children.Add(_drawingPreview);
        }
    }

    private Polyline CreateBrushPreview(WpfPoint point)
    {
        _brushPoints = [point];

        return new Polyline
        {
            Stroke = new SolidColorBrush(_selectedColor),
            StrokeThickness = _strokeWidth,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Points = new PointCollection(_brushPoints),
        };
    }

    private Polyline CreateMosaicBrushPreview(WpfPoint point)
    {
        _brushPoints = [point];

        return new Polyline
        {
            Stroke = new SolidColorBrush(WpfColor.FromArgb(150, 46, 175, 165)),
            StrokeThickness = GetMosaicStrokeWidth(),
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Points = new PointCollection(_brushPoints),
        };
    }

    private void UpdateRectanglePreview(
        WpfRectangle rectangle,
        WpfPoint currentPoint)
    {
        var bounds = CreateBounds(_drawingStartPoint, currentPoint);
        SetLeft(rectangle, bounds.X);
        SetTop(rectangle, bounds.Y);
        rectangle.Width = bounds.Width;
        rectangle.Height = bounds.Height;
    }

    private static void UpdateArrowPreview(Line line, WpfPoint currentPoint)
    {
        line.X2 = currentPoint.X;
        line.Y2 = currentPoint.Y;
    }

    private void UpdateBrushPreview(Polyline line, WpfPoint currentPoint)
    {
        if (_brushPoints is null)
        {
            return;
        }

        _brushPoints.Add(currentPoint);
        line.Points.Add(currentPoint);
    }

    private void AddDrawingAnnotation(WpfPoint endPoint)
    {
        EditorAnnotation? annotation = _selectedTool switch
        {
            EditorTool.Rectangle => CreateRectangleAnnotation(endPoint),
            EditorTool.Arrow => CreateArrowAnnotation(endPoint),
            EditorTool.Brush => CreateBrushAnnotation(),
            EditorTool.Mosaic => CreateMosaicAnnotation(),
            _ => null,
        };

        _brushPoints = null;

        if (annotation is null)
        {
            return;
        }

        _document.Add(annotation);
        RebuildCanvas();
        RaiseHistoryChanged();
    }

    private RectangleAnnotation? CreateRectangleAnnotation(WpfPoint endPoint)
    {
        var bounds = CreateBounds(_drawingStartPoint, endPoint);
        return bounds.Width >= 2 && bounds.Height >= 2
            ? new RectangleAnnotation(bounds, _selectedColor, _strokeWidth)
            : null;
    }

    private ArrowAnnotation? CreateArrowAnnotation(WpfPoint endPoint)
    {
        return (_drawingStartPoint - endPoint).Length >= 2
            ? new ArrowAnnotation(
                _drawingStartPoint,
                endPoint,
                _selectedColor,
                _strokeWidth)
            : null;
    }

    private BrushAnnotation? CreateBrushAnnotation()
    {
        return _brushPoints is { Count: > 1 }
            ? new BrushAnnotation(
                _brushPoints.ToArray(),
                _selectedColor,
                _strokeWidth)
            : null;
    }

    private MosaicAnnotation? CreateMosaicAnnotation()
    {
        return _brushPoints is { Count: > 1 }
            ? new MosaicAnnotation(
                _brushPoints.ToArray(),
                GetMosaicStrokeWidth(),
                BlockSize: Math.Clamp(
                    (int)Math.Round(_strokeWidth * 1.5),
                    4,
                    18))
            : null;
    }

    private void StartTextInput(WpfPoint point)
    {
        var input = new WpfTextBox
        {
            Width = Math.Max(60, Math.Min(220, Width - point.X)),
            MinHeight = 34,
            Padding = new Thickness(6, 4, 6, 4),
            Background = WpfBrushes.Transparent,
            BorderBrush = new SolidColorBrush(_selectedColor),
            BorderThickness = new Thickness(1),
            FontSize = Math.Max(14, _strokeWidth * 5),
            Foreground = new SolidColorBrush(_selectedColor),
        };

        input.KeyDown += OnTextInputKeyDown;
        input.LostKeyboardFocus += OnTextInputLostKeyboardFocus;
        _activeTextInput = input;
        _activeTextPosition = point;
        SetLeft(input, point.X);
        SetTop(input, point.Y);
        Children.Add(input);
        input.Focus();
    }

    private void OnTextInputKeyDown(object sender, WpfKeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CommitActiveTextInput(keepText: true);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            CommitActiveTextInput(keepText: false);
            e.Handled = true;
        }
    }

    private void OnTextInputLostKeyboardFocus(
        object sender,
        KeyboardFocusChangedEventArgs e)
    {
        CommitActiveTextInput(keepText: true);
    }

    private void CommitActiveTextInput(bool keepText)
    {
        if (_activeTextInput is null)
        {
            return;
        }

        var input = _activeTextInput;
        _activeTextInput = null;
        input.KeyDown -= OnTextInputKeyDown;
        input.LostKeyboardFocus -= OnTextInputLostKeyboardFocus;
        Children.Remove(input);

        if (!keepText || string.IsNullOrWhiteSpace(input.Text))
        {
            return;
        }

        _document.Add(new TextAnnotation(
            _activeTextPosition,
            input.Text.Trim(),
            _selectedColor,
            input.FontSize));
        RebuildCanvas();
        RaiseHistoryChanged();
    }

    private void RebuildCanvas()
    {
        Children.Clear();

        if (_capturedImage is null)
        {
            return;
        }

        Children.Add(new WpfImage
        {
            Source = _capturedImage.Preview,
            Width = _capturedImage.Preview.PixelWidth,
            Height = _capturedImage.Preview.PixelHeight,
            Stretch = Stretch.Fill,
            IsHitTestVisible = false,
        });

        foreach (var annotation in _document.Annotations)
        {
            AddAnnotationVisual(annotation);
        }
    }

    private static Rect GetAnnotationBounds(EditorAnnotation annotation)
    {
        return annotation switch
        {
            RectangleAnnotation rectangle => Inflate(
                rectangle.Bounds,
                (rectangle.StrokeWidth / 2) + 1),
            ArrowAnnotation arrow => BoundsFromPoints(
                [arrow.Start, arrow.End],
                Math.Max(10, arrow.StrokeWidth * 4) + arrow.StrokeWidth),
            BrushAnnotation brush => BoundsFromPoints(
                brush.Points,
                (brush.StrokeWidth / 2) + 1),
            MosaicAnnotation mosaic => BoundsFromPoints(
                mosaic.Points,
                (mosaic.StrokeWidth / 2) + 2),
            TextAnnotation text => GetTextBounds(text),
            EmojiAnnotation emoji => new Rect(
                emoji.Position.X - (emoji.FontSize / 2),
                emoji.Position.Y - (emoji.FontSize / 2),
                emoji.FontSize,
                emoji.FontSize),
            TranslationOverlayAnnotation translation => translation.Regions
                .Select(region => region.Bounds)
                .Aggregate(Rect.Empty, Rect.Union),
            _ => Rect.Empty,
        };
    }

    private static Rect GetTextBounds(TextAnnotation annotation)
    {
        var lines = annotation.Text.Replace("\r", string.Empty).Split('\n');
        var longestLine = Math.Max(1, lines.Max(line => line.Length));
        return new Rect(
            annotation.Position,
            new WpfSize(
                Math.Max(annotation.FontSize, longestLine * annotation.FontSize),
                Math.Max(annotation.FontSize, lines.Length * annotation.FontSize * 1.35)));
    }

    private static Rect BoundsFromPoints(
        IReadOnlyList<WpfPoint> points,
        double padding)
    {
        if (points.Count == 0)
        {
            return Rect.Empty;
        }

        var bounds = new Rect(points[0], points[0]);
        foreach (var point in points.Skip(1))
        {
            bounds.Union(point);
        }

        return Inflate(bounds, padding);
    }

    private static Rect Inflate(Rect bounds, double padding)
    {
        if (bounds.IsEmpty)
        {
            return bounds;
        }

        bounds.Inflate(padding, padding);
        return bounds;
    }

    private static EditorAnnotation TranslateAnnotation(
        EditorAnnotation annotation,
        Vector offset)
    {
        return annotation switch
        {
            RectangleAnnotation rectangle => rectangle with
            {
                Bounds = new Rect(rectangle.Bounds.TopLeft + offset, rectangle.Bounds.Size),
            },
            ArrowAnnotation arrow => arrow with
            {
                Start = arrow.Start + offset,
                End = arrow.End + offset,
            },
            BrushAnnotation brush => brush with
            {
                Points = brush.Points.Select(point => point + offset).ToArray(),
            },
            TextAnnotation text => text with
            {
                Position = text.Position + offset,
            },
            EmojiAnnotation emoji => emoji with
            {
                Position = emoji.Position + offset,
            },
            TranslationOverlayAnnotation translation => translation with
            {
                Regions = translation.Regions.Select(region => region with
                {
                    Bounds = new Rect(region.Bounds.TopLeft + offset, region.Bounds.Size),
                }).ToArray(),
            },
            MosaicAnnotation mosaic => mosaic with
            {
                Points = mosaic.Points.Select(point => point + offset).ToArray(),
            },
            _ => annotation,
        };
    }

    private void ApplyDisplayTransform()
    {
        if (Width <= 0 || Height <= 0)
        {
            return;
        }

        RenderTransform = new ScaleTransform(
            DisplayWidth / Width,
            DisplayHeight / Height);
    }

    private void AddAnnotationVisual(EditorAnnotation annotation)
    {
        switch (annotation)
        {
            case RectangleAnnotation rectangle:
                AddRectangleVisual(rectangle);
                break;
            case ArrowAnnotation arrow:
                AddArrowVisual(arrow);
                break;
            case BrushAnnotation brush:
                AddBrushVisual(brush);
                break;
            case TextAnnotation text:
                AddTextVisual(text);
                break;
            case EmojiAnnotation emoji:
                AddEmojiVisual(emoji);
                break;
            case TranslationOverlayAnnotation translation:
                AddTranslationOverlayVisual(translation);
                break;
            case MosaicAnnotation mosaic:
                AddMosaicVisual(mosaic);
                break;
        }
    }

    private void AddRectangleVisual(RectangleAnnotation annotation)
    {
        var rectangle = new WpfRectangle
        {
            Width = annotation.Bounds.Width,
            Height = annotation.Bounds.Height,
            Stroke = new SolidColorBrush(annotation.StrokeColor),
            StrokeThickness = annotation.StrokeWidth,
            Fill = WpfBrushes.Transparent,
            IsHitTestVisible = false,
        };
        SetLeft(rectangle, annotation.Bounds.X);
        SetTop(rectangle, annotation.Bounds.Y);
        Children.Add(rectangle);
    }

    private void AddArrowVisual(ArrowAnnotation annotation)
    {
        Children.Add(new Line
        {
            X1 = annotation.Start.X,
            Y1 = annotation.Start.Y,
            X2 = annotation.End.X,
            Y2 = annotation.End.Y,
            Stroke = new SolidColorBrush(annotation.StrokeColor),
            StrokeThickness = annotation.StrokeWidth,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            IsHitTestVisible = false,
        });

        var direction = annotation.End - annotation.Start;

        if (direction.Length < 1)
        {
            return;
        }

        direction.Normalize();
        var perpendicular = new Vector(-direction.Y, direction.X);
        var headLength = Math.Max(10, annotation.StrokeWidth * 4);
        var headWidth = headLength * 0.55;
        var left = annotation.End - (direction * headLength) +
                   (perpendicular * headWidth);
        var right = annotation.End - (direction * headLength) -
                    (perpendicular * headWidth);
        Children.Add(new Polygon
        {
            Fill = new SolidColorBrush(annotation.StrokeColor),
            Points = [annotation.End, left, right],
            IsHitTestVisible = false,
        });
    }

    private void AddBrushVisual(BrushAnnotation annotation)
    {
        Children.Add(new Polyline
        {
            Points = new PointCollection(annotation.Points),
            Stroke = new SolidColorBrush(annotation.StrokeColor),
            StrokeThickness = annotation.StrokeWidth,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            IsHitTestVisible = false,
        });
    }

    private void AddTextVisual(TextAnnotation annotation)
    {
        var text = new TextBlock
        {
            Text = annotation.Text,
            FontSize = annotation.FontSize,
            Foreground = new SolidColorBrush(annotation.Color),
            IsHitTestVisible = false,
        };
        SetLeft(text, annotation.Position.X);
        SetTop(text, annotation.Position.Y);
        Children.Add(text);
    }

    private void AddEmojiVisual(EmojiAnnotation annotation)
    {
        var image = new WpfImage
        {
            Source = EmojiStickerRenderer.GetImage(annotation.Sticker),
            Width = annotation.FontSize,
            Height = annotation.FontSize,
            Stretch = Stretch.Uniform,
            IsHitTestVisible = false,
        };
        SetLeft(image, annotation.Position.X - (annotation.FontSize / 2));
        SetTop(image, annotation.Position.Y - (annotation.FontSize / 2));
        Children.Add(image);
    }

    private void AddTranslationOverlayVisual(
        TranslationOverlayAnnotation annotation)
    {
        foreach (var region in annotation.Regions)
        {
            var palette = GetTranslationPalette(region.Bounds);
            var contentWidth = Math.Max(8, region.Bounds.Width - 6);
            var contentHeight = Math.Max(8, region.Bounds.Height - 2);
            var fittedFontSize = TranslationTextLayout.FitFontSize(
                region.Text,
                contentWidth,
                contentHeight,
                region.FontSize);
            var text = new TextBlock
            {
                Text = region.Text,
                FontFamily = new System.Windows.Media.FontFamily(
                    "Microsoft YaHei UI"),
                FontSize = fittedFontSize,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(palette.Foreground),
                Width = contentWidth,
                Height = contentHeight,
                TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                LineHeight = fittedFontSize * 1.12,
                LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                ClipToBounds = true,
                IsHitTestVisible = false,
            };
            var border = new Border
            {
                Width = Math.Max(12, region.Bounds.Width),
                Height = Math.Max(12, region.Bounds.Height),
                Padding = new Thickness(3, 1, 3, 1),
                Background = new SolidColorBrush(palette.Background),
                CornerRadius = new CornerRadius(2),
                ClipToBounds = true,
                IsHitTestVisible = false,
                Child = text,
            };
            SetLeft(border, region.Bounds.X);
            SetTop(border, region.Bounds.Y);
            Children.Add(border);
        }
    }

    private (WpfColor Background, WpfColor Foreground)
        GetTranslationPalette(Rect bounds)
    {
        if (_capturedImage is null)
        {
            return (
                WpfColor.FromArgb(246, 35, 42, 46),
                WpfColor.FromRgb(248, 251, 251));
        }

        var bitmap = _capturedImage.Bitmap;
        var left = Math.Clamp((int)Math.Floor(bounds.Left), 0, bitmap.Width - 1);
        var top = Math.Clamp((int)Math.Floor(bounds.Top), 0, bitmap.Height - 1);
        var right = Math.Clamp((int)Math.Ceiling(bounds.Right), left + 1, bitmap.Width);
        var bottom = Math.Clamp((int)Math.Ceiling(bounds.Bottom), top + 1, bitmap.Height);
        var stepX = Math.Max(1, (right - left) / 10);
        var stepY = Math.Max(1, (bottom - top) / 5);
        long red = 0;
        long green = 0;
        long blue = 0;
        var samples = 0;

        for (var y = top; y < bottom; y += stepY)
        {
            for (var x = left; x < right; x += stepX)
            {
                var pixel = bitmap.GetPixel(x, y);
                if (pixel.A < 32)
                {
                    continue;
                }

                red += pixel.R;
                green += pixel.G;
                blue += pixel.B;
                samples++;
            }
        }

        if (samples == 0)
        {
            return (
                WpfColor.FromArgb(246, 35, 42, 46),
                WpfColor.FromRgb(248, 251, 251));
        }

        var averageRed = (byte)(red / samples);
        var averageGreen = (byte)(green / samples);
        var averageBlue = (byte)(blue / samples);
        var luminance =
            (0.2126 * averageRed) +
            (0.7152 * averageGreen) +
            (0.0722 * averageBlue);
        if (luminance < 145)
        {
            return (
                WpfColor.FromArgb(
                    246,
                    (byte)(averageRed * 0.82),
                    (byte)(averageGreen * 0.82),
                    (byte)(averageBlue * 0.82)),
                WpfColor.FromRgb(248, 251, 251));
        }

        return (
            WpfColor.FromArgb(
                246,
                (byte)Math.Min(255, averageRed + 12),
                (byte)Math.Min(255, averageGreen + 12),
                (byte)Math.Min(255, averageBlue + 12)),
            WpfColor.FromRgb(25, 32, 36));
    }

    private void AddMosaicVisual(MosaicAnnotation annotation)
    {
        if (_capturedImage is null)
        {
            return;
        }

        if (annotation.Points.Count < 2)
        {
            return;
        }

        var radius = (annotation.StrokeWidth / 2) + 2;
        var left = Math.Max(0, (int)Math.Floor(
            annotation.Points.Min(point => point.X) - radius));
        var top = Math.Max(0, (int)Math.Floor(
            annotation.Points.Min(point => point.Y) - radius));
        var right = Math.Min(_capturedImage.Bitmap.Width, (int)Math.Ceiling(
            annotation.Points.Max(point => point.X) + radius));
        var bottom = Math.Min(_capturedImage.Bitmap.Height, (int)Math.Ceiling(
            annotation.Points.Max(point => point.Y) + radius));
        var bounds = new Int32Rect(
            left,
            top,
            Math.Max(1, right - left),
            Math.Max(1, bottom - top));
        var path = new StreamGeometry();
        using (var context = path.Open())
        {
            context.BeginFigure(
                new WpfPoint(
                    annotation.Points[0].X - bounds.X,
                    annotation.Points[0].Y - bounds.Y),
                isFilled: false,
                isClosed: false);
            context.PolyLineTo(
                annotation.Points
                    .Skip(1)
                    .Select(point => new WpfPoint(
                        point.X - bounds.X,
                        point.Y - bounds.Y))
                    .ToArray(),
                isStroked: true,
                isSmoothJoin: true);
        }

        var clipPen = new WpfPen(WpfBrushes.Black, annotation.StrokeWidth)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
        };
        var clip = path.GetWidenedPathGeometry(clipPen);
        clip.Freeze();

        var image = new WpfImage
        {
            Source = MosaicRenderer.Create(
                _capturedImage.Bitmap,
                bounds,
                annotation.BlockSize),
            Width = bounds.Width,
            Height = bounds.Height,
            Stretch = Stretch.Fill,
            Clip = clip,
            IsHitTestVisible = false,
        };
        SetLeft(image, bounds.X);
        SetTop(image, bounds.Y);
        Children.Add(image);
    }

    private double GetMosaicStrokeWidth() => Math.Max(8, _strokeWidth * 4);

    private WpfPoint ClampPoint(WpfPoint point)
    {
        return new WpfPoint(
            Math.Clamp(point.X, 0, Width),
            Math.Clamp(point.Y, 0, Height));
    }

    private void RaiseHistoryChanged()
    {
        HistoryChanged?.Invoke(this, EventArgs.Empty);
    }

    private static Rect CreateBounds(WpfPoint firstPoint, WpfPoint secondPoint)
    {
        return new Rect(firstPoint, secondPoint);
    }
}
