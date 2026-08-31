using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Screenshot.App.Capture;
using Screenshot.App.Core;
using Screenshot.App.Editor;
using Screenshot.App.Infrastructure;
using Screenshot.App.Text;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfButtonBase = System.Windows.Controls.Primitives.ButtonBase;
using WpfColor = System.Windows.Media.Color;
using WpfMouseEventArgs = System.Windows.Input.MouseEventArgs;
using WpfPoint = System.Windows.Point;
using WpfFontFamily = System.Windows.Media.FontFamily;
using WpfTextBox = System.Windows.Controls.TextBox;
using WpfCursors = System.Windows.Input.Cursors;

namespace Screenshot.App.Pin;

public partial class PinnedImageGroupWindow : Window
{
    private static readonly DrawingBrush CompositeCheckerboardBrush =
        CreateCompositeCheckerboardBrush();
    private readonly Func<AppSettings>? _settingsProvider;
    private bool _isClosed;

    private sealed record CompositionResult(
        BitmapSource Image,
        IReadOnlyList<Int32Rect> MemberBounds);

    private IReadOnlyList<PinnedImageWindow> _members = [];
    private BitmapSource? _compositePreview;
    private IReadOnlyList<Int32Rect> _memberBounds = [];
    private CapturedImage? _inlineEditorImage;
    private Int32Rect _cropRect;
    private Rect _renderedImageBounds;
    private WpfPoint? _cropDragStart;
    private bool _isEditorMode;
    private bool _isCropMode;
    private bool _hasCompositeEdits;
    private bool _applyingCompositeToMembers;
    private PinnedImageEditorToolbarWindow? _editorToolbar;
    private OcrRecognitionResult? _recognition;
    private bool _isPanningInlineEditor;
    private WpfPoint _inlineEditorPanStartPoint;
    private double _inlineEditorPanStartHorizontalOffset;
    private double _inlineEditorPanStartVerticalOffset;
    private PinnedWindowMinimization.Bounds? _restoreBounds;
    private bool _isMinimized;
    private double _restoreMinWidth;
    private double _restoreMinHeight;
    private bool _isDraggingThumbnail;
    private bool _thumbnailDragMoved;
    private WpfPoint _thumbnailDragStart;
    private WpfPoint _thumbnailDragStartScreen;
    private double _thumbnailStartTop;
    private Thickness _restoreShellBorderThickness;
    private ContextMenu? _restoreContextMenu;
    private readonly NativeWindowDragTracker _windowDragTracker;

    public PinnedImageGroupWindow(
        IReadOnlyList<PinnedImageWindow> members,
        Func<AppSettings>? settingsProvider = null)
    {
        _settingsProvider = settingsProvider;
        InitializeComponent();
        _windowDragTracker = new NativeWindowDragTracker(this, EndWindowDrag);
        SetMembers(members);
    }

    public event EventHandler? UngroupRequested;

    public event EventHandler? CloseGroupRequested;

    public event EventHandler? SettingsRequested;

    public event EventHandler? HideAllRequested;

    public event EventHandler? MinimizeRequested;

    public event EventHandler? MinimizedStateChanged;

    internal bool IsMinimized => _isMinimized;

    internal PinnedWindowMinimization.Bounds GetPersistenceBounds() =>
        _restoreBounds ?? new(Left, Top, Width, Height);

    internal void MinimizeTo(int stackIndex)
    {
        if (_isMinimized) return;
        _restoreBounds = new(Left, Top, Width, Height);
        _restoreMinWidth = MinWidth; _restoreMinHeight = MinHeight;
        _isMinimized = true; MinWidth = 0; MinHeight = 0;
        _restoreContextMenu = GroupShell.ContextMenu;
        GroupShell.ContextMenu = null;
        MinimizedStateChanged?.Invoke(this, EventArgs.Empty);
        _restoreShellBorderThickness = GroupShell.BorderThickness;
        Background = WpfBrushes.Transparent;
        GroupShell.BorderThickness = new Thickness(0);
        GroupShell.Background = WpfBrushes.Transparent;
        GroupHeaderRow.Height = new GridLength(0);
        GroupHeader.Visibility = Visibility.Collapsed;
        GroupCanvas.Background = WpfBrushes.Transparent;
        CompositeImage.Stretch = Stretch.UniformToFill;
        GroupCanvas.Cursor = WpfCursors.Arrow;
        GroupTextOverlay.Visibility = Visibility.Collapsed;
        PinnedWindowMinimization.Animate(this, PinnedWindowMinimization.GetThumbnailBounds(stackIndex), () => { });
    }

    internal void RestoreFromMinimized()
    {
        if (!_isMinimized || _restoreBounds is not { } bounds) return;
        PinnedWindowMinimization.Animate(this, bounds, () =>
        {
            BeginAnimation(LeftProperty, null); BeginAnimation(TopProperty, null);
            BeginAnimation(WidthProperty, null); BeginAnimation(HeightProperty, null);
            Left = bounds.Left; Top = bounds.Top;
            Width = bounds.Width; Height = bounds.Height;
            MinWidth = _restoreMinWidth; MinHeight = _restoreMinHeight;
            SetResourceReference(
                BackgroundProperty,
                "AppPanelBackgroundBrush");
            GroupShell.BorderThickness = _restoreShellBorderThickness;
            GroupShell.SetResourceReference(
                BackgroundProperty,
                "AppPanelBackgroundBrush");
            GroupShell.ContextMenu = _restoreContextMenu;
            GroupHeaderRow.Height = new GridLength(36);
            GroupHeader.Visibility = Visibility.Visible;
            GroupCanvas.Background =
                (System.Windows.Media.Brush)FindResource("PinnedCheckerboardBrush");
            CompositeImage.Stretch = Stretch.Uniform;
            GroupCanvas.Cursor = WpfCursors.Arrow;
            GroupTextOverlay.Visibility = Visibility.Visible;
            _isMinimized = false; _restoreBounds = null;
            MinimizedStateChanged?.Invoke(this, EventArgs.Empty);
        });
    }

