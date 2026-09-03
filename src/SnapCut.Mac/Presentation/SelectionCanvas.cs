using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.TextFormatting;
using SnapCut.Core;
using SnapCut.Mac.Editor;

namespace SnapCut.Mac.Presentation;

internal sealed class SelectionCanvas : Control
{
    private const double HandleRadius = 6;
    private const double HandleHitRadius = 11;
    private readonly Bitmap _background;
    private readonly PixelImage _backgroundPixels;
    private readonly Pen _selectionPen = new(MacTheme.AccentBrush, 2);
    private readonly Pen _handlePen = new(MacTheme.AccentBrush, 2);
    private readonly IBrush _mask = new SolidColorBrush(Color.FromArgb(150, 0, 0, 0));
    private readonly List<MacAnnotation> _annotations = [];
    private readonly Stack<MacAnnotation> _redoAnnotations = [];
    private Point _anchor;
    private Point _current;
    private Point _dragOrigin;
    private Rect _dragStart;
    private InteractionMode _interaction;
    private ResizeHandle _resizeHandle;
    private bool _hasSelection;
    private MacAnnotation? _draftAnnotation;
    private List<Point>? _draftPoints;
    private Point _annotationStart;
    private Rect _suggestedSelection;
    private bool _fixedSelection;
    private int _selectedAnnotationIndex = -1;
    private Color _annotationColor = Color.Parse("#FF3B30");
    private double _annotationWidth = 3;
    private string _textValue = string.Empty;
    private string _emojiValue = "😊";
    private MacArrowStyle _arrowStyle = MacArrowStyle.Filled;

    public SelectionCanvas(Bitmap background, PixelImage backgroundPixels)
    {
        _background = background;
        _backgroundPixels = backgroundPixels;
        Cursor = new Cursor(StandardCursorType.Cross);
        Focusable = true;
    }

    public event Action<Rect>? SelectionReady;

    public event Action<Rect>? SelectionChanged;

    public event Action? CancelRequested;

    public event Action? AnnotationStateChanged;

    public event Action<MacAnnotation?>? AnnotationSelectionChanged;

    public event Action<Color, Point>? ColorSampleChanged;

    public event Action<Point>? WindowSnapRequested;

    public Rect Selection => Normalize(_anchor, _current);

    public bool HasSelection => _hasSelection;

    public MacEditorTool? ActiveTool { get; private set; }

    public Color AnnotationColor
    {
        get => _annotationColor;
        set
        {
            _annotationColor = value;
            UpdateSelectedAnnotation(annotation => annotation switch
            {
                MacShapeAnnotation shape => shape with { Color = value },
                MacStrokeAnnotation stroke when stroke.Tool == MacEditorTool.Brush =>
                    stroke with { Color = value },
                MacTextAnnotation text => text with { Color = value },
                MacNumberAnnotation number => number with { Color = value },
                _ => annotation,
            });
        }
    }

    public double AnnotationWidth
    {
        get => _annotationWidth;
        set
        {
            _annotationWidth = Math.Clamp(value, 1, 10);
            UpdateSelectedAnnotation(annotation => annotation switch
            {
                MacShapeAnnotation shape => shape with { StrokeWidth = _annotationWidth },
                MacStrokeAnnotation stroke when stroke.Tool == MacEditorTool.Brush =>
                    stroke with { StrokeWidth = _annotationWidth },
                MacStrokeAnnotation stroke when stroke.Tool == MacEditorTool.Mosaic =>
                    stroke with { StrokeWidth = Math.Max(12, _annotationWidth * 4) },
                MacTextAnnotation text when text.Tool == MacEditorTool.Text =>
                    text with { FontSize = Math.Max(14, _annotationWidth * 6) },
                MacTextAnnotation text =>
                    text with { FontSize = Math.Max(18, _annotationWidth * 7) },
                MacNumberAnnotation number =>
                    number with { Size = Math.Max(24, _annotationWidth * 9) },
                _ => annotation,
            });
        }
    }

