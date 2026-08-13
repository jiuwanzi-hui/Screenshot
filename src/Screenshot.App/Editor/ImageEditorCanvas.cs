using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Globalization;
using Screenshot.App.Capture;
using Screenshot.App.Core;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfColor = System.Windows.Media.Color;
using WpfCursors = System.Windows.Input.Cursors;
using WpfImage = System.Windows.Controls.Image;
using WpfKeyEventArgs = System.Windows.Input.KeyEventArgs;
using WpfMouseEventArgs = System.Windows.Input.MouseEventArgs;
using WpfPen = System.Windows.Media.Pen;
using WpfPoint = System.Windows.Point;
using WpfRectangle = System.Windows.Shapes.Rectangle;
using WpfEllipse = System.Windows.Shapes.Ellipse;
using WpfSize = System.Windows.Size;
using WpfTextBox = System.Windows.Controls.TextBox;

namespace Screenshot.App.Editor;

public sealed class ImageEditorCanvas : Canvas
{
    private CapturedImage? _capturedImage;
    private EditorDocument _document = new();
    private EditorTool _selectedTool = EditorTool.Rectangle;
    private bool _isAnnotationCreationEnabled = true;
    private WpfColor _selectedColor = WpfColor.FromRgb(214, 69, 69);
    private string _selectedEmoji = EmojiStickerCatalog.Default;
    private double _strokeWidth = 3;
    private ArrowStyle _arrowStyle = ArrowStyle.Filled;
    private WpfPoint _drawingStartPoint;
    private List<WpfPoint>? _brushPoints;
    private UIElement? _drawingPreview;
    private WpfTextBox? _activeTextInput;
    private WpfPoint _activeTextPosition;
    private bool _isDrawing;
    private int _selectedAnnotationIndex = -1;
    private int _activeAnnotationHandle = -1;
    private bool _isEditingAnnotation;
    private WpfPoint _annotationEditStartPoint;
    private EditorAnnotation? _annotationEditOriginal;
    private double _baseDisplayWidth;
    private double _baseDisplayHeight;
    private double _zoom = 1;
    private bool _isTranslationOverlayVisible = true;

    public ImageEditorCanvas()
    {
        Background = WpfBrushes.Transparent;
        ClipToBounds = true;
        Cursor = WpfCursors.Cross;
        Focusable = true;
    }

    public event EventHandler? HistoryChanged;

    public event EventHandler? AnnotationSelectionChanged;

    public bool HasImage => _capturedImage is not null;

    public bool CanUndo => _document.CanUndo;

    public bool CanRedo => _document.CanRedo;

    public bool HasSelectedAnnotation =>
        _selectedAnnotationIndex >= 0 &&
        _selectedAnnotationIndex < _document.Annotations.Count;

    public bool HasTranslationOverlay =>
        _document.Annotations.Any(annotation =>
            annotation is TranslationOverlayAnnotation);