    public event EventHandler? RestoreRequested;

    internal IReadOnlyList<PinnedImageWindow> Members => _members;

    internal BitmapSource CompositePreview =>
        _compositePreview ?? throw new InvalidOperationException(
            "The group does not contain a composite image.");

    internal bool IsInlineEditorVisible => _isEditorMode;

    internal bool IsInlineCropVisible => _isCropMode;

    internal bool ToolbarHasCustomPosition =>
        _editorToolbar?.HasCustomPosition == true;

    internal PinnedImageEditorToolbarWindow? EditorToolbar => _editorToolbar;

    internal void SetMembers(IReadOnlyList<PinnedImageWindow> members)
    {
        ArgumentNullException.ThrowIfNull(members);
        var membershipChanged = !_members.SequenceEqual(members);
        foreach (var member in _members)
        {
            member.ImageChanged -= OnMemberImageChanged;
        }

        _members = members.ToArray();
        foreach (var member in _members)
        {
            member.ImageChanged += OnMemberImageChanged;
        }

        if (membershipChanged || _compositePreview is null)
        {
            RebuildCompositeFromMembers();
        }
        HeaderStatusText.Text = _members.Count == 0
            ? "钉图编组"
            : $"钉图编组 · {_members.Count} 张 · 组合编辑";
    }

    protected override void OnClosed(EventArgs e)
    {
        _isClosed = true;
        _windowDragTracker.Stop();
        EndInlineEditorPanning();
        foreach (var member in _members)
        {
            member.ImageChanged -= OnMemberImageChanged;
        }
        CloseEditorToolbar();
        ExitInlineEditor(discardChanges: true);
        base.OnClosed(e);
    }

    private void RebuildCompositeFromMembers()
    {
        ExitInlineEditor(discardChanges: true);
        ExitCropMode();
        if (_members.Count == 0)
        {
            _compositePreview = null;
            _memberBounds = [];
            CompositeImage.Source = null;
            return;
        }

        var result = ComposeImagesWithLayout(
            _members.Select(member => member.GroupingPreview).ToArray());
        _compositePreview = result.Image;
        _memberBounds = result.MemberBounds;
        _hasCompositeEdits = false;
        CompositeImage.Source = result.Image;
        _recognition = null;
        GroupTextOverlay.Children.Clear();
    }

    private void OnMemberImageChanged(object? sender, EventArgs e)
    {
        if (!_applyingCompositeToMembers)
        {
            RebuildCompositeFromMembers();
        }
    }

    internal void SetCompositePreview(BitmapSource image)
    {
        ArgumentNullException.ThrowIfNull(image);
        var previousWidth = _compositePreview?.PixelWidth ?? image.PixelWidth;
        var previousHeight = _compositePreview?.PixelHeight ?? image.PixelHeight;
        _compositePreview = FreezeBitmap(image);
        if (previousWidth != image.PixelWidth || previousHeight != image.PixelHeight)
        {
            var scaleX = image.PixelWidth / (double)Math.Max(1, previousWidth);
            var scaleY = image.PixelHeight / (double)Math.Max(1, previousHeight);
            _memberBounds = _memberBounds
                .Select(bounds => ScaleBounds(bounds, scaleX, scaleY))
                .ToArray();
        }
        _hasCompositeEdits = true;
        CompositeImage.Source = _compositePreview;
        _recognition = null;
        GroupTextOverlay.Children.Clear();
    }

    internal void ApplyCompositeCrop(Int32Rect cropRect)
    {
        var source = CompositePreview;
        var bounded = IntersectBounds(
            cropRect,
            new Int32Rect(0, 0, source.PixelWidth, source.PixelHeight));
        if (bounded.IsEmpty)
        {
            return;
        }

        var cropped = new CroppedBitmap(source, bounded);
        cropped.Freeze();
        _memberBounds = _memberBounds.Select(bounds =>
        {
            var intersection = IntersectBounds(bounds, bounded);
            return intersection.IsEmpty
                ? Int32Rect.Empty
                : new Int32Rect(
                    intersection.X - bounded.X,
                    intersection.Y - bounded.Y,
                    intersection.Width,
                    intersection.Height);
        }).ToArray();
        _compositePreview = cropped;
        _hasCompositeEdits = true;
        CompositeImage.Source = cropped;
        _recognition = null;
        GroupTextOverlay.Children.Clear();
    }

    internal void ApplyCompositeToMembers()
    {
        CommitInlineEditor();
        if (!_hasCompositeEdits || _compositePreview is null)
        {
            return;
        }

        _applyingCompositeToMembers = true;
        try
        {
            var imageBounds = new Int32Rect(
                0,
                0,
                _compositePreview.PixelWidth,
                _compositePreview.PixelHeight);
            for (var index = 0;
                 index < Math.Min(_members.Count, _memberBounds.Count);
                 index++)
            {
                var bounds = IntersectBounds(_memberBounds[index], imageBounds);
                if (bounds.IsEmpty)
                {
                    continue;
                }

                var memberImage = new CroppedBitmap(_compositePreview, bounds);
                memberImage.Freeze();
                _members[index].ReplaceImage(
                    CapturedImage.FromBitmapSource(memberImage));
            }
            _hasCompositeEdits = false;
        }
        finally
        {
            _applyingCompositeToMembers = false;
        }
    }

    private static BitmapSource FreezeBitmap(BitmapSource image)
    {
        if (image.IsFrozen)
        {
            return image;
        }

        var copy = new WriteableBitmap(image);
        copy.Freeze();
        return copy;
    }

    private static Int32Rect ScaleBounds(
        Int32Rect bounds,
        double scaleX,
        double scaleY)
    {
        if (bounds.IsEmpty)
        {
            return bounds;
        }
        var left = (int)Math.Floor(bounds.X * scaleX);
        var top = (int)Math.Floor(bounds.Y * scaleY);
        var right = (int)Math.Ceiling((bounds.X + bounds.Width) * scaleX);
        var bottom = (int)Math.Ceiling((bounds.Y + bounds.Height) * scaleY);
        return new Int32Rect(
            left,
            top,
            Math.Max(1, right - left),
            Math.Max(1, bottom - top));
    }