    public string TextValue
    {
        get => _textValue;
        set
        {
            _textValue = value ?? string.Empty;
            UpdateSelectedAnnotation(annotation =>
                annotation is MacTextAnnotation { Tool: MacEditorTool.Text } text &&
                !string.IsNullOrWhiteSpace(_textValue)
                    ? text with { Text = _textValue }
                    : annotation);
        }
    }

    public string EmojiValue
    {
        get => _emojiValue;
        set
        {
            _emojiValue = string.IsNullOrWhiteSpace(value) ? "😊" : value;
            UpdateSelectedAnnotation(annotation =>
                annotation is MacTextAnnotation { Tool: MacEditorTool.Emoji } text
                    ? text with { Text = _emojiValue }
                    : annotation);
        }
    }

    public MacArrowStyle ArrowStyle
    {
        get => _arrowStyle;
        set
        {
            _arrowStyle = value;
            UpdateSelectedAnnotation(annotation =>
                annotation is MacShapeAnnotation { Tool: MacEditorTool.Arrow } arrow
                    ? arrow with { ArrowStyle = value }
                    : annotation);
        }
    }

    public IReadOnlyList<MacAnnotation> Annotations => _annotations;

    public MacAnnotation? SelectedAnnotation =>
        _selectedAnnotationIndex >= 0 && _selectedAnnotationIndex < _annotations.Count
            ? _annotations[_selectedAnnotationIndex]
            : null;

    public bool CanUndo => _annotations.Count > 0;

    public bool CanRedo => _redoAnnotations.Count > 0;

    public void SetFixedSelection()
    {
        _fixedSelection = true;
        _anchor = new Point(0, 0);
        _current = new Point(Bounds.Width, Bounds.Height);
        _hasSelection = true;
        InvalidateVisual();
    }

    public void SetSuggestedSelection(Rect selection)
    {
        if (_hasSelection || _interaction != InteractionMode.None)
        {
            return;
        }

        _suggestedSelection = selection.Intersect(new Rect(Bounds.Size));
        InvalidateVisual();
    }

    public void SelectTool(MacEditorTool? tool)
    {
        if (tool is not null && _selectedAnnotationIndex >= 0)
        {
            _selectedAnnotationIndex = -1;
            AnnotationSelectionChanged?.Invoke(null);
        }

        ActiveTool = tool;
        Cursor = new Cursor(tool is null
            ? StandardCursorType.SizeAll
            : StandardCursorType.Cross);
        InvalidateVisual();
    }

    public void Undo()
    {
        if (_annotations.Count == 0)
        {
            return;
        }

        var annotation = _annotations[^1];
        _annotations.RemoveAt(_annotations.Count - 1);
        _redoAnnotations.Push(annotation);
        AnnotationStateChanged?.Invoke();
        InvalidateVisual();
    }

    public void Redo()
    {
        if (!_redoAnnotations.TryPop(out var annotation))
        {
            return;
        }

        _annotations.Add(annotation);
        AnnotationStateChanged?.Invoke();
        InvalidateVisual();
    }

    public void DeleteSelectedAnnotation()
    {
        var index = _selectedAnnotationIndex >= 0
            ? _selectedAnnotationIndex
            : _annotations.Count - 1;
        if (index < 0 || index >= _annotations.Count)
        {
            return;
        }

        _annotations.RemoveAt(index);
        _selectedAnnotationIndex = -1;
        _redoAnnotations.Clear();
        AnnotationSelectionChanged?.Invoke(null);
        AnnotationStateChanged?.Invoke();
        InvalidateVisual();
    }

