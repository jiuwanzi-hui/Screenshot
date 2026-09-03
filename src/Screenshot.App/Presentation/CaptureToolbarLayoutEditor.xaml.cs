using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using MediaBrush = System.Windows.Media.Brush;
using MediaFontFamily = System.Windows.Media.FontFamily;
using WpfPath = System.Windows.Shapes.Path;
using Screenshot.App.Core;
using Screenshot.App.Editor;

namespace Screenshot.App.Presentation;

public partial class CaptureToolbarLayoutEditor : System.Windows.Controls.UserControl
{
    private SettingsViewModel? _viewModel;
    private System.Windows.Point _dragStart;
    private CaptureToolbarFeatureItem? _draggedItem;
    private DropIndicatorAdorner? _dropIndicator;
    private AdornerLayer? _dropIndicatorLayer;
    private ToggleButton? _dropTarget;
    private bool _insertAfterTarget;
    private bool _isRefreshing;

    public CaptureToolbarLayoutEditor()
    {
        InitializeComponent();
    }

    public event EventHandler? LayoutChanged;

    private void OnNestedScrollViewerPreviewMouseWheel(
        object sender,
        MouseWheelEventArgs e)
    {
        e.Handled = true;
        RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
        {
            RoutedEvent = Mouse.MouseWheelEvent,
            Source = this,
        });
    }

    private void OnDataContextChanged(
        object sender,
        DependencyPropertyChangedEventArgs e)
    {
        DetachViewModel();
        _viewModel = e.NewValue as SettingsViewModel;
        AttachViewModel();
        Refresh();
    }

    private void AttachViewModel()
    {
        if (_viewModel is null)
        {
            return;
        }

        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        _viewModel.CaptureToolbarFeatureItems.CollectionChanged +=
            OnFeatureCollectionChanged;
        foreach (var item in _viewModel.CaptureToolbarFeatureItems)
        {
            item.PropertyChanged += OnFeaturePropertyChanged;
        }
    }

    private void DetachViewModel()
    {
        if (_viewModel is null)
        {
            return;
        }

        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel.CaptureToolbarFeatureItems.CollectionChanged -=
            OnFeatureCollectionChanged;
        foreach (var item in _viewModel.CaptureToolbarFeatureItems)
        {
            item.PropertyChanged -= OnFeaturePropertyChanged;
        }
    }

    private void OnViewModelPropertyChanged(
        object? sender,
        PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SettingsViewModel.CaptureToolbarRows))
        {
            Refresh();
        }
        else if (e.PropertyName == nameof(SettingsViewModel.ToolbarScalePercent))
        {
            ApplyToolbarScale();
        }
    }

    private void OnFeatureCollectionChanged(
        object? sender,
        System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (CaptureToolbarFeatureItem item in e.OldItems)
            {
                item.PropertyChanged -= OnFeaturePropertyChanged;
            }
        }

        if (e.NewItems is not null)
        {
            foreach (CaptureToolbarFeatureItem item in e.NewItems)
            {
                item.PropertyChanged += OnFeaturePropertyChanged;
            }
        }

        Refresh();
    }

    private void OnFeaturePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CaptureToolbarFeatureItem.IsVisible))
        {
            Refresh();
        }
    }

    private void OnRowCountChecked(object sender, RoutedEventArgs e)
    {
        if (_isRefreshing || _viewModel is null ||
            sender is not FrameworkElement { Tag: string tag } ||
            !int.TryParse(tag, out var value) ||
            !Enum.IsDefined(typeof(CaptureToolbarRowCount), value))
        {
            return;
        }

        var rows = (CaptureToolbarRowCount)value;
        if (_viewModel.CaptureToolbarRows == rows)
        {
            return;
        }

        _viewModel.CaptureToolbarRows = rows;
        Refresh();
        LayoutChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Refresh()
    {
        _isRefreshing = true;
        try
        {
            EditorRowsHost.Children.Clear();
            PreviewRowsHost.Children.Clear();
            ClearDropIndicator();
            if (_viewModel is null)
            {
                return;
            }

            OneRowButton.IsChecked =
                _viewModel.CaptureToolbarRows == CaptureToolbarRowCount.One;
            TwoRowsButton.IsChecked =
                _viewModel.CaptureToolbarRows == CaptureToolbarRowCount.Two;
            BuildEditorRows();
            BuildPreviewRows();
            ApplyToolbarScale();
        }
        finally
        {
            _isRefreshing = false;
        }
    }

    private void ApplyToolbarScale()
    {
        var scale = _viewModel is null ||
                    !double.IsFinite(_viewModel.ToolbarScalePercent)
            ? 1
            : Math.Clamp(_viewModel.ToolbarScalePercent / 100d, 0.5, 1.5);
        ApplyScale(EditorRowsHost, scale);
        ApplyScale(PreviewRowsHost, scale);
    }

    private static void ApplyScale(FrameworkElement element, double scale)
    {
        if (element.LayoutTransform is ScaleTransform transform)
        {
            transform.ScaleX = scale;
            transform.ScaleY = scale;
            return;
        }

        element.LayoutTransform = new ScaleTransform(scale, scale);
    }

    private void BuildEditorRows()
    {
        if (_viewModel is null)
        {
            return;
        }

        var tokens = new List<FrameworkElement>();
        var groups = Enum.GetValues<CaptureToolbarFeatureGroup>();
        foreach (var group in groups)
        {
            if (tokens.Count > 0)
            {
                tokens.Add(CreateSeparator());
            }

            foreach (var item in _viewModel.CaptureToolbarFeatureItems.Where(
                         candidate => candidate.Group == group))
            {
                tokens.Add(CreateEditorButton(item));
                if (item.Feature == CaptureToolbarFeature.UndoRedo)
                {
                    tokens.Add(CreateEditorButton(item, "↷", "重做"));
                }
            }
        }

        tokens.Add(CreateSeparator());
        tokens.Add(CreateFixedButton("×", "取消截图", false));
        tokens.Add(CreateFixedButton("✓", "完成并复制", true));
        AddRows(EditorRowsHost, tokens, _viewModel.CaptureToolbarRows);
    }

    private void BuildPreviewRows()
    {
        if (_viewModel is null)
        {
            return;
        }

        var tokens = new List<FrameworkElement>();
        foreach (var group in Enum.GetValues<CaptureToolbarFeatureGroup>())
        {
            var items = _viewModel.CaptureToolbarFeatureItems
                .Where(item => item.Group == group && item.IsVisible)
                .ToList();
            if (items.Count == 0)
            {
                continue;
            }

            if (tokens.Count > 0)
            {
                tokens.Add(CreateSeparator());
            }

            foreach (var item in items)
            {
                tokens.Add(CreatePreviewButton(item.Feature, item.Glyph, item.Label));
                if (item.Feature == CaptureToolbarFeature.UndoRedo)
                {
                    tokens.Add(CreatePreviewButton(item.Feature, "↷", "重做"));
                }
            }
        }

        if (tokens.Count > 0)
        {
            tokens.Add(CreateSeparator());
        }

        tokens.Add(CreateFixedButton("×", "取消截图", false));
        tokens.Add(CreateFixedButton("✓", "完成并复制", true));
        AddRows(PreviewRowsHost, tokens, _viewModel.CaptureToolbarRows);
    }

    private ToggleButton CreateEditorButton(
        CaptureToolbarFeatureItem item,
        string? glyph = null,
        string? toolTip = null)
    {
        var button = new ToggleButton
        {
            Style = (Style)FindResource("ToolbarDesignerButton"),
            IsChecked = item.IsVisible,
            Tag = item,
            ToolTip = toolTip ?? item.Label,
            Cursor = System.Windows.Input.Cursors.Hand,
            AllowDrop = true,
            Content = CreateToolbarIcon(item.Feature, glyph ?? item.Glyph, 20),
        };
        button.SetResourceReference(ForegroundProperty,
            item.IsVisible ? "EditorToolbarIconBrush" : "AppTextSecondaryBrush");
        button.Opacity = item.IsVisible ? 1 : 0.38;
        button.Click += OnFeatureButtonClick;
        button.PreviewMouseLeftButtonDown += OnFeatureButtonMouseDown;
        button.PreviewMouseMove += OnFeatureButtonMouseMove;
        button.DragOver += OnFeatureButtonDragOver;
        button.DragLeave += OnFeatureButtonDragLeave;
        button.Drop += OnFeatureButtonDrop;
        return button;
    }

    private System.Windows.Controls.Button CreatePreviewButton(
        CaptureToolbarFeature? feature,
        string glyph,
        string toolTip)
    {
        var button = new System.Windows.Controls.Button
        {
            Width = 36,
            Height = 32,
            Margin = new Thickness(0, 0, 5, 0),
            Padding = new Thickness(0),
            IsHitTestVisible = false,
            ToolTip = toolTip,
            Content = CreateToolbarIcon(feature, glyph, 20),
        };
        button.SetResourceReference(BackgroundProperty,
            "EditorToolbarButtonBackgroundBrush");
        button.SetResourceReference(BorderBrushProperty,
            "EditorToolbarButtonBorderBrush");
        return button;
    }

    private System.Windows.Controls.Button CreateFixedButton(
        string glyph,
        string toolTip,
        bool confirm)
    {
        var button = CreatePreviewButton(null, glyph, toolTip);
        button.IsHitTestVisible = false;
        if (confirm)
        {
            button.SetResourceReference(BackgroundProperty,
                "EditorToolbarConfirmBackgroundBrush");
            button.SetResourceReference(BorderBrushProperty,
                "EditorToolbarConfirmBorderBrush");
        }

        return button;
    }

    private FrameworkElement CreateToolbarIcon(
        CaptureToolbarFeature? feature,
        string glyph,
        double size)
    {
        if (feature == CaptureToolbarFeature.Emoji)
        {
            return new EmojiStickerImage
            {
                Width = size + 2,
                Height = size + 2,
                Sticker = "😊",
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = System.Windows.VerticalAlignment.Center,
            };
        }

        if (feature == CaptureToolbarFeature.Number)
        {
            return new Border
            {
                Width = size - 1,
                Height = size - 1,
                Background = ResolveBrush("EditorToolbarIconBrush", Colors.DimGray),
                CornerRadius = new CornerRadius((size - 1) / 2),
                Child = new TextBlock
                {
                    Text = "1",
                    Foreground = ResolveBrush("EditorToolbarButtonBackgroundBrush", Colors.White),
                    FontSize = 12,
                    FontWeight = FontWeights.Bold,
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                    VerticalAlignment = System.Windows.VerticalAlignment.Center,
                },
            };
        }

        if (feature is CaptureToolbarFeature.TextRecognition or
            CaptureToolbarFeature.CopyRecognizedText or
            CaptureToolbarFeature.Translation or
            CaptureToolbarFeature.PrivacyRedaction)
        {
            return new TextBlock
            {
                Text = glyph,
                FontFamily = new MediaFontFamily("Microsoft YaHei UI"),
                FontSize = 18,
                FontWeight = FontWeights.SemiBold,
                Foreground = ResolveBrush("EditorToolbarIconBrush", Colors.DimGray),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = System.Windows.VerticalAlignment.Center,
            };
        }

        var resourceKey = feature switch
        {
            CaptureToolbarFeature.Shape => "RectangleIconGeometry",
            CaptureToolbarFeature.Arrow => "ArrowIconGeometry",
            CaptureToolbarFeature.Brush => "PenIconGeometry",
            CaptureToolbarFeature.Text => "TextIconGeometry",
            CaptureToolbarFeature.Mosaic => "MosaicIconGeometry",
            CaptureToolbarFeature.VideoRecording => "RecordIconGeometry",
            CaptureToolbarFeature.Save => "SaveIconGeometry",
            CaptureToolbarFeature.ScrollCapture => "ScrollIconGeometry",
            CaptureToolbarFeature.CopyTable => "TableIconGeometry",
            CaptureToolbarFeature.TextRecognition or
                CaptureToolbarFeature.CopyRecognizedText => "OcrIconGeometry",
            CaptureToolbarFeature.PinImage => "PinIconGeometry",
            CaptureToolbarFeature.UndoRedo when glyph == "↷" => "RedoIconGeometry",
            CaptureToolbarFeature.UndoRedo => "UndoIconGeometry",
            null when glyph == "×" => "CancelIconGeometry",
            null when glyph == "✓" => "ConfirmIconGeometry",
            _ => null,
        };
        if (resourceKey is not null && TryFindResource(resourceKey) is Geometry geometry)
        {
            var path = new WpfPath { Data = geometry };
            path.SetResourceReference(StyleProperty, "ToolbarPreviewIcon");
            path.Width = size - 2;
            path.Height = size - 2;
            if (feature is null)
            {
                path.Stroke = ResolveBrush(
                    glyph == "✓"
                        ? "EditorToolbarConfirmIconBrush"
                        : "EditorToolbarCancelIconBrush",
                    Colors.White);
            }

            return path;
        }

        return new TextBlock
        {
            Text = glyph,
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = System.Windows.VerticalAlignment.Center,
        };
    }

    private MediaBrush ResolveBrush(string key, System.Windows.Media.Color fallback)
    {
        if (TryFindResource(key) is MediaBrush brush)
        {
            return brush;
        }

        if (System.Windows.Application.Current?.TryFindResource(key) is MediaBrush applicationBrush)
        {
            return applicationBrush;
        }

        return new SolidColorBrush(fallback);
    }

    private static Border CreateSeparator() => new()
    {
        Width = 2,
        Height = 26,
        Margin = new Thickness(6, 3, 11, 3),
        Background = new SolidColorBrush(
            System.Windows.Media.Color.FromRgb(63, 210, 198)),
        CornerRadius = new CornerRadius(1),
        Tag = "Separator",
    };

    private static void AddRows(
        System.Windows.Controls.Panel host,
        IReadOnlyList<FrameworkElement> tokens,
        CaptureToolbarRowCount rowCount)
    {
        var rows = rowCount == CaptureToolbarRowCount.Two ? 2 : 1;
        var split = rows == 1 ? tokens.Count : FindBalancedSplit(tokens);
        AddRow(host, tokens.Take(split));
        if (rows == 2)
        {
            AddRow(host, tokens.Skip(split), new Thickness(0, 6, 0, 0));
        }
    }

    private static int FindBalancedSplit(IReadOnlyList<FrameworkElement> tokens)
    {
        var buttonCount = tokens.Count(item => item.Tag as string != "Separator");
        var target = (buttonCount + 1) / 2;
        var seen = 0;
        for (var index = 0; index < tokens.Count; index++)
        {
            if (tokens[index].Tag as string != "Separator")
            {
                seen++;
            }

            if (seen >= target)
            {
                var split = index + 1;
                while (split < tokens.Count &&
                       tokens[split].Tag as string == "Separator")
                {
                    split++;
                }

                return split;
            }
        }

        return tokens.Count;
    }

    private static void AddRow(
        System.Windows.Controls.Panel host,
        IEnumerable<FrameworkElement> rowItems,
        Thickness? margin = null)
    {
        var row = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            Margin = margin ?? new Thickness(0),
        };
        var items = rowItems.ToList();
        while (items.Count > 0 && items[0].Tag as string == "Separator")
        {
            items.RemoveAt(0);
        }

        while (items.Count > 0 && items[^1].Tag as string == "Separator")
        {
            items.RemoveAt(items.Count - 1);
        }

        foreach (var item in items)
        {
            row.Children.Add(item);
        }

        host.Children.Add(row);
    }

    private void OnFeatureButtonClick(object sender, RoutedEventArgs e)
    {
        if (_isRefreshing || sender is not ToggleButton
            {
                Tag: CaptureToolbarFeatureItem item
            })
        {
            return;
        }

        item.IsVisible = !item.IsVisible;
        LayoutChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnFeatureButtonMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is ToggleButton { Tag: CaptureToolbarFeatureItem item })
        {
            _dragStart = e.GetPosition(this);
            _draggedItem = item;
        }
    }

    private void OnFeatureButtonMouseMove(
        object sender,
        System.Windows.Input.MouseEventArgs e)
    {
        if (_draggedItem is null || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var current = e.GetPosition(this);
        if (Math.Abs(current.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(current.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        if (sender is not ToggleButton sourceButton)
        {
            return;
        }

        var originalOpacity = sourceButton.Opacity;
        sourceButton.Opacity = 0.5;
        try
        {
            _ = System.Windows.DragDrop.DoDragDrop(
                sourceButton,
                new System.Windows.DataObject(
                    typeof(CaptureToolbarFeatureItem),
                    _draggedItem),
                System.Windows.DragDropEffects.Move);
        }
        finally
        {
            sourceButton.Opacity = originalOpacity;
            _draggedItem = null;
            ClearDropIndicator();
        }
    }

    private void OnFeatureButtonDragOver(
        object sender,
        System.Windows.DragEventArgs e)
    {
        if (sender is not ToggleButton { Tag: CaptureToolbarFeatureItem target } ||
            e.Data.GetData(typeof(CaptureToolbarFeatureItem)) is not
                CaptureToolbarFeatureItem source ||
            source.Group != target.Group)
        {
            ClearDropIndicator();
            e.Effects = System.Windows.DragDropEffects.None;
            e.Handled = true;
            return;
        }

        var button = (ToggleButton)sender;
        var insertAfter = e.GetPosition(button).X >= button.ActualWidth / 2;
        ShowDropIndicator(button, insertAfter);
        e.Effects = System.Windows.DragDropEffects.Move;
        e.Handled = true;
    }

    private void OnFeatureButtonDragLeave(
        object sender,
        System.Windows.DragEventArgs e)
    {
        if (ReferenceEquals(sender, _dropTarget))
        {
            ClearDropIndicator();
        }
    }

    private void OnFeatureButtonDrop(
        object sender,
        System.Windows.DragEventArgs e)
    {
        if (_viewModel is null ||
            sender is not ToggleButton { Tag: CaptureToolbarFeatureItem target } ||
            e.Data.GetData(typeof(CaptureToolbarFeatureItem)) is not
                CaptureToolbarFeatureItem source)
        {
            ClearDropIndicator();
            return;
        }

        var moved = _viewModel.MoveCaptureToolbarFeature(
            source.Feature,
            target.Feature,
            _insertAfterTarget);
        ClearDropIndicator();
        if (!moved)
        {
            e.Handled = true;
            return;
        }

        Refresh();
        LayoutChanged?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    private void ShowDropIndicator(ToggleButton target, bool insertAfter)
    {
        if (ReferenceEquals(_dropTarget, target) &&
            _insertAfterTarget == insertAfter)
        {
            return;
        }

        ClearDropIndicator();
        _dropTarget = target;
        _insertAfterTarget = insertAfter;
        var layer = AdornerLayer.GetAdornerLayer(target);
        if (layer is null)
        {
            return;
        }

        _dropIndicatorLayer = layer;
        _dropIndicator = new DropIndicatorAdorner(target, insertAfter);
        layer.Add(_dropIndicator);
    }

    private void ClearDropIndicator()
    {
        if (_dropIndicator is not null && _dropIndicatorLayer is not null)
        {
            _dropIndicatorLayer.Remove(_dropIndicator);
        }

        _dropIndicator = null;
        _dropIndicatorLayer = null;
        _dropTarget = null;
    }

    private sealed class DropIndicatorAdorner : Adorner
    {
        private readonly bool _insertAfter;

        public DropIndicatorAdorner(UIElement adornedElement, bool insertAfter)
            : base(adornedElement)
        {
            _insertAfter = insertAfter;
            IsHitTestVisible = false;
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            var width = AdornedElement.RenderSize.Width;
            var height = AdornedElement.RenderSize.Height;
            var x = _insertAfter ? Math.Max(1.5, width - 1.5) : 1.5;
            var brush = new SolidColorBrush(
                System.Windows.Media.Color.FromRgb(90, 245, 229));
            var glowPen = new System.Windows.Media.Pen(
                new SolidColorBrush(
                    System.Windows.Media.Color.FromArgb(90, 90, 245, 229)),
                7);
            var linePen = new System.Windows.Media.Pen(brush, 3);
            drawingContext.DrawLine(
                glowPen,
                new System.Windows.Point(x, 2),
                new System.Windows.Point(x, Math.Max(2, height - 2)));
            drawingContext.DrawLine(
                linePen,
                new System.Windows.Point(x, 2),
                new System.Windows.Point(x, Math.Max(2, height - 2)));
            drawingContext.DrawEllipse(
                brush,
                null,
                new System.Windows.Point(x, 3),
                3,
                3);
            drawingContext.DrawEllipse(
                brush,
                null,
                new System.Windows.Point(x, Math.Max(3, height - 3)),
                3,
                3);
        }
    }
}