    public bool IsTranslationOverlayVisible =>
        HasTranslationOverlay && _isTranslationOverlayVisible;

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
        _isTranslationOverlayVisible = true;
        RenderTransformOrigin = new WpfPoint(0, 0);
        ApplyDisplayTransform();
        RebuildCanvas();
        UpdateInteractionCursor(new WpfPoint(-1, -1));
        RaiseHistoryChanged();
    }

    public Rect? GetAnnotationBounds()
    {
        CommitPendingText();
        Rect? combinedBounds = null;

        foreach (var annotation in _document.Annotations)
        {
            if (annotation is TranslationOverlayAnnotation &&
                !_isTranslationOverlayVisible)
            {
                continue;
            }

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
        if (_isEditingAnnotation)
        {
            CommitAnnotationEdit();
        }

        _selectedTool = tool;
        var hadSelection = HasSelectedAnnotation;
        _selectedAnnotationIndex = -1;
        _activeAnnotationHandle = -1;
        RebuildCanvas();
        UpdateInteractionCursor(new WpfPoint(-1, -1));
        if (hadSelection)
        {
            AnnotationSelectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void SetAnnotationCreationEnabled(bool isEnabled)
    {
        _isAnnotationCreationEnabled = isEnabled;
        if (!isEnabled)
        {
            _ = CancelActiveOperation();
        }
    }

    public void SelectColor(WpfColor color)
    {
        _selectedColor = color;
    }

    public void SelectEmoji(string emoji)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(emoji);
        _selectedEmoji = emoji;
    }

    public void SetStrokeWidth(double strokeWidth)
    {
        _strokeWidth = Math.Clamp(strokeWidth, 1, 24);
    }

    public void SelectArrowStyle(
        ArrowStyle arrowStyle,
        bool updateSelectedAnnotation = true)
    {
        _arrowStyle = Enum.IsDefined(arrowStyle)
            ? arrowStyle
            : ArrowStyle.Filled;
        if (!updateSelectedAnnotation ||
            !HasSelectedAnnotation ||
            _document.Annotations[_selectedAnnotationIndex] is not ArrowAnnotation arrow ||
            arrow.Style == _arrowStyle)
        {
            return;
        }

        _document.ReplaceAt(
            _selectedAnnotationIndex,
            arrow,
            arrow with { Style = _arrowStyle });
        RebuildCanvas();
        RaiseHistoryChanged();
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
        _isTranslationOverlayVisible = true;
        _document.Add(new TranslationOverlayAnnotation(validRegions));
        RebuildCanvas();
        RaiseHistoryChanged();
    }

    public void SetTranslationOverlayVisible(bool isVisible)
    {
        if (!HasTranslationOverlay ||
            _isTranslationOverlayVisible == isVisible)
        {
            return;
        }

        CommitPendingText();
        _isTranslationOverlayVisible = isVisible;
        RebuildCanvas();
    }

    public void Undo()
    {
        CommitPendingText();
        ClearAnnotationSelection();
        _document.Undo();
        RebuildCanvas();
        RaiseHistoryChanged();
    }

    public void Redo()
    {
        CommitPendingText();
        ClearAnnotationSelection();
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

    public bool DeleteSelectedAnnotation()
    {
        if (!HasSelectedAnnotation)
        {
            return false;
        }

        if (_isEditingAnnotation)
        {
            CommitAnnotationEdit();
        }

        _document.RemoveAt(_selectedAnnotationIndex);
        _selectedAnnotationIndex = -1;
        _activeAnnotationHandle = -1;
        RebuildCanvas();
        AnnotationSelectionChanged?.Invoke(this, EventArgs.Empty);
        RaiseHistoryChanged();
        Focus();
        return true;
    }

    private void ClearAnnotationSelection()
    {
        if (!HasSelectedAnnotation)
        {
            return;
        }

        _selectedAnnotationIndex = -1;
        _activeAnnotationHandle = -1;
        _isEditingAnnotation = false;
        _annotationEditOriginal = null;
        AnnotationSelectionChanged?.Invoke(this, EventArgs.Empty);
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

        if (_isEditingAnnotation)
        {
            CancelAnnotationEdit();
            wasCanceled = true;
        }

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
        _selectedAnnotationIndex = -1;
        _activeAnnotationHandle = -1;
        _isEditingAnnotation = false;
        _annotationEditOriginal = null;
        _baseDisplayWidth = 0;
        _baseDisplayHeight = 0;
        _zoom = 1;
        _isTranslationOverlayVisible = true;
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
        var renderer = new ImageEditorCanvas
        {
            _capturedImage = _capturedImage,
            _document = _document,
            _isTranslationOverlayVisible = _isTranslationOverlayVisible,
            Width = _capturedImage.Preview.PixelWidth,
            Height = _capturedImage.Preview.PixelHeight,
            RenderTransform = Transform.Identity,
        };

        try
        {
            renderer.RebuildCanvasCore(includeSelection: false);
            renderer.Measure(new WpfSize(renderer.Width, renderer.Height));
            renderer.Arrange(new Rect(0, 0, renderer.Width, renderer.Height));
            var renderedImage = new RenderTargetBitmap(
                _capturedImage.Preview.PixelWidth,
                _capturedImage.Preview.PixelHeight,
                96,
                96,
                PixelFormats.Pbgra32);
            renderedImage.Render(renderer);
            renderedImage.Freeze();
            return renderedImage;
        }
        finally
        {
            renderer.Children.Clear();
            renderer._capturedImage = null;
            renderer._document = new EditorDocument();
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);

        if (_capturedImage is null)
        {
            return;
        }

        Focus();

        CommitPendingText();
        var point = ClampPoint(e.GetPosition(this));

        if (BeginAnnotationEdit(point))
        {
            e.Handled = true;
            return;
        }

        if (!_isAnnotationCreationEnabled)
        {
            return;
        }

        UpdateInteractionCursor(point);

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

        if (_selectedTool == EditorTool.Number)
        {
            _document.Add(new NumberAnnotation(
                point,
                Math.Max(24, _strokeWidth * 9),
                _selectedColor));
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
        var point = ClampPoint(e.GetPosition(this));
        UpdateInteractionCursor(point);

        if (_isEditingAnnotation)
        {
            UpdateAnnotationEdit(point);
            e.Handled = true;
            return;
        }

        if (!_isDrawing || _drawingPreview is null)
        {
            return;
        }

        switch (_selectedTool)
        {
            case EditorTool.Rectangle:
                UpdateRectanglePreview((WpfRectangle)_drawingPreview, point);
                break;
            case EditorTool.Ellipse:
                UpdateEllipsePreview((WpfEllipse)_drawingPreview, point);
                break;
            case EditorTool.Arrow:
                UpdateArrowPreview((Polygon)_drawingPreview, point);
                break;
            case EditorTool.Brush:
            case EditorTool.Mosaic:
                UpdateBrushPreview((Polyline)_drawingPreview, point);
                break;
        }
    }

    protected override void OnKeyDown(WpfKeyEventArgs e)
    {
        if (e.Key == Key.Delete && DeleteSelectedAnnotation())
        {
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);

        if (_isEditingAnnotation)
        {
            var point = ClampPoint(e.GetPosition(this));
            UpdateAnnotationEdit(point);
            CommitAnnotationEdit();
            UpdateInteractionCursor(point);
            e.Handled = true;
            return;
        }

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

    private bool BeginAnnotationEdit(WpfPoint point)
    {
        var hitIndex = HitTestAnnotation(point);
        if (hitIndex < 0)
        {
            var hadSelection = HasSelectedAnnotation;
            _selectedAnnotationIndex = -1;
            if (hadSelection)
            {
                RebuildCanvas();
                AnnotationSelectionChanged?.Invoke(this, EventArgs.Empty);
            }
            return false;
        }

        var selectionChanged = _selectedAnnotationIndex != hitIndex;
        _selectedAnnotationIndex = hitIndex;
        var annotation = _document.Annotations[hitIndex];
        _activeAnnotationHandle = GetAnnotationHandle(annotation, point);
        _annotationEditStartPoint = point;
        _annotationEditOriginal = annotation;
        _isEditingAnnotation = true;
        CaptureMouse();
        RebuildCanvas();
        UpdateInteractionCursor(point);
        if (selectionChanged)
        {
            AnnotationSelectionChanged?.Invoke(this, EventArgs.Empty);
        }
        return true;
    }

    private void UpdateAnnotationEdit(WpfPoint point)
    {
        if (!_isEditingAnnotation ||
            _annotationEditOriginal is not { } original ||
            _selectedAnnotationIndex < 0)
        {
            return;
        }

        var delta = point - _annotationEditStartPoint;
        var updated = original switch
        {
            RectangleAnnotation rectangle => rectangle with
            {
                Bounds = _activeAnnotationHandle < 0
                    ? new Rect(
                        rectangle.Bounds.TopLeft + delta,
                        rectangle.Bounds.Size)
                    : ResizeRectangle(
                        rectangle.Bounds,
                        _activeAnnotationHandle,
                        point),
            },
            EllipseAnnotation ellipse => ellipse with
            {
                Bounds = _activeAnnotationHandle < 0
                    ? new Rect(
                        ellipse.Bounds.TopLeft + delta,
                        ellipse.Bounds.Size)
                    : ResizeRectangle(
                        ellipse.Bounds,
                        _activeAnnotationHandle,
                        point),
            },
            ArrowAnnotation arrow => _activeAnnotationHandle switch
            {
                0 => arrow with { Start = point },
                1 => arrow with { End = point },
                _ => arrow with
                {
                    Start = arrow.Start + delta,
                    End = arrow.End + delta,
                },
            },
            TextAnnotation text => _activeAnnotationHandle == 8
                ? text with
                {
                    FontSize = Math.Clamp(
                        text.FontSize + ((delta.X + delta.Y) * 0.25),
                        8,
                        256),
                }
                : text with { Position = text.Position + delta },
            EmojiAnnotation emoji => _activeAnnotationHandle == 8
                ? emoji with
                {
                    FontSize = Math.Clamp(
                        emoji.FontSize + ((delta.X + delta.Y) * 0.5),
                        12,
                        512),
                }
                : emoji with { Position = emoji.Position + delta },
            NumberAnnotation number => _activeAnnotationHandle == 8
                ? number with
                {
                    Size = Math.Clamp(
                        number.Size + ((delta.X + delta.Y) * 0.5),
                        12,
                        512),
                }
                : number with { Position = number.Position + delta },
            _ => original,
        };

        _document.SetAt(_selectedAnnotationIndex, updated);
        RebuildCanvas();
    }

    private void CommitAnnotationEdit()
    {
        if (!_isEditingAnnotation)
        {
            return;
        }

        if (Mouse.Captured == this)
        {
            ReleaseMouseCapture();
        }

        var index = _selectedAnnotationIndex;
        var original = _annotationEditOriginal;
        var current = index >= 0 && index < _document.Annotations.Count
            ? _document.Annotations[index]
            : null;
        _isEditingAnnotation = false;
        _activeAnnotationHandle = -1;
        _annotationEditOriginal = null;

        if (index >= 0 && original is not null && current is not null &&
            !Equals(original, current))
        {
            _document.ReplaceAt(index, original, current);
        }

        RebuildCanvas();
        RaiseHistoryChanged();
    }

    private void CancelAnnotationEdit()
    {
        if (!_isEditingAnnotation)
        {
            return;
        }

        if (Mouse.Captured == this)
        {
            ReleaseMouseCapture();
        }

        if (_selectedAnnotationIndex >= 0 &&
            _annotationEditOriginal is not null)
        {
            _document.SetAt(
                _selectedAnnotationIndex,
                _annotationEditOriginal);
        }

        _isEditingAnnotation = false;
        _activeAnnotationHandle = -1;
        _annotationEditOriginal = null;
        RebuildCanvas();
    }

    private int HitTestAnnotation(WpfPoint point)
    {
        for (var index = _document.Annotations.Count - 1; index >= 0; index--)
        {
            var annotation = _document.Annotations[index];
            if (annotation is not (
                RectangleAnnotation or
                EllipseAnnotation or
                ArrowAnnotation or
                TextAnnotation or
                EmojiAnnotation or
                NumberAnnotation))
            {
                continue;
            }

            if (IsAnnotationHit(annotation, point))
            {
                return index;
            }
        }

        return -1;
    }

    private static bool IsAnnotationHit(
        EditorAnnotation annotation,
        WpfPoint point)
    {
        return annotation switch
        {
            RectangleAnnotation rectangle => IsRectangleBorderHit(
                rectangle.Bounds,
                point,
                Math.Max(3, (rectangle.StrokeWidth / 2) + 2)),
            EllipseAnnotation ellipse => IsEllipseBorderHit(
                ellipse.Bounds,
                point,
                Math.Max(3, (ellipse.StrokeWidth / 2) + 2)),
            ArrowAnnotation arrow => DistanceToSegment(
                point,
                arrow.Start,
                arrow.End) <= Math.Max(5, (arrow.StrokeWidth * 2) + 2),
            TextAnnotation text => Inflate(
                GetTextBounds(text),
                3).Contains(point),
            EmojiAnnotation emoji => Inflate(
                GetAnnotationBounds(emoji),
                3).Contains(point),
            NumberAnnotation number => Inflate(
                GetAnnotationBounds(number),
                3).Contains(point),
            _ => false,
        };
    }

    private static bool IsEllipseBorderHit(
        Rect bounds,
        WpfPoint point,
        double tolerance)
    {
        if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0)
        {
            return false;
        }

        var centerX = bounds.Left + (bounds.Width / 2);
        var centerY = bounds.Top + (bounds.Height / 2);
        var radiusX = bounds.Width / 2;
        var radiusY = bounds.Height / 2;
        var normalized = Math.Sqrt(
            Math.Pow((point.X - centerX) / radiusX, 2) +
            Math.Pow((point.Y - centerY) / radiusY, 2));
        var normalizedTolerance = tolerance / Math.Max(1, Math.Min(radiusX, radiusY));
        return Math.Abs(normalized - 1) <= normalizedTolerance;
    }

    private static bool IsRectangleBorderHit(
        Rect bounds,
        WpfPoint point,
        double tolerance)
    {
        if (bounds.IsEmpty)
        {
            return false;
        }

        var outer = bounds;
        outer.Inflate(tolerance, tolerance);
        if (!outer.Contains(point))
        {
            return false;
        }

        var inner = bounds;
        inner.Inflate(-tolerance, -tolerance);
        return inner.Width <= 0 || inner.Height <= 0 || !inner.Contains(point);
    }

    private static int GetAnnotationHandle(
        EditorAnnotation annotation,
        WpfPoint point)
    {
        var handlePoints = GetAnnotationHandlePoints(annotation);
        for (var index = 0; index < handlePoints.Count; index++)
        {
            if ((handlePoints[index] - point).Length <= 7)
            {
                return annotation is TextAnnotation or EmojiAnnotation or NumberAnnotation
                    ? 8
                    : index;
            }
        }

        return -1;
    }

    private void UpdateInteractionCursor(WpfPoint point)
    {
        if (_selectedAnnotationIndex >= 0 &&
            _selectedAnnotationIndex < _document.Annotations.Count)
        {
            var selected = _document.Annotations[_selectedAnnotationIndex];
            var handle = _isEditingAnnotation
                ? _activeAnnotationHandle
                : GetAnnotationHandle(selected, point);
            if (handle >= 0)
            {
                Cursor = GetHandleCursor(selected, handle);
                return;
            }

            if (_isEditingAnnotation || IsAnnotationHit(selected, point))
            {
                Cursor = WpfCursors.SizeAll;
                return;
            }
        }

        if (HitTestAnnotation(point) >= 0)
        {
            Cursor = WpfCursors.Hand;
            return;
        }

        if (!_isAnnotationCreationEnabled)
        {
            Cursor = WpfCursors.Arrow;
            return;
        }

        Cursor = _selectedTool == EditorTool.Text
            ? WpfCursors.IBeam
            : WpfCursors.Cross;
    }

    private static System.Windows.Input.Cursor GetHandleCursor(
        EditorAnnotation annotation,
        int handle)
    {
        return annotation switch
        {
            RectangleAnnotation or EllipseAnnotation => handle switch
            {
                0 or 4 => WpfCursors.SizeNWSE,
                2 or 6 => WpfCursors.SizeNESW,
                1 or 5 => WpfCursors.SizeNS,
                3 or 7 => WpfCursors.SizeWE,
                _ => WpfCursors.SizeAll,
            },
            TextAnnotation or EmojiAnnotation or NumberAnnotation => WpfCursors.SizeNWSE,
            ArrowAnnotation => WpfCursors.Cross,
            _ => WpfCursors.SizeAll,
        };
    }

    private static IReadOnlyList<WpfPoint> GetAnnotationHandlePoints(
        EditorAnnotation annotation)
    {
        return annotation switch
        {
            RectangleAnnotation rectangle => GetRectangleHandlePoints(
                rectangle.Bounds),
            EllipseAnnotation ellipse => GetRectangleHandlePoints(
                ellipse.Bounds),
            ArrowAnnotation arrow => [arrow.Start, arrow.End],
            TextAnnotation text =>
            [
                new WpfPoint(
                    GetTextBounds(text).Right,
                    GetTextBounds(text).Bottom),
            ],
            EmojiAnnotation emoji =>
            [
                new WpfPoint(
                    emoji.Position.X + (emoji.FontSize / 2),
                    emoji.Position.Y + (emoji.FontSize / 2)),
            ],
            NumberAnnotation number =>
            [
                new WpfPoint(
                    number.Position.X + number.Size,
                    number.Position.Y + number.Size),
            ],
            _ => [],
        };
    }

    private static IReadOnlyList<WpfPoint> GetRectangleHandlePoints(Rect bounds)
    {
        return
        [
            bounds.TopLeft,
            new WpfPoint(bounds.Left + (bounds.Width / 2), bounds.Top),
            bounds.TopRight,
            new WpfPoint(bounds.Right, bounds.Top + (bounds.Height / 2)),
            bounds.BottomRight,
            new WpfPoint(bounds.Left + (bounds.Width / 2), bounds.Bottom),
            bounds.BottomLeft,
            new WpfPoint(bounds.Left, bounds.Top + (bounds.Height / 2)),
        ];
    }

    private static Rect ResizeRectangle(
        Rect original,
        int handle,
        WpfPoint point)
    {
        if (handle < 0 || handle > 7)
        {
            return original;
        }

        var left = original.Left;
        var top = original.Top;
        var right = original.Right;
        var bottom = original.Bottom;
        if (handle is 0 or 6 or 7)
        {
            left = point.X;
        }
        if (handle is 0 or 1 or 2)
        {
            top = point.Y;
        }
        if (handle is 2 or 3 or 4)
        {
            right = point.X;
        }
        if (handle is 4 or 5 or 6)
        {
            bottom = point.Y;
        }

        const double minimumSize = 2;
        if (right - left < minimumSize)
        {
            if (handle is 0 or 6 or 7)
            {
                left = right - minimumSize;
            }
            else
            {
                right = left + minimumSize;
            }
        }
        if (bottom - top < minimumSize)
        {
            if (handle is 0 or 1 or 2)
            {
                top = bottom - minimumSize;
            }
            else
            {
                bottom = top + minimumSize;
            }
        }

        return new Rect(
            new WpfPoint(left, top),
            new WpfPoint(right, bottom));
    }

    private void AddSelectionVisual()
    {
        if (_selectedAnnotationIndex < 0 ||
            _selectedAnnotationIndex >= _document.Annotations.Count)
        {
            return;
        }

        var annotation = _document.Annotations[_selectedAnnotationIndex];
        var handles = GetAnnotationHandlePoints(annotation);
        for (var index = 0; index < handles.Count; index++)
        {
            var handle = new Ellipse
            {
                Width = 8,
                Height = 8,
                Fill = WpfBrushes.White,
                Stroke = new SolidColorBrush(WpfColor.FromRgb(7, 92, 89)),
                StrokeThickness = 1.5,
                IsHitTestVisible = false,
            };
            SetLeft(handle, handles[index].X - 4);
            SetTop(handle, handles[index].Y - 4);
            Children.Add(handle);
        }
    }

    private static double DistanceToSegment(
        WpfPoint point,
        WpfPoint start,
        WpfPoint end)
    {
        var delta = end - start;
        var lengthSquared = (delta.X * delta.X) + (delta.Y * delta.Y);
        if (lengthSquared <= 0.001)
        {
            return (point - start).Length;
        }

        var projection =
            (((point.X - start.X) * delta.X) +
             ((point.Y - start.Y) * delta.Y)) / lengthSquared;
        projection = Math.Clamp(projection, 0, 1);
        var closest = start + (delta * projection);
        return (point - closest).Length;
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
            EditorTool.Ellipse => new WpfEllipse
            {
                Stroke = new SolidColorBrush(_selectedColor),
                StrokeThickness = _strokeWidth,
                Fill = WpfBrushes.Transparent,
            },
            EditorTool.Arrow => CreateArrowPolygon(
                point,
                point,
                _selectedColor,
                _strokeWidth,
                _arrowStyle),
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

    private void UpdateEllipsePreview(
        WpfEllipse ellipse,
        WpfPoint currentPoint)
    {
        var bounds = CreateBounds(_drawingStartPoint, currentPoint);
        SetLeft(ellipse, bounds.X);
        SetTop(ellipse, bounds.Y);
        ellipse.Width = bounds.Width;
        ellipse.Height = bounds.Height;
    }

    private void UpdateArrowPreview(Polygon polygon, WpfPoint currentPoint)
    {
        polygon.Points = CreateTaperedArrowPoints(
            _drawingStartPoint,
            currentPoint,
            _strokeWidth);
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
            EditorTool.Ellipse => CreateEllipseAnnotation(endPoint),
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

    private EllipseAnnotation? CreateEllipseAnnotation(WpfPoint endPoint)
    {
        var bounds = CreateBounds(_drawingStartPoint, endPoint);
        return bounds.Width >= 2 && bounds.Height >= 2
            ? new EllipseAnnotation(bounds, _selectedColor, _strokeWidth)
            : null;
    }

    private ArrowAnnotation? CreateArrowAnnotation(WpfPoint endPoint)
    {
        return (_drawingStartPoint - endPoint).Length >= 2
            ? new ArrowAnnotation(
                _drawingStartPoint,
                endPoint,
                _selectedColor,
                _strokeWidth,
                _arrowStyle)
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
        RebuildCanvasCore(includeSelection: true);
    }

    private void RebuildCanvasCore(bool includeSelection)
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
            if (annotation is TranslationOverlayAnnotation &&
                !_isTranslationOverlayVisible)
            {
                continue;
            }

            AddAnnotationVisual(annotation);
        }

        if (includeSelection && _selectedAnnotationIndex >= 0)
        {
            AddSelectionVisual();
        }
    }

    private static Rect GetAnnotationBounds(EditorAnnotation annotation)
    {
        return annotation switch
        {
            RectangleAnnotation rectangle => Inflate(
                rectangle.Bounds,
                (rectangle.StrokeWidth / 2) + 1),
            EllipseAnnotation ellipse => Inflate(
                ellipse.Bounds,
                (ellipse.StrokeWidth / 2) + 1),
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
            NumberAnnotation number => new Rect(
                number.Position.X,
                number.Position.Y,
                number.Size,
                number.Size),
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
            EllipseAnnotation ellipse => ellipse with
            {
                Bounds = new Rect(ellipse.Bounds.TopLeft + offset, ellipse.Bounds.Size),
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
            NumberAnnotation number => number with
            {
                Position = number.Position + offset,
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
            case EllipseAnnotation ellipse:
                AddEllipseVisual(ellipse);
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
            case NumberAnnotation number:
                AddNumberVisual(number, GetNumberLabel(annotation));
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

    private void AddEllipseVisual(EllipseAnnotation annotation)
    {
        var ellipse = new WpfEllipse
        {
            Width = annotation.Bounds.Width,
            Height = annotation.Bounds.Height,
            Stroke = new SolidColorBrush(annotation.StrokeColor),
            StrokeThickness = annotation.StrokeWidth,
            Fill = WpfBrushes.Transparent,
            IsHitTestVisible = false,
        };
        SetLeft(ellipse, annotation.Bounds.X);
        SetTop(ellipse, annotation.Bounds.Y);
        Children.Add(ellipse);
    }

    private void AddArrowVisual(ArrowAnnotation annotation)
    {
        var polygon = CreateArrowPolygon(
            annotation.Start,
            annotation.End,
            annotation.StrokeColor,
            annotation.StrokeWidth,
            annotation.Style);
        polygon.IsHitTestVisible = false;
        Children.Add(polygon);
    }

    private static Polygon CreateArrowPolygon(
        WpfPoint start,
        WpfPoint end,
        WpfColor color,
        double strokeWidth,
        ArrowStyle style)
    {
        var brush = new SolidColorBrush(color);
        return new Polygon
        {
            Fill = style == ArrowStyle.Hollow
                ? WpfBrushes.Transparent
                : brush,
            Stroke = style == ArrowStyle.Hollow ? brush : null,
            StrokeThickness = style == ArrowStyle.Hollow
                ? Math.Max(1.5, strokeWidth * 0.55)
                : 0,
            StrokeLineJoin = PenLineJoin.Round,
            Points = CreateTaperedArrowPoints(start, end, strokeWidth),
        };
    }

    /// <summary>
    /// WeChat-style tapered arrow: one filled polygon that swells from a thin
    /// tail into a wide head base and ends in a solid triangular tip. A single
    /// shape cannot poke past its own tip, unlike the previous round-capped
    /// shaft drawn underneath a separate head triangle.
    /// </summary>
    private static PointCollection CreateTaperedArrowPoints(
        WpfPoint start,
        WpfPoint end,
        double strokeWidth)
    {
        var direction = end - start;
        var length = direction.Length;

        if (length < 1)
        {
            return [start, end];
        }

        direction.Normalize();
        var perpendicular = new Vector(-direction.Y, direction.X);
        // The head grows with the arrow itself, the way chat-app arrows do: a
        // long arrow gets a long, wide head, while the stroke width mostly
        // controls how much the shaft swells. Composed from Min/Max instead of
        // Math.Clamp: while the user is still dragging a tiny arrow the
        // length-derived maximum drops below the preferred minimum, and Clamp
        // throws on an inverted range — which crashed the whole app on the
        // first mouse move of an arrow drag.
        var headLength = Math.Min(
            Math.Max((length * 0.11) + (strokeWidth * 1.6), 9),
            Math.Min(44, length * 0.45));
        var headHalfWidth = headLength * 0.36;
        var baseHalfWidth = Math.Max(
            1.4,
            Math.Max(strokeWidth * 0.9, headHalfWidth * 0.22));
        var tailHalfWidth = Math.Max(0.6, strokeWidth * 0.22);
        var basePoint = end - (direction * headLength);

        return
        [
            start + (perpendicular * tailHalfWidth),
            basePoint + (perpendicular * baseHalfWidth),
            basePoint + (perpendicular * headHalfWidth),
            end,
            basePoint - (perpendicular * headHalfWidth),
            basePoint - (perpendicular * baseHalfWidth),
            start - (perpendicular * tailHalfWidth),
        ];
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
            // Rasterized at twice the placed size so display scaling and the
            // final full-resolution composition both stay crisp.
            Source = EmojiStickerRenderer.GetImage(
                annotation.Sticker,
                (int)Math.Ceiling(annotation.FontSize * 2)),
            Width = annotation.FontSize,
            Height = annotation.FontSize,
            Stretch = Stretch.Uniform,
            IsHitTestVisible = false,
        };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        SetLeft(image, annotation.Position.X - (annotation.FontSize / 2));
        SetTop(image, annotation.Position.Y - (annotation.FontSize / 2));
        Children.Add(image);
    }

    private void AddNumberVisual(NumberAnnotation annotation, string label)
    {
        var digitCount = Math.Max(1, label.Length);
        var labelFontSize = Math.Clamp(
            annotation.Size * (digitCount >= 4 ? 0.28 : digitCount >= 3 ? 0.36 : 0.48),
            8,
            annotation.Size * 0.48);
        var border = new Border
        {
            Width = annotation.Size,
            Height = annotation.Size,
            CornerRadius = new CornerRadius(annotation.Size / 2),
            Background = new SolidColorBrush(annotation.Color),
            BorderBrush = WpfBrushes.White,
            BorderThickness = new Thickness(Math.Max(1, annotation.Size * 0.06)),
            IsHitTestVisible = false,
            Child = new TextBlock
            {
                Text = label,
                Foreground = WpfBrushes.White,
                FontSize = labelFontSize,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = System.Windows.VerticalAlignment.Center,
                TextAlignment = TextAlignment.Center,
            },
        };
        SetLeft(border, annotation.Position.X);
        SetTop(border, annotation.Position.Y);
        Children.Add(border);
    }

    private string GetNumberLabel(EditorAnnotation annotation)
    {
        var number = 0;
        foreach (var item in _document.Annotations)
        {
            if (item is NumberAnnotation)
            {
                number++;
            }

            if (ReferenceEquals(item, annotation))
            {
                break;
            }
        }

        return Math.Max(1, number).ToString(CultureInfo.InvariantCulture);
    }

    private void AddTranslationOverlayVisual(
        TranslationOverlayAnnotation annotation)
    {
        foreach (var region in annotation.Regions)
        {
            var palette = GetTranslationPalette(region.Bounds);
            var contentWidth = Math.Max(8, region.Bounds.Width - 8);
            var contentHeight = Math.Max(8, region.Bounds.Height - 6);
            var layout = TranslationTextLayout.LayoutParagraph(region);
            var text = new TextBlock
            {
                Text = string.Join(
                    Environment.NewLine,
                    layout.Lines.Select(line => line.Text)),
                FontFamily = new System.Windows.Media.FontFamily(
                    "Microsoft YaHei UI"),
                FontSize = layout.FontSize,
                FontWeight = FontWeights.Normal,
                Foreground = new SolidColorBrush(palette.Foreground),
                Width = contentWidth,
                Height = contentHeight,
                TextWrapping = TextWrapping.NoWrap,
                TextTrimming = TextTrimming.None,
                LineHeight = layout.LineHeight,
                LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                ClipToBounds = true,
                IsHitTestVisible = false,
            };
            var border = new Border
            {
                Width = Math.Max(12, region.Bounds.Width),
                Height = Math.Max(12, region.Bounds.Height),
                Padding = new Thickness(4, 3, 4, 3),
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
                WpfColor.FromRgb(35, 42, 46),
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
                WpfColor.FromRgb(35, 42, 46),
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
                WpfColor.FromRgb(
                    (byte)(averageRed * 0.82),
                    (byte)(averageGreen * 0.82),
                    (byte)(averageBlue * 0.82)),
                WpfColor.FromRgb(248, 251, 251));
        }

        return (
            WpfColor.FromRgb(
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