    public void ClearSelection()
    {
        _anchor = default;
        _current = default;
        _hasSelection = false;
        _annotations.Clear();
        _redoAnnotations.Clear();
        _draftAnnotation = null;
        _draftPoints = null;
        _suggestedSelection = default;
        ActiveTool = null;
        _interaction = InteractionMode.None;
        _selectedAnnotationIndex = -1;
        Cursor = new Cursor(StandardCursorType.Cross);
        AnnotationSelectionChanged?.Invoke(null);
        InvalidateVisual();
        SelectionChanged?.Invoke(default);
        AnnotationStateChanged?.Invoke();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var bounds = new Rect(Bounds.Size);
        context.DrawImage(_background, bounds);
        var selection = Selection.Intersect(bounds);
        if (_fixedSelection)
        {
            selection = bounds;
            _anchor = bounds.TopLeft;
            _current = bounds.BottomRight;
        }
        if (!_hasSelection && _interaction == InteractionMode.None)
        {
            if (_suggestedSelection.Width > 0 && _suggestedSelection.Height > 0)
            {
                DrawMask(context, bounds, _suggestedSelection);
                context.DrawRectangle(null, _selectionPen, _suggestedSelection);
            }
            else
            {
                context.DrawRectangle(_mask, null, bounds);
            }
            return;
        }

        if (!_fixedSelection)
        {
            DrawMask(context, bounds, selection);
        }
        if (selection.Width <= 0 || selection.Height <= 0)
        {
            return;
        }

        using (context.PushClip(selection))
        {
            foreach (var annotation in _annotations)
            {
                DrawAnnotation(context, annotation);
            }

            if (_draftAnnotation is not null)
            {
                DrawAnnotation(context, _draftAnnotation);
            }

            if (SelectedAnnotation is { } selected)
            {
                DrawAnnotationSelection(context, selected);
            }
        }

        if (!_fixedSelection)
        {
            context.DrawRectangle(null, _selectionPen, selection);
        }
        if (!_fixedSelection && _hasSelection && _interaction == InteractionMode.None)
        {
            foreach (var point in HandlePoints(selection))
            {
                context.DrawEllipse(Brushes.White, _handlePen, point, HandleRadius, HandleRadius);
            }
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var properties = e.GetCurrentPoint(this).Properties;
        if (properties.IsRightButtonPressed)
        {
            CancelRequested?.Invoke();
            e.Handled = true;
            return;
        }

        if (!properties.IsLeftButtonPressed)
        {
            return;
        }

        var position = Clamp(e.GetPosition(this));
        if (_fixedSelection && ActiveTool is null)
        {
            return;
        }
        if (!_hasSelection && _suggestedSelection.Contains(position))
        {
            _anchor = _suggestedSelection.TopLeft;
            _current = _suggestedSelection.BottomRight;
            _dragOrigin = position;
            _interaction = InteractionMode.SnapPending;
            e.Pointer.Capture(this);
            Focus();
            e.Handled = true;
            return;
        }

        if (_hasSelection && ActiveTool is not null && Selection.Contains(position))
        {
            BeginAnnotation(position);
            _interaction = InteractionMode.Draw;
            e.Pointer.Capture(this);
            Focus();
            e.Handled = true;
            return;
        }

        if (_hasSelection && ActiveTool is null && Selection.Contains(position))
        {
            _selectedAnnotationIndex = HitTestAnnotation(position);
            if (_selectedAnnotationIndex >= 0)
            {
                AnnotationSelectionChanged?.Invoke(SelectedAnnotation);
                _dragOrigin = position;
                _interaction = InteractionMode.AnnotationMove;
                e.Pointer.Capture(this);
                Focus();
                e.Handled = true;
                return;
            }


            AnnotationSelectionChanged?.Invoke(null);
        }

        _dragOrigin = position;
        _dragStart = Selection;
        _resizeHandle = _hasSelection
            ? HitTestHandle(position, _dragStart)
            : ResizeHandle.None;
        if (_resizeHandle != ResizeHandle.None)
        {
            _interaction = InteractionMode.Resize;
        }
        else if (_hasSelection && _dragStart.Contains(position))
        {
            _interaction = InteractionMode.Move;
        }
        else
        {
            _anchor = position;
            _current = position;
            _hasSelection = false;
            _interaction = InteractionMode.Create;
        }

        e.Pointer.Capture(this);
        Focus();
        InvalidateVisual();
        SelectionChanged?.Invoke(Selection);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var position = Clamp(e.GetPosition(this));
        if (_interaction == InteractionMode.None)
        {
            UpdateCursor(position);
            PublishColorSample(position);
            WindowSnapRequested?.Invoke(position);
            return;
        }

        switch (_interaction)
        {
            case InteractionMode.Create:
                _current = position;
                break;
            case InteractionMode.Move:
                MoveSelection(position);
                break;
            case InteractionMode.AnnotationMove:
                MoveAnnotation(position);
                break;
            case InteractionMode.Resize:
                ResizeSelection(position);
                break;
            case InteractionMode.Draw:
                UpdateAnnotation(position);
                break;
            case InteractionMode.SnapPending:
                if (Math.Abs(position.X - _dragOrigin.X) >= 3 ||
                    Math.Abs(position.Y - _dragOrigin.Y) >= 3)
                {
                    _anchor = _dragOrigin;
                    _current = position;
                    _suggestedSelection = default;
                    _interaction = InteractionMode.Create;
                }
                break;
        }

        InvalidateVisual();
        SelectionChanged?.Invoke(Selection);
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_interaction == InteractionMode.None)
        {
            return;
        }

        var position = Clamp(e.GetPosition(this));
        if (_interaction == InteractionMode.Create)
        {
            _current = position;
        }
        else if (_interaction == InteractionMode.Move)
        {
            MoveSelection(position);
        }
        else if (_interaction == InteractionMode.AnnotationMove)
        {
            MoveAnnotation(position);
            AnnotationStateChanged?.Invoke();
        }
        else if (_interaction != InteractionMode.SnapPending)
        {
            if (_interaction == InteractionMode.Resize)
            {
                ResizeSelection(position);
            }
            else
            {
                UpdateAnnotation(position);
            }
        }

        var completedAnnotation = _interaction == InteractionMode.Draw;
        _interaction = InteractionMode.None;
        e.Pointer.Capture(null);
        if (completedAnnotation)
        {
            CommitAnnotation();
            UpdateCursor(position);
            e.Handled = true;
            return;
        }

        var selection = Selection;
        _hasSelection = selection.Width >= 6 && selection.Height >= 6;
        _suggestedSelection = default;
        InvalidateVisual();
        SelectionChanged?.Invoke(selection);
        if (_hasSelection)
        {
            SelectionReady?.Invoke(selection);
        }

        UpdateCursor(position);
        e.Handled = true;
    }