    private static Int32Rect IntersectBounds(Int32Rect first, Int32Rect second)
    {
        if (first.IsEmpty || second.IsEmpty)
        {
            return Int32Rect.Empty;
        }
        var left = Math.Max(first.X, second.X);
        var top = Math.Max(first.Y, second.Y);
        var right = Math.Min(first.X + first.Width, second.X + second.Width);
        var bottom = Math.Min(first.Y + first.Height, second.Y + second.Height);
        return right <= left || bottom <= top
            ? Int32Rect.Empty
            : new Int32Rect(left, top, right - left, bottom - top);
    }

    private void OnHeaderMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed &&
            e.OriginalSource is not WpfButtonBase and not Slider)
        {
            BeginWindowDrag(e);
            e.Handled = true;
        }
    }

    private void BeginWindowDrag(MouseButtonEventArgs e)
    {
        _windowDragTracker.Start();
    }

    private void OnWindowPreviewMouseMove(object sender, WpfMouseEventArgs e)
    {
        if (_windowDragTracker.IsActive)
        {
            e.Handled = true;
        }
    }

    private void OnWindowPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _windowDragTracker.Stop();
        e.Handled = true;
    }

    private void OnWindowLostMouseCapture(object sender, WpfMouseEventArgs e) =>
        EndWindowDrag();

    private void EndWindowDrag()
    {
        _windowDragTracker.Stop();
    }

    private void OnGroupCanvasMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_isMinimized)
        {
            PinnedWindowMinimization.CommitCurrentAnimation(this);
            _isDraggingThumbnail = true;
            _thumbnailDragMoved = false;
            _thumbnailDragStart = e.GetPosition(this);
            _thumbnailDragStartScreen = GetCurrentScreenCursorPosition();
            _thumbnailStartTop = Top;
            GroupCanvas.Cursor = WpfCursors.SizeAll;
            _ = GroupCanvas.CaptureMouse();
            e.Handled = true;
        }
    }

    private void OnGroupCanvasMouseMove(object sender, WpfMouseEventArgs e)
    {
        if (!_isDraggingThumbnail || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var workArea = SystemParameters.WorkArea;
        var currentScreen = GetCurrentScreenCursorPosition();
        var top = _thumbnailStartTop +
            currentScreen.Y - _thumbnailDragStartScreen.Y;
        _thumbnailDragMoved |= Math.Abs(top - _thumbnailStartTop) >= 3;
        Left = workArea.Right - Width - 12;
        Top = Math.Clamp(top, workArea.Top + 12, workArea.Bottom - Height - 12);
        e.Handled = true;
    }

    private void OnGroupCanvasMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var restore = _isDraggingThumbnail && !_thumbnailDragMoved;
        EndThumbnailDrag();
        if (restore)
        {
            RestoreFromMinimized();
        }
    }

    private void OnGroupCanvasLostMouseCapture(object sender, WpfMouseEventArgs e) =>
        EndThumbnailDrag();

    private WpfPoint GetCurrentScreenCursorPosition()
    {
        var cursor = System.Windows.Forms.Cursor.Position;
        var source = PresentationSource.FromVisual(this);
        return source?.CompositionTarget is { } target
            ? target.TransformFromDevice.Transform(
                new WpfPoint(cursor.X, cursor.Y))
            : new WpfPoint(cursor.X, cursor.Y);
    }

    private void EndThumbnailDrag()
    {
        _isDraggingThumbnail = false;
        _thumbnailDragMoved = false;
        if (GroupCanvas.IsMouseCaptured)
        {
            GroupCanvas.ReleaseMouseCapture();
        }
        if (_isMinimized)
        {
            GroupCanvas.Cursor = WpfCursors.Arrow;
        }
    }

    private void OnEditClick(object sender, RoutedEventArgs e)
    {
        if (_isEditorMode)
        {
            return;
        }
        if (_isCropMode)
        {
            ExitCropMode();
        }

        var source = CompositePreview;
        _inlineEditorImage = CapturedImage.FromBitmapSource(source);
        var availableWidth = Math.Max(240, GroupCanvas.ActualWidth);
        var availableHeight = Math.Max(180, GroupCanvas.ActualHeight);
        var scale = Math.Min(
            1,
            Math.Min(
                availableWidth / source.PixelWidth,
                availableHeight / source.PixelHeight));
        var displayWidth = Math.Max(1, source.PixelWidth * scale);
        var displayHeight = Math.Max(1, source.PixelHeight * scale);
        InlineEditorCanvas.Initialize(
            _inlineEditorImage,
            displayWidth,
            displayHeight);
        InlineEditorFrame.Width = displayWidth;
        InlineEditorFrame.Height = displayHeight;
        CompositeImage.Visibility = Visibility.Collapsed;
        GroupTextOverlay.Children.Clear();
        InlineEditorViewport.Visibility = Visibility.Visible;
        _isEditorMode = true;
        ShowEditorToolbar(PinnedImageToolbarMode.Edit);
        HeaderStatusText.Text = $"钉图编组 · {_members.Count} 张 · 正在编辑组合图";
    }

    private void CommitInlineEditor()
    {
        if (!_isEditorMode)
        {
            return;
        }
        SetCompositePreview(InlineEditorCanvas.RenderEditedImage());
        ExitInlineEditor(discardChanges: true);
    }

    private void ExitInlineEditor(bool discardChanges)
    {
        if (!_isEditorMode && _inlineEditorImage is null)
        {
            return;
        }
        EndInlineEditorPanning();
        InlineEditorCanvas.Reset();
        _inlineEditorImage?.Dispose();
        _inlineEditorImage = null;
        _isEditorMode = false;
        InlineEditorViewport.Visibility = Visibility.Collapsed;
        CompositeImage.Visibility = Visibility.Visible;
        GroupTextOverlay.Children.Clear();
        if (!_isCropMode)
        {
            CloseEditorToolbar();
        }
        HeaderStatusText.Text = $"钉图编组 · {_members.Count} 张 · 组合编辑";
    }

    private void OnCropClick(object sender, RoutedEventArgs e)
    {
        if (_isEditorMode)
        {
            CommitInlineEditor();
        }
        if (_isCropMode)
        {
            return;
        }

        var source = CompositePreview;
        _cropRect = new Int32Rect(0, 0, source.PixelWidth, source.PixelHeight);
        _isCropMode = true;
        CropOverlay.Visibility = Visibility.Visible;
        UpdateRenderedImageBounds();
        UpdateCropSelection();
        ShowEditorToolbar(PinnedImageToolbarMode.Crop);
        HeaderStatusText.Text = $"钉图编组 · {_members.Count} 张 · 框选裁剪组合图";
    }

    private void ExitCropMode()
    {
        _cropDragStart = null;
        _isCropMode = false;
        CropOverlay.Visibility = Visibility.Collapsed;
        if (!_isEditorMode)
        {
            CloseEditorToolbar();
        }
        HeaderStatusText.Text = _members.Count == 0
            ? "钉图编组"
            : $"钉图编组 · {_members.Count} 张 · 组合编辑";
    }

    private void ApplyInlineCrop()
    {
        ApplyCompositeCrop(_cropRect);
        ExitCropMode();
    }

    private void OnGroupCanvasSizeChanged(object sender, SizeChangedEventArgs e)
    {
        CropOverlay.Width = e.NewSize.Width;
        CropOverlay.Height = e.NewSize.Height;
        if (_isCropMode)
        {
            UpdateRenderedImageBounds();
            UpdateCropSelection();
        }
    }

    private void UpdateRenderedImageBounds()
    {
        var source = _compositePreview;
        if (source is null || GroupCanvas.ActualWidth <= 0 || GroupCanvas.ActualHeight <= 0)
        {
            _renderedImageBounds = Rect.Empty;
            return;
        }
        var scale = Math.Min(
            GroupCanvas.ActualWidth / source.PixelWidth,
            GroupCanvas.ActualHeight / source.PixelHeight);
        var width = source.PixelWidth * scale;
        var height = source.PixelHeight * scale;
        _renderedImageBounds = new Rect(
            (GroupCanvas.ActualWidth - width) / 2,
            (GroupCanvas.ActualHeight - height) / 2,
            width,
            height);
    }

    private void OnCropMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!_isCropMode || _renderedImageBounds.IsEmpty ||
            FindAncestor<Thumb>(e.OriginalSource as DependencyObject) is not null)
        {
            return;
        }
        _cropDragStart = ClampToImage(e.GetPosition(GroupCanvas));
        CropOverlay.CaptureMouse();
        UpdateCropFromDisplay(new Rect(_cropDragStart.Value, _cropDragStart.Value));
        e.Handled = true;
    }

    private void OnCropMouseMove(object sender, WpfMouseEventArgs e)
    {
        if (_cropDragStart is not { } start || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }
        var current = ClampToImage(e.GetPosition(GroupCanvas));
        UpdateCropFromDisplay(new Rect(start, current));
        e.Handled = true;
    }

    private void OnCropMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_cropDragStart is null)
        {
            return;
        }
        var current = ClampToImage(e.GetPosition(GroupCanvas));
        UpdateCropFromDisplay(new Rect(_cropDragStart.Value, current));
        _cropDragStart = null;
        CropOverlay.ReleaseMouseCapture();
        e.Handled = true;
    }

    private void OnCropHandleDragDelta(object sender, DragDeltaEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string direction } ||
            _renderedImageBounds.IsEmpty)
        {
            return;
        }
        var selection = GetCropDisplayRect();
        var adjusted = ImageCropWindow.AdjustSelectionRect(
            selection,
            _renderedImageBounds,
            direction,
            e.HorizontalChange,
            e.VerticalChange,
            Math.Max(1, _renderedImageBounds.Width / CompositePreview.PixelWidth),
            Math.Max(1, _renderedImageBounds.Height / CompositePreview.PixelHeight));
        if (string.Equals(direction, "Move", StringComparison.Ordinal))
        {
            _cropRect = ImageCropWindow.MoveCropRectWithoutResizing(
                _cropRect,
                _renderedImageBounds,
                adjusted,
                CompositePreview.PixelWidth,
                CompositePreview.PixelHeight);
            UpdateCropSelection();
        }
        else
        {
            UpdateCropFromDisplay(adjusted);
        }
    }

    private WpfPoint ClampToImage(WpfPoint point) => new(
        Math.Clamp(point.X, _renderedImageBounds.Left, _renderedImageBounds.Right),
        Math.Clamp(point.Y, _renderedImageBounds.Top, _renderedImageBounds.Bottom));

    private void UpdateCropFromDisplay(Rect selection)
    {
        _cropRect = ImageCropWindow.CalculateCropRectFromSelection(
            _renderedImageBounds,
            selection,
            CompositePreview.PixelWidth,
            CompositePreview.PixelHeight);
        UpdateCropSelection();
    }

    private Rect GetCropDisplayRect()
    {
        var scaleX = _renderedImageBounds.Width / CompositePreview.PixelWidth;
        var scaleY = _renderedImageBounds.Height / CompositePreview.PixelHeight;
        return new Rect(
            _renderedImageBounds.Left + (_cropRect.X * scaleX),
            _renderedImageBounds.Top + (_cropRect.Y * scaleY),
            Math.Max(1, _cropRect.Width * scaleX),
            Math.Max(1, _cropRect.Height * scaleY));
    }

    private void UpdateCropSelection()
    {
        if (_renderedImageBounds.IsEmpty || _cropRect.IsEmpty)
        {
            return;
        }
        var selection = GetCropDisplayRect();
        Canvas.SetLeft(CropSelection, selection.Left);
        Canvas.SetTop(CropSelection, selection.Top);
        CropSelection.Width = selection.Width;
        CropSelection.Height = selection.Height;
        Canvas.SetLeft(CropMoveThumb, selection.Left);
        Canvas.SetTop(CropMoveThumb, selection.Top);
        CropMoveThumb.Width = selection.Width;
        CropMoveThumb.Height = selection.Height;
        PositionCropThumb(CropTopLeftThumb, selection.Left, selection.Top);
        PositionCropThumb(CropTopRightThumb, selection.Right, selection.Top);
        PositionCropThumb(CropBottomLeftThumb, selection.Left, selection.Bottom);
        PositionCropThumb(CropBottomRightThumb, selection.Right, selection.Bottom);
        CropMask.Data = new CombinedGeometry(
            GeometryCombineMode.Exclude,
            new RectangleGeometry(_renderedImageBounds),
            new RectangleGeometry(selection));
        _editorToolbar?.UpdateCropSize(_cropRect.Width, _cropRect.Height);
    }

    private void PositionCropThumb(FrameworkElement thumb, double x, double y)
    {
        var position = ImageCropWindow.CalculateVisibleHandlePosition(
            _renderedImageBounds,
            x,
            y,
            thumb.Width,
            thumb.Height);
        Canvas.SetLeft(thumb, position.X);
        Canvas.SetTop(thumb, position.Y);
    }

    private static T? FindAncestor<T>(DependencyObject? source)
        where T : DependencyObject
    {
        while (source is not null)
        {
            if (source is T match)
            {
                return match;
            }
            source = source is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(source)
                : LogicalTreeHelper.GetParent(source);
        }
        return null;
    }

    internal void MoveInlineToolbar(double horizontalChange, double verticalChange)
    {
        _editorToolbar?.MoveToolbar(horizontalChange, verticalChange);
    }

    internal void ResetInlineToolbarPosition()
    {
        _editorToolbar?.ResetPosition();
    }

    private void ShowEditorToolbar(PinnedImageToolbarMode mode)
    {
        CloseEditorToolbar();
        var toolbar = _members.Count > 0
            ? _members[0].CreateEditorToolbar(this)
            : new PinnedImageEditorToolbarWindow(this);
        _editorToolbar = toolbar;
        toolbar.ToolSelected += tool =>
        {
            if (_isEditorMode)
            {
                InlineEditorCanvas.SelectTool(tool);
                InlineEditorCanvas.Focus();
            }
        };
        toolbar.EmojiSelected += emoji =>
        {
            if (_isEditorMode)
            {
                InlineEditorCanvas.SelectEmoji(emoji);
                InlineEditorCanvas.SelectTool(EditorTool.Emoji);
                InlineEditorCanvas.Focus();
            }
        };
        toolbar.ColorSelected += color =>
        {
            if (_isEditorMode)
            {
                InlineEditorCanvas.SelectColor(color);
                InlineEditorCanvas.Focus();
            }
        };
        toolbar.StrokeWidthChanged += width =>
            InlineEditorCanvas.SetStrokeWidth(width);
        toolbar.ArrowStyleSelected += style =>
            InlineEditorCanvas.SelectArrowStyle(style);
        toolbar.UndoRequested += (_, _) => InlineEditorCanvas.Undo();
        toolbar.CropRequested += (_, _) => OnCropClick(this, new RoutedEventArgs());
        toolbar.SaveRequested += (_, _) => SaveCurrentImage();
        toolbar.OcrRequested += async (_, _) => await ShowRecognizedTextAsync();
        toolbar.CopyTableRequested += async (_, _) => await CopyTableAsync();
        toolbar.CopyTextRequested += async (_, _) => await CopyRecognizedTextAsync();
        toolbar.TranslateRequested += async (_, _) => await TranslateCurrentImageAsync();
        toolbar.PrivacyRequested += async (_, _) => await RedactPrivacyAsync();
        toolbar.ApplyRequested += (_, _) =>
        {
            if (_isEditorMode)
            {
                CommitInlineEditor();
            }
            else if (_isCropMode)
            {
                ApplyInlineCrop();
            }
        };
        toolbar.CancelRequested += (_, _) =>
        {
            if (_isEditorMode)
            {
                ExitInlineEditor(discardChanges: true);
            }
            else if (_isCropMode)
            {
                ExitCropMode();
            }
        };

        if (mode == PinnedImageToolbarMode.Edit)
        {
            toolbar.ShowEdit();
        }
        else
        {
            toolbar.ShowCrop(_cropRect.Width, _cropRect.Height);
        }
    }

    private void CloseEditorToolbar()
    {
        var toolbar = _editorToolbar;
        _editorToolbar = null;
        toolbar?.Close();
    }

    private BitmapSource GetCurrentToolbarImage() =>
        _isEditorMode
            ? InlineEditorCanvas.RenderEditedImage()
            : CompositePreview;

    private async void SaveCurrentImage()
    {
        var settings = _settingsProvider?.Invoke();
        var restoreAfterPicker = settings?.PngSaveLocationMode ==
            PngSaveLocationMode.AskEveryTime &&
            IsVisible;
        try
        {
            if (restoreAfterPicker)
            {
                Hide();
            }
            var saveDirectory = await PngSaveLocationService.ResolveAsync(
                settings?.PngSaveLocationMode ?? PngSaveLocationMode.DefaultDirectory,
                settings?.SaveDirectory ?? AppMetadata.DefaultCaptureDirectory);
            if (saveDirectory is null)
            {
                return;
            }

            var path = CaptureFileService.SaveAsPng(
                GetCurrentToolbarImage(),
                saveDirectory);
            HeaderStatusText.Text =
                $"钉图编组 · 已保存 {System.IO.Path.GetFileName(path)}";
        }
        catch
        {
            HeaderStatusText.Text = "钉图编组 · 保存失败";
        }
        finally
        {
            if (restoreAfterPicker && !_isClosed)
            {
                Show();
                Activate();
            }
        }
    }

    private async Task<OcrRecognitionResult> EnsureRecognitionAsync()
    {
        if (_recognition is { IsSuccess: true } cached)
        {
            return cached;
        }

        var member = _members.Count > 0 ? _members[0] : null;
        if (member is null)
        {
            return OcrRecognitionResult.Failure("编组中没有钉图");
        }

        using var image = CapturedImage.FromBitmapSource(GetCurrentToolbarImage());
        _recognition = await member.RecognizeImageAsync(image);
        return _recognition;
    }

    private async Task ShowRecognizedTextAsync()
    {
        HeaderStatusText.Text = "钉图编组 · 正在识别文字…";
        var recognition = await EnsureRecognitionAsync();
        if (!recognition.IsSuccess || recognition.Regions.Count == 0)
        {
            HeaderStatusText.Text = recognition.ErrorMessage ?? "钉图编组 · 未识别到文字";
            return;
        }

        RenderSelectableTextOverlay(recognition.Regions);
        HeaderStatusText.Text = "钉图编组 · 文字可选择复制";
    }

    private async Task CopyRecognizedTextAsync()
    {
        HeaderStatusText.Text = "钉图编组 · 正在提取文字…";
        var recognition = await EnsureRecognitionAsync();
        if (!recognition.IsSuccess || string.IsNullOrWhiteSpace(recognition.Text))
        {
            HeaderStatusText.Text = recognition.ErrorMessage ?? "钉图编组 · 未识别到文字";
            return;
        }

        try
        {
            System.Windows.Clipboard.SetText(recognition.Text);
            HeaderStatusText.Text = "钉图编组 · 文字已复制";
        }
        catch (COMException)
        {
            HeaderStatusText.Text = "钉图编组 · 剪贴板正忙";
        }
    }

    private async Task CopyTableAsync()
    {
        HeaderStatusText.Text = "钉图编组 · 正在识别表格…";
        var member = _members.Count > 0 ? _members[0] : null;
        if (member is null)
        {
            HeaderStatusText.Text = "钉图编组 · 编组中没有钉图";
            return;
        }

        using var image = CapturedImage.FromBitmapSource(GetCurrentToolbarImage());
        var recognition = await member.RecognizeImageAsync(image);
        var table = TableRecognitionService.BuildTsv(
            recognition,
            image.Bitmap,
            await TableSupplementaryOcrService.RecognizeAsync(
                image,
                recognition.Words));
        if (!table.IsSuccess)
        {
            HeaderStatusText.Text = table.ErrorMessage ?? "钉图编组 · 未识别到表格";
            return;
        }

        try
        {
            if (!string.IsNullOrWhiteSpace(table.ClipboardHtml))
            {
                await ClipboardTextService.SetHtmlAsync(table.ClipboardHtml, table.Content);
            }
            else
            {
                await ClipboardTextService.SetTextAsync(table.Content);
            }
            HeaderStatusText.Text = "钉图编组 · 表格已复制";
        }
        catch (COMException)
        {
            HeaderStatusText.Text = "钉图编组 · 剪贴板正忙";
        }
    }

    private async Task TranslateCurrentImageAsync()
    {
        if (InlineEditorCanvas.HasTranslationOverlay)
        {
            InlineEditorCanvas.SetTranslationOverlayVisible(
                !InlineEditorCanvas.IsTranslationOverlayVisible);
            return;
        }

        HeaderStatusText.Text = "钉图编组 · 正在翻译…";
        var recognition = await EnsureRecognitionAsync();
        if (!recognition.IsSuccess)
        {
            HeaderStatusText.Text = recognition.ErrorMessage ?? "钉图编组 · 识别失败";
            return;
        }

        var member = _members.Count > 0 ? _members[0] : null;
        if (member is null)
        {
            return;
        }
        var translation = await member.TranslateRecognitionAsync(recognition);
        if (!translation.IsSuccess ||
            translation.Segments.Count != recognition.Regions.Count)
        {
            HeaderStatusText.Text = translation.ErrorMessage ?? "钉图编组 · 翻译失败";
            return;
        }

        var translated = recognition.Regions.Select((region, index) =>
            new TranslatedTextAnnotationRegion(
                new Rect(region.X, region.Y, region.Width, region.Height),
                translation.Segments[index],
                Math.Max(
                    10,
                    region.EstimatedFontSize > 0
                        ? region.EstimatedFontSize
                        : region.Height * 0.78))).ToArray();
        GroupTextOverlay.Children.Clear();
        InlineEditorCanvas.AddTranslationOverlay(translated);
        HeaderStatusText.Text = "钉图编组 · 已添加翻译，可撤销";
    }

    private async Task RedactPrivacyAsync()
    {
        HeaderStatusText.Text = "钉图编组 · 正在检测敏感信息…";
        var recognition = await EnsureRecognitionAsync();
        if (!recognition.IsSuccess)
        {
            HeaderStatusText.Text = recognition.ErrorMessage ?? "钉图编组 · 识别失败";
            return;
        }

        var candidates = PrivacyDetectionService.Detect(recognition);
        if (candidates.Count == 0)
        {
            HeaderStatusText.Text = "钉图编组 · 未检测到敏感信息";
            return;
        }

        var confirmation = new PrivacyRedactionWindow(candidates)
        {
            Owner = this,
            Topmost = true,
        };
        if (confirmation.ShowDialog() != true ||
            confirmation.SelectedCandidates.Count == 0)
        {
            HeaderStatusText.Text = "钉图编组 · 已取消隐私打码";
            return;
        }

        GroupTextOverlay.Children.Clear();
        InlineEditorCanvas.AddMosaicRegions(
            confirmation.SelectedCandidates.Select(candidate => candidate.Bounds));
        HeaderStatusText.Text =
            $"钉图编组 · 已添加 {confirmation.SelectedCandidates.Count} 处隐私马赛克";
    }

    private void RenderSelectableTextOverlay(IReadOnlyList<OcrTextRegion> regions)
    {
        GroupTextOverlay.Children.Clear();
        var source = GetCurrentToolbarImage();
        if (GroupCanvas.ActualWidth <= 0 || GroupCanvas.ActualHeight <= 0 ||
            source.PixelWidth <= 0 || source.PixelHeight <= 0)
        {
            return;
        }

        var scale = Math.Min(
            GroupCanvas.ActualWidth / source.PixelWidth,
            GroupCanvas.ActualHeight / source.PixelHeight);
        var renderedWidth = source.PixelWidth * scale;
        var renderedHeight = source.PixelHeight * scale;
        var offsetX = (GroupCanvas.ActualWidth - renderedWidth) / 2;
        var offsetY = (GroupCanvas.ActualHeight - renderedHeight) / 2;
        foreach (var region in regions)
        {
            var textBox = new WpfTextBox
            {
                Text = region.Text,
                Width = Math.Max(12, region.Width * scale + 2),
                Height = Math.Max(16, region.Height * scale + 2),
                Background = WpfBrushes.Transparent,
                BorderThickness = new Thickness(0),
                Cursor = WpfCursors.IBeam,
                FontFamily = new WpfFontFamily("Microsoft YaHei UI"),
                FontSize = Math.Max(10, region.Height * scale * 0.78),
                Foreground = WpfBrushes.Transparent,
                IsReadOnly = true,
                IsTabStop = false,
                SelectionBrush = new SolidColorBrush(
                    WpfColor.FromArgb(150, 46, 175, 165)),
                SelectionTextBrush = WpfBrushes.Transparent,
            };
            Canvas.SetLeft(textBox, offsetX + (region.X * scale));
            Canvas.SetTop(textBox, offsetY + (region.Y * scale));
            GroupTextOverlay.Children.Add(textBox);
        }
    }

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_isEditorMode)
            {
                System.Windows.Clipboard.SetImage(InlineEditorCanvas.RenderEditedImage());
            }
            else
            {
                System.Windows.Clipboard.SetImage(CompositePreview);
            }
        }
        catch (COMException)
        {
        }
    }

    internal static BitmapSource ComposeImages(IReadOnlyList<BitmapSource> images) =>
        ComposeImagesWithLayout(images).Image;

    private static CompositionResult ComposeImagesWithLayout(
        IReadOnlyList<BitmapSource> images)
    {
        ArgumentNullException.ThrowIfNull(images);
        if (images.Count == 0)
        {
            throw new ArgumentException("At least one image is required.", nameof(images));
        }
        const int gap = 1;
        var columns = Math.Min(2, images.Count);
        var columnHeights = new int[columns];
        var columnWidths = new int[columns];
        var placements = new List<(BitmapSource Image, int Column, int Top)>(images.Count);
        for (var index = 0; index < images.Count; index++)
        {
            var image = images[index];
            var column = index < columns
                ? index
                : Array.IndexOf(columnHeights, columnHeights.Min());
            placements.Add((image, column, columnHeights[column]));
            columnHeights[column] += image.PixelHeight + gap;
            columnWidths[column] = Math.Max(columnWidths[column], image.PixelWidth);
        }

        var columnOffsets = new int[columns];
        for (var column = 1; column < columns; column++)
        {
            columnOffsets[column] = columnOffsets[column - 1] +
                columnWidths[column - 1] + gap;
        }

        var rawWidth = columnWidths.Sum() + (gap * Math.Max(0, columns - 1));
        var rawHeight = Math.Max(1, columnHeights.Max() - gap);
        const int maximumOutputDimension = 8192;
        const long maximumOutputPixels = 48_000_000;
        var scale = Math.Min(
            1,
            Math.Min(
                maximumOutputDimension / (double)rawWidth,
                maximumOutputDimension / (double)rawHeight));
        scale = Math.Min(
            scale,
            Math.Sqrt(maximumOutputPixels /
                (double)rawWidth * rawHeight));
        var outputWidth = Math.Max(1, (int)Math.Round(rawWidth * scale));
        var outputHeight = Math.Max(1, (int)Math.Round(rawHeight * scale));
        var memberBounds = new List<Int32Rect>(images.Count);
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            // The composite is copied directly to the clipboard, so its
            // transparent areas need a visible checkerboard baked into it.
            drawing.DrawRectangle(
                CompositeCheckerboardBrush,
                null,
                new Rect(0, 0, outputWidth, outputHeight));
            foreach (var placement in placements)
            {
                var width = Math.Max(1, placement.Image.PixelWidth * scale);
                var height = Math.Max(1, placement.Image.PixelHeight * scale);
                var destination = new Rect(
                    columnOffsets[placement.Column] * scale,
                    placement.Top * scale,
                    width,
                    height);
                drawing.DrawImage(placement.Image, destination);
                var left = Math.Clamp((int)Math.Floor(destination.Left), 0, outputWidth - 1);
                var top = Math.Clamp((int)Math.Floor(destination.Top), 0, outputHeight - 1);
                var right = Math.Clamp((int)Math.Ceiling(destination.Right), left + 1, outputWidth);
                var bottom = Math.Clamp((int)Math.Ceiling(destination.Bottom), top + 1, outputHeight);
                memberBounds.Add(new Int32Rect(left, top, right - left, bottom - top));
            }
        }
        var result = new RenderTargetBitmap(
            outputWidth,
            outputHeight,
            96,
            96,
            PixelFormats.Pbgra32);
        result.Render(visual);
        result.Freeze();
        return new CompositionResult(result, memberBounds);
    }

    private static DrawingBrush CreateCompositeCheckerboardBrush()
    {
        const double squareSize = 6;
        const double tileSize = squareSize * 2;
        var tile = new DrawingGroup();
        using (var drawing = tile.Open())
        {
            drawing.DrawRectangle(
                WpfBrushes.White,
                null,
                new Rect(0, 0, tileSize, tileSize));
            var gray = new SolidColorBrush(WpfColor.FromRgb(156, 156, 156));
            gray.Freeze();
            drawing.DrawRectangle(gray, null, new Rect(0, 0, squareSize, squareSize));
            drawing.DrawRectangle(
                gray,
                null,
                new Rect(squareSize, squareSize, squareSize, squareSize));
        }
        tile.Freeze();

        var brush = new DrawingBrush(tile)
        {
            TileMode = TileMode.Tile,
            Viewport = new Rect(0, 0, tileSize, tileSize),
            ViewportUnits = BrushMappingMode.Absolute,
            Viewbox = new Rect(0, 0, tileSize, tileSize),
            ViewboxUnits = BrushMappingMode.Absolute,
        };
        brush.Freeze();
        return brush;
    }

    private void OnUngroupClick(object sender, RoutedEventArgs e) =>
        UngroupRequested?.Invoke(this, EventArgs.Empty);

    private void OnOpenSettingsClick(object sender, RoutedEventArgs e) =>
        SettingsRequested?.Invoke(this, EventArgs.Empty);

    private void OnHideAllClick(object sender, RoutedEventArgs e) =>
        HideAllRequested?.Invoke(this, EventArgs.Empty);

    private void OnMinimizeClick(object sender, RoutedEventArgs e) =>
        MinimizeRequested?.Invoke(this, EventArgs.Empty);

    private void OnRestoreClick(object sender, RoutedEventArgs e) =>
        RestoreRequested?.Invoke(this, EventArgs.Empty);

    private void OnCloseClick(object sender, RoutedEventArgs e) =>
        CloseGroupRequested?.Invoke(this, EventArgs.Empty);

    private void OnOpacityValueChanged(
        object sender,
        RoutedPropertyChangedEventArgs<double> e) => Opacity = e.NewValue;

    private void OnCanvasMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_isEditorMode)
        {
            var pointer = e.GetPosition(InlineEditorViewport);
            var previousZoom = InlineEditorCanvas.Zoom;
            var contentX =
                (InlineEditorViewport.HorizontalOffset + pointer.X) / previousZoom;
            var contentY =
                (InlineEditorViewport.VerticalOffset + pointer.Y) / previousZoom;
            var editorZoomFactor = e.Delta > 0 ? 1.1 : 1 / 1.1;
            InlineEditorCanvas.SetZoom(InlineEditorCanvas.Zoom * editorZoomFactor);
            InlineEditorFrame.Width = InlineEditorCanvas.DisplayWidth;
            InlineEditorFrame.Height = InlineEditorCanvas.DisplayHeight;
            InlineEditorViewport.UpdateLayout();
            InlineEditorViewport.ScrollToHorizontalOffset(
                (contentX * InlineEditorCanvas.Zoom) - pointer.X);
            InlineEditorViewport.ScrollToVerticalOffset(
                (contentY * InlineEditorCanvas.Zoom) - pointer.Y);
            HeaderStatusText.Text = $"钉图编组 · {_members.Count} 张 · 正在编辑 · {InlineEditorCanvas.Zoom * 100:0}%";
            e.Handled = true;
            return;
        }
        if (_isCropMode)
        {
            e.Handled = true;
            return;
        }
        var factor = e.Delta > 0 ? 1.08 : 1 / 1.08;
        var previousWidth = Width;
        var previousHeight = Height;
        var nextWidth = Math.Clamp(previousWidth * factor, MinWidth, MaxWidth);
        var nextHeight = Math.Clamp(previousHeight * factor, MinHeight, MaxHeight);
        Left -= (nextWidth - previousWidth) / 2;
        Top -= (nextHeight - previousHeight) / 2;
        Width = nextWidth;
        Height = nextHeight;
        e.Handled = true;
    }

    private void OnInlineEditorPreviewMouseDown(
        object sender,
        MouseButtonEventArgs e)
    {
        if (!_isEditorMode || e.ChangedButton != MouseButton.Middle)
        {
            return;
        }

        _isPanningInlineEditor = true;
        _inlineEditorPanStartPoint = e.GetPosition(InlineEditorViewport);
        _inlineEditorPanStartHorizontalOffset = InlineEditorViewport.HorizontalOffset;
        _inlineEditorPanStartVerticalOffset = InlineEditorViewport.VerticalOffset;
        InlineEditorViewport.Cursor = WpfCursors.SizeAll;
        _ = InlineEditorViewport.CaptureMouse();
        e.Handled = true;
    }

    private void OnInlineEditorPreviewMouseMove(object sender, WpfMouseEventArgs e)
    {
        if (!_isPanningInlineEditor)
        {
            return;
        }
        if (e.MiddleButton != MouseButtonState.Pressed)
        {
            EndInlineEditorPanning();
            return;
        }

        var current = e.GetPosition(InlineEditorViewport);
        InlineEditorViewport.ScrollToHorizontalOffset(
            _inlineEditorPanStartHorizontalOffset + _inlineEditorPanStartPoint.X - current.X);
        InlineEditorViewport.ScrollToVerticalOffset(
            _inlineEditorPanStartVerticalOffset + _inlineEditorPanStartPoint.Y - current.Y);
        e.Handled = true;
    }

    private void OnInlineEditorPreviewMouseUp(
        object sender,
        MouseButtonEventArgs e)
    {
        if (_isPanningInlineEditor && e.ChangedButton == MouseButton.Middle)
        {
            EndInlineEditorPanning();
            e.Handled = true;
        }
    }

    private void OnInlineEditorLostMouseCapture(object sender, WpfMouseEventArgs e) =>
        EndInlineEditorPanning();

    private void EndInlineEditorPanning()
    {
        _isPanningInlineEditor = false;
        if (InlineEditorViewport.IsMouseCaptured)
        {
            InlineEditorViewport.ReleaseMouseCapture();
        }
        InlineEditorViewport.Cursor = WpfCursors.Arrow;
    }

}