    private void MoveSelection(Point position)
    {
        var delta = position - _dragOrigin;
        var left = Math.Clamp(
            _dragStart.Left + delta.X,
            0,
            Math.Max(0, Bounds.Width - _dragStart.Width));
        var top = Math.Clamp(
            _dragStart.Top + delta.Y,
            0,
            Math.Max(0, Bounds.Height - _dragStart.Height));
        _anchor = new Point(left, top);
        _current = new Point(left + _dragStart.Width, top + _dragStart.Height);
    }

    private void MoveAnnotation(Point position)
    {
        if (_selectedAnnotationIndex < 0 ||
            _selectedAnnotationIndex >= _annotations.Count)
        {
            return;
        }

        var delta = position - _dragOrigin;
        _annotations[_selectedAnnotationIndex] = Translate(
            _annotations[_selectedAnnotationIndex],
            delta);
        _dragOrigin = position;
    }

    private void UpdateSelectedAnnotation(Func<MacAnnotation, MacAnnotation> update)
    {
        if (_selectedAnnotationIndex < 0 ||
            _selectedAnnotationIndex >= _annotations.Count)
        {
            return;
        }

        var current = _annotations[_selectedAnnotationIndex];
        var updated = update(current);
        if (Equals(current, updated))
        {
            return;
        }

        _annotations[_selectedAnnotationIndex] = updated;
        _redoAnnotations.Clear();
        AnnotationStateChanged?.Invoke();
        AnnotationSelectionChanged?.Invoke(updated);
        InvalidateVisual();
    }

    private static MacAnnotation Translate(MacAnnotation annotation, Vector delta) =>
        annotation switch
        {
            MacShapeAnnotation shape => shape with
            {
                Start = shape.Start + delta,
                End = shape.End + delta,
            },
            MacStrokeAnnotation stroke => stroke with
            {
                Points = stroke.Points.Select(point => point + delta).ToArray(),
            },
            MacTextAnnotation text => text with { Position = text.Position + delta },
            MacNumberAnnotation number => number with
            {
                Position = number.Position + delta,
            },
            _ => annotation,
        };

    private int HitTestAnnotation(Point point)
    {
        for (var index = _annotations.Count - 1; index >= 0; index--)
        {
            var annotation = _annotations[index];
            var bounds = annotation switch
            {
                MacShapeAnnotation shape => Normalize(shape.Start, shape.End).Inflate(10),
                MacStrokeAnnotation stroke => BoundsOf(stroke.Points).Inflate(10),
                MacTextAnnotation text => new Rect(
                    text.Position,
                    new Size(140, text.FontSize + 12)),
                MacNumberAnnotation number => new Rect(
                    number.Position.X - number.Size,
                    number.Position.Y - number.Size,
                    number.Size * 2,
                    number.Size * 2),
                _ => default,
            };
            if (bounds.Contains(point))
            {
                return index;
            }
        }

        return -1;
    }

    private static Rect BoundsOf(IReadOnlyList<Point> points)
    {
        if (points.Count == 0)
        {
            return default;
        }

        var left = points.Min(point => point.X);
        var top = points.Min(point => point.Y);
        var right = points.Max(point => point.X);
        var bottom = points.Max(point => point.Y);
        return new Rect(left, top, right - left, bottom - top);
    }

    private static Rect AnnotationBounds(MacAnnotation annotation) => annotation switch
    {
        MacShapeAnnotation shape => Normalize(shape.Start, shape.End),
        MacStrokeAnnotation stroke => BoundsOf(stroke.Points),
        MacTextAnnotation text => new Rect(
            text.Position,
            new Size(
                Math.Max(24, text.Text.Length * text.FontSize),
                text.FontSize + 8)),
        MacNumberAnnotation number => new Rect(
            number.Position.X - (number.Size / 2),
            number.Position.Y - (number.Size / 2),
            number.Size,
            number.Size),
        _ => default,
    };

    private static void DrawAnnotationSelection(
        DrawingContext context,
        MacAnnotation annotation)
    {
        var bounds = AnnotationBounds(annotation).Inflate(4);
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        var pen = new Pen(MacTheme.AccentBrush, 1);
        context.DrawRectangle(null, pen, bounds);
        foreach (var point in new[]
                 {
                     bounds.TopLeft,
                     bounds.TopRight,
                     bounds.BottomRight,
                     bounds.BottomLeft,
                 })
        {
            context.DrawEllipse(Brushes.White, pen, point, 3.5, 3.5);
        }
    }

    private void ResizeSelection(Point position)
    {
        var left = _dragStart.Left;
        var top = _dragStart.Top;
        var right = _dragStart.Right;
        var bottom = _dragStart.Bottom;
        if (_resizeHandle is ResizeHandle.TopLeft or ResizeHandle.Left or ResizeHandle.BottomLeft)
        {
            left = position.X;
        }
        if (_resizeHandle is ResizeHandle.TopRight or ResizeHandle.Right or ResizeHandle.BottomRight)
        {
            right = position.X;
        }
        if (_resizeHandle is ResizeHandle.TopLeft or ResizeHandle.Top or ResizeHandle.TopRight)
        {
            top = position.Y;
        }
        if (_resizeHandle is ResizeHandle.BottomLeft or ResizeHandle.Bottom or ResizeHandle.BottomRight)
        {
            bottom = position.Y;
        }

        _anchor = Clamp(new Point(left, top));
        _current = Clamp(new Point(right, bottom));
    }

    private void UpdateCursor(Point position)
    {
        if (ActiveTool is not null && _hasSelection && Selection.Contains(position))
        {
            Cursor = new Cursor(StandardCursorType.Cross);
            return;
        }

        if (!_hasSelection)
        {
            Cursor = new Cursor(StandardCursorType.Cross);
            return;
        }

        var handle = HitTestHandle(position, Selection);
        Cursor = handle switch
        {
            ResizeHandle.TopLeft or ResizeHandle.BottomRight =>
                new Cursor(StandardCursorType.TopLeftCorner),
            ResizeHandle.TopRight or ResizeHandle.BottomLeft =>
                new Cursor(StandardCursorType.TopRightCorner),
            ResizeHandle.Top or ResizeHandle.Bottom =>
                new Cursor(StandardCursorType.SizeNorthSouth),
            ResizeHandle.Left or ResizeHandle.Right =>
                new Cursor(StandardCursorType.SizeWestEast),
            _ when Selection.Contains(position) =>
                new Cursor(StandardCursorType.SizeAll),
            _ => new Cursor(StandardCursorType.Cross),
        };
    }

    private void PublishColorSample(Point position)
    {
        if (_hasSelection || Bounds.Width <= 0 || Bounds.Height <= 0)
        {
            return;
        }

        var x = Math.Clamp(
            (int)Math.Floor(position.X * _backgroundPixels.Width / Bounds.Width),
            0,
            _backgroundPixels.Width - 1);
        var y = Math.Clamp(
            (int)Math.Floor(position.Y * _backgroundPixels.Height / Bounds.Height),
            0,
            _backgroundPixels.Height - 1);
        var offset = (y * _backgroundPixels.Stride) + (x * 4);
        var color = Color.FromArgb(
            _backgroundPixels.Pixels[offset + 3],
            _backgroundPixels.Pixels[offset + 2],
            _backgroundPixels.Pixels[offset + 1],
            _backgroundPixels.Pixels[offset]);
        ColorSampleChanged?.Invoke(color, position);
    }

    private static ResizeHandle HitTestHandle(Point point, Rect selection)
    {
        var handles = HandlePoints(selection);
        for (var index = 0; index < handles.Length; index++)
        {
            var delta = point - handles[index];
            if ((delta.X * delta.X) + (delta.Y * delta.Y) <=
                HandleHitRadius * HandleHitRadius)
            {
                return (ResizeHandle)(index + 1);
            }
        }

        return ResizeHandle.None;
    }

    private static Point[] HandlePoints(Rect selection)
    {
        var centerX = selection.Left + (selection.Width / 2);
        var centerY = selection.Top + (selection.Height / 2);
        return
        [
            selection.TopLeft,
            new Point(centerX, selection.Top),
            selection.TopRight,
            new Point(selection.Right, centerY),
            selection.BottomRight,
            new Point(centerX, selection.Bottom),
            selection.BottomLeft,
            new Point(selection.Left, centerY),
        ];
    }

    private Point Clamp(Point point)
    {
        return new Point(
            Math.Clamp(point.X, 0, Bounds.Width),
            Math.Clamp(point.Y, 0, Bounds.Height));
    }

    private static Rect Normalize(Point first, Point second)
    {
        var left = Math.Min(first.X, second.X);
        var top = Math.Min(first.Y, second.Y);
        return new Rect(
            left,
            top,
            Math.Abs(second.X - first.X),
            Math.Abs(second.Y - first.Y));
    }

    private void DrawMask(DrawingContext context, Rect bounds, Rect selection)
    {
        if (selection.Width <= 0 || selection.Height <= 0)
        {
            context.DrawRectangle(_mask, null, bounds);
            return;
        }

        context.DrawRectangle(_mask, null, new Rect(bounds.X, bounds.Y, bounds.Width, selection.Top));
        context.DrawRectangle(_mask, null, new Rect(bounds.X, selection.Bottom, bounds.Width, bounds.Bottom - selection.Bottom));
        context.DrawRectangle(_mask, null, new Rect(bounds.X, selection.Top, selection.Left, selection.Height));
        context.DrawRectangle(_mask, null, new Rect(selection.Right, selection.Top, bounds.Right - selection.Right, selection.Height));
    }

    private void BeginAnnotation(Point position)
    {
        _annotationStart = position;
        _draftPoints = ActiveTool is MacEditorTool.Brush or MacEditorTool.Mosaic
            ? new List<Point> { position }
            : null;
        _draftAnnotation = CreateDraft(position);
        InvalidateVisual();
    }

    private void UpdateAnnotation(Point position)
    {
        position = ClampToSelection(position);
        if (_draftPoints is not null)
        {
            var previous = _draftPoints[^1];
            if (Math.Abs(previous.X - position.X) >= 1 ||
                Math.Abs(previous.Y - position.Y) >= 1)
            {
                _draftPoints.Add(position);
            }
        }

        _draftAnnotation = CreateDraft(position);
        InvalidateVisual();
    }

    private MacAnnotation? CreateDraft(Point position)
    {
        return ActiveTool switch
        {
            MacEditorTool.Rectangle or MacEditorTool.Ellipse or MacEditorTool.Arrow =>
                new MacShapeAnnotation(
                    ActiveTool.Value,
                    _annotationStart,
                    position,
                    AnnotationColor,
                    AnnotationWidth,
                    ArrowStyle),
            MacEditorTool.Brush or MacEditorTool.Mosaic =>
                new MacStrokeAnnotation(
                    ActiveTool.Value,
                    _draftPoints?.ToArray() ?? [_annotationStart, position],
                    AnnotationColor,
                    ActiveTool == MacEditorTool.Mosaic
                        ? Math.Max(12, AnnotationWidth * 4)
                        : AnnotationWidth),
            MacEditorTool.Text => new MacTextAnnotation(
                MacEditorTool.Text,
                _annotationStart,
                 string.IsNullOrWhiteSpace(TextValue) ? string.Empty : TextValue,
                AnnotationColor,
                Math.Max(14, AnnotationWidth * 6)),
            MacEditorTool.Emoji => new MacTextAnnotation(
                MacEditorTool.Emoji,
                _annotationStart,
                 string.IsNullOrWhiteSpace(EmojiValue) ? "😊" : EmojiValue,
                AnnotationColor,
                Math.Max(18, AnnotationWidth * 7)),
            MacEditorTool.Number => new MacNumberAnnotation(
                _annotationStart,
                NextNumber(),
                AnnotationColor,
                Math.Max(24, AnnotationWidth * 9)),
            _ => null,
        };
    }

    private void CommitAnnotation()
    {
        if (_draftAnnotation is MacTextAnnotation { Text: "" })
        {
            _draftAnnotation = null;
        }

        if (_draftAnnotation is not null)
        {
            _annotations.Add(_draftAnnotation);
            _redoAnnotations.Clear();
        }

        _draftAnnotation = null;
        _draftPoints = null;
        AnnotationStateChanged?.Invoke();
        InvalidateVisual();
    }

    private int NextNumber() =>
        _annotations.OfType<MacNumberAnnotation>()
            .Select(annotation => annotation.Number)
            .DefaultIfEmpty(0)
            .Max() + 1;

    private Point ClampToSelection(Point point)
    {
        var selection = Selection;
        return new Point(
            Math.Clamp(point.X, selection.Left, selection.Right),
            Math.Clamp(point.Y, selection.Top, selection.Bottom));
    }

    private void DrawAnnotation(DrawingContext context, MacAnnotation annotation)
    {
        switch (annotation)
        {
            case MacShapeAnnotation shape:
                DrawShape(context, shape);
                break;
            case MacStrokeAnnotation stroke:
                DrawStroke(context, stroke);
                break;
            case MacTextAnnotation text:
                DrawText(context, text);
                break;
            case MacNumberAnnotation number:
                DrawNumber(context, number);
                break;
        }
    }

    private static void DrawShape(DrawingContext context, MacShapeAnnotation shape)
    {
        var pen = new Pen(new SolidColorBrush(shape.Color), shape.StrokeWidth);
        if (shape.Tool == MacEditorTool.Arrow)
        {
            var points = CreateArrowPoints(
                shape.Start,
                shape.End,
                shape.StrokeWidth);
            if (points.Length < 3)
            {
                context.DrawLine(pen, shape.Start, shape.End);
                return;
            }

            var geometry = new StreamGeometry();
            using (var geometryContext = geometry.Open())
            {
                geometryContext.BeginFigure(points[0], isFilled: true);
                foreach (var point in points.Skip(1))
                {
                    geometryContext.LineTo(point);
                }
                geometryContext.EndFigure(isClosed: true);
            }

            if (shape.ArrowStyle == MacArrowStyle.Hollow)
            {
                context.DrawGeometry(null, pen, geometry);
            }
            else
            {
                context.DrawGeometry(new SolidColorBrush(shape.Color), null, geometry);
            }
            return;
        }

        var bounds = Normalize(shape.Start, shape.End);
        if (shape.Tool == MacEditorTool.Ellipse)
        {
            context.DrawEllipse(null, pen, bounds.Center, bounds.Width / 2, bounds.Height / 2);
        }
        else
        {
            context.DrawRectangle(null, pen, bounds);
        }
    }

    private static Point[] CreateArrowPoints(
        Point start,
        Point end,
        double strokeWidth)
    {
        var delta = end - start;
        var length = Math.Sqrt(
            (delta.X * delta.X) + (delta.Y * delta.Y));
        if (length < 1)
        {
            return [start, end];
        }

        var direction = new Vector(delta.X / length, delta.Y / length);
        var perpendicular = new Vector(-direction.Y, direction.X);
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

    private void DrawStroke(DrawingContext context, MacStrokeAnnotation stroke)
    {
        if (stroke.Points.Count < 2)
        {
            return;
        }

        if (stroke.Tool == MacEditorTool.Mosaic)
        {
            var block = Math.Max(6, stroke.StrokeWidth / 2);
            foreach (var point in stroke.Points)
            {
                var color = SampleBackgroundColor(point, block);
                context.DrawRectangle(
                    new SolidColorBrush(color),
                    null,
                    new Rect(
                        point.X - block,
                        point.Y - block,
                        block * 2,
                        block * 2));
            }

            return;
        }

        var pen = new Pen(new SolidColorBrush(stroke.Color), stroke.StrokeWidth);
        for (var index = 1; index < stroke.Points.Count; index++)
        {
            context.DrawLine(pen, stroke.Points[index - 1], stroke.Points[index]);
        }
    }

    private Color SampleBackgroundColor(Point point, double block)
    {
        if (Bounds.Width <= 0 || Bounds.Height <= 0)
        {
            return Color.Parse("#343740");
        }

        var left = Math.Clamp(
            (int)Math.Floor((point.X - block) * _backgroundPixels.Width / Bounds.Width),
            0,
            _backgroundPixels.Width - 1);
        var top = Math.Clamp(
            (int)Math.Floor((point.Y - block) * _backgroundPixels.Height / Bounds.Height),
            0,
            _backgroundPixels.Height - 1);
        var right = Math.Clamp(
            (int)Math.Ceiling((point.X + block) * _backgroundPixels.Width / Bounds.Width),
            left + 1,
            _backgroundPixels.Width);
        var bottom = Math.Clamp(
            (int)Math.Ceiling((point.Y + block) * _backgroundPixels.Height / Bounds.Height),
            top + 1,
            _backgroundPixels.Height);
        long red = 0, green = 0, blue = 0;
        var count = 0;
        for (var y = top; y < bottom; y++)
        {
            for (var x = left; x < right; x++)
            {
                var offset = (y * _backgroundPixels.Stride) + (x * 4);
                blue += _backgroundPixels.Pixels[offset];
                green += _backgroundPixels.Pixels[offset + 1];
                red += _backgroundPixels.Pixels[offset + 2];
                count++;
            }
        }

        return count == 0
            ? Color.Parse("#343740")
            : Color.FromRgb(
                (byte)(red / count),
                (byte)(green / count),
                (byte)(blue / count));
    }

    private static void DrawText(DrawingContext context, MacTextAnnotation text)
    {
        var layout = new TextLayout(
            text.Text,
            Typeface.Default,
            text.FontSize,
            new SolidColorBrush(text.Color));
        layout.Draw(context, text.Position);
    }

    private static void DrawNumber(DrawingContext context, MacNumberAnnotation number)
    {
        var radius = number.Size / 2;
        var brush = new SolidColorBrush(number.Color);
        context.DrawEllipse(brush, null, number.Position, radius, radius);
        var layout = new TextLayout(
            number.Number.ToString(),
            new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.Bold),
            number.Size * 0.55,
            Brushes.White);
        layout.Draw(
            context,
            new Point(
                number.Position.X - (layout.Width / 2),
                number.Position.Y - (layout.Height / 2)));
    }

    private enum InteractionMode
    {
        None,
        Create,
        Move,
        AnnotationMove,
        Resize,
        Draw,
        SnapPending,
    }

    private enum ResizeHandle
    {
        None,
        TopLeft,
        Top,
        TopRight,
        Right,
        BottomRight,
        Bottom,
        BottomLeft,
        Left,
    }
}
