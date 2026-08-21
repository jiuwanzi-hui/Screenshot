using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using SnapCut.Core;
using SnapCut.Mac.App;
using SnapCut.Mac.Capture;
using SnapCut.Mac.Editor;
using SnapCut.Mac.Native;

namespace SnapCut.Mac.Presentation;

internal sealed class RegionSelectionWindow : Window
{
    private readonly TaskCompletionSource<MacCaptureSelection?> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly SelectionCanvas _selectionCanvas;
    private readonly Border _sizeBadge;
    private readonly TextBlock _sizeText;
    private readonly Border _selectionToolbar;
    private readonly Dictionary<MacEditorTool, Button> _toolButtons = [];
    private readonly Dictionary<string, List<Control>> _featureControls =
        new(StringComparer.Ordinal);
    private readonly Button _undoButton;
    private readonly Button _redoButton;
    private readonly bool _scrollCapture;
    private readonly MacSettings _settings;
    private readonly Border _colorBadge;
    private readonly Border _colorSwatch;
    private readonly TextBlock _colorText;
    private Color _sampledColor;
    private readonly MacCaptureAction _defaultAction;
    private MacColorPickerWindow? _colorPicker;
    private bool _toolbarDragging;
    private Point _toolbarDragStart;
    private Thickness _toolbarMarginStart;
    private TextBox? _textInput;
    private TextBox? _emojiInput;

    public RegionSelectionWindow(
        MacDisplay display,
        PixelImage desktop,
        bool scrollCapture,
        MacSettings settings,
        MacCaptureAction defaultAction = MacCaptureAction.Complete)
    {
        _scrollCapture = scrollCapture;
        _settings = settings;
        _defaultAction = defaultAction;
        Display = display;
        Title = "SnapCut 框选";
        SystemDecorations = SystemDecorations.None;
        ShowInTaskbar = false;
        Topmost = true;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Position = new PixelPoint(
            (int)Math.Round(display.Bounds.Left),
            (int)Math.Round(display.Bounds.Top));
        Width = display.Bounds.Size.Width;
        Height = display.Bounds.Size.Height;
        Background = Brushes.Black;

        _selectionCanvas = new SelectionCanvas(
            PixelImageBitmap.Create(desktop),
            desktop);
        if (Color.TryParse(settings.AnnotationColor, out var savedColor))
        {
            _selectionCanvas.AnnotationColor = savedColor;
        }
        _selectionCanvas.AnnotationWidth = Math.Clamp(
            settings.AnnotationWidth,
            1,
            10);
        _selectionCanvas.ArrowStyle = settings.ArrowStyle == "Hollow"
            ? MacArrowStyle.Hollow
            : MacArrowStyle.Filled;
        _selectionCanvas.SelectionReady += ShowSelectionTools;
        _selectionCanvas.SelectionChanged += UpdateSelectionSize;
        _selectionCanvas.AnnotationStateChanged += UpdateUndoRedo;
        _selectionCanvas.AnnotationSelectionChanged += LoadSelectedAnnotation;
        _selectionCanvas.ColorSampleChanged += UpdateColorSample;
        _selectionCanvas.WindowSnapRequested += UpdateWindowSnap;
        _selectionCanvas.CancelRequested += () =>
            _completion.TrySetResult(null);
        var hint = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(220, 30, 29, 37)),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(14, 8),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 20, 0, 0),
            IsHitTestVisible = false,
            Child = new TextBlock
            {
                Text = scrollCapture
                    ? "拖动框选长截图区域 · Esc/右键取消"
                    : "拖动框选截图区域 · Esc/右键取消",
                Foreground = Brushes.White,
                FontSize = 13,
            },
        };
        _sizeText = new TextBlock
        {
            Foreground = Brushes.White,
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
        };
        _sizeBadge = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(225, 30, 29, 37)),
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(10, 5),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            IsHitTestVisible = false,
            IsVisible = false,
            Child = _sizeText,
        };
        _colorSwatch = new Border
        {
            Width = 26,
            Height = 26,
            CornerRadius = new CornerRadius(4),
            BorderBrush = new SolidColorBrush(Color.Parse("#9AA8B8")),
            BorderThickness = new Thickness(1),
        };
        _colorText = new TextBlock
        {
            Text = "#000000  C 复制",
            Foreground = Brushes.White,
            FontFamily = FontFamily.Default,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _colorBadge = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(232, 30, 29, 37)),
            BorderBrush = new SolidColorBrush(Color.Parse("#52627486")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(7),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            IsHitTestVisible = false,
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 7,
                Children = { _colorSwatch, _colorText },
            },
        };
        var retry = CreateToolbarButton("↺", "重新框选");
        var cancel = CreateToolbarButton("×", "取消");
        cancel.Foreground = new SolidColorBrush(Color.Parse("#F87171"));
        var confirm = CreateToolbarButton("✓", "完成");
        confirm.Background = MacTheme.AccentBrush;
        confirm.Foreground = Brushes.White;
        _undoButton = CreateToolbarButton("↶", "撤销");
        _redoButton = CreateToolbarButton("↷", "重做");
        _undoButton.Tag = "UndoRedo";
        _redoButton.Tag = "UndoRedo";
        _undoButton.Click += (_, _) => _selectionCanvas.Undo();
        _redoButton.Click += (_, _) => _selectionCanvas.Redo();

        var firstRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
        };
        firstRow.Children.Add(CreateToolbarGrip());
        if (!scrollCapture)
        {
            AddTool(firstRow, MacEditorTool.Rectangle, "□⌄", "矩形 / 椭圆", "Shape");
            AddTool(firstRow, MacEditorTool.Arrow, "→⌄", "实心 / 空心箭头", "Arrow");
            AddTool(firstRow, MacEditorTool.Brush, "✎", "画笔", "Brush");
            AddTool(firstRow, MacEditorTool.Text, "T", "文字", "Text");
            AddTool(firstRow, MacEditorTool.Emoji, "😊", "表情", "Emoji");
            AddTool(firstRow, MacEditorTool.Number, "①", "序号", "Number");
            AddTool(firstRow, MacEditorTool.Mosaic, "▦", "马赛克", "Mosaic");
            firstRow.Children.Add(CreateSeparator());
            AddAction(firstRow, "●", "录制当前区域", MacCaptureAction.VideoRecording, "VideoRecording");
            AddAction(firstRow, "▣", "保存 PNG", MacCaptureAction.Save, "Save");
            AddAction(firstRow, "↕", "使用当前选区开始长截图", MacCaptureAction.ScrollCapture, "ScrollCapture");
            AddAction(firstRow, "文", "识别文字", MacCaptureAction.RecognizeText, "RecognizeText");
            AddAction(firstRow, "取", "识别并复制文字", MacCaptureAction.CopyRecognizedText, "CopyRecognizedText");
            AddAction(firstRow, "译", "识别并翻译", MacCaptureAction.Translation, "Translation");
            AddAction(firstRow, "隐", "检测隐私信息并确认打码", MacCaptureAction.PrivacyRedaction, "PrivacyRedaction");
            AddAction(firstRow, "⌖", "钉在桌面", MacCaptureAction.PinImage, "PinImage");
            AddAction(firstRow, "码", "识别二维码/条码", MacCaptureAction.QrRecognition, "QrRecognition");
            firstRow.Children.Add(CreateSeparator());
            firstRow.Children.Add(_undoButton);
            firstRow.Children.Add(_redoButton);
            RegisterFeature("UndoRedo", _undoButton);
            RegisterFeature("UndoRedo", _redoButton);
            firstRow.Children.Add(CreateSeparator());
        }
        firstRow.Children.Add(retry);
        firstRow.Children.Add(cancel);
        firstRow.Children.Add(confirm);
        ApplyToolbarOrder(firstRow);

        var toolbarRows = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Spacing = 5,
            Children = { firstRow },
        };
        if (!scrollCapture)
        {
            if (settings.ToolbarRows == 2)
            {
                toolbarRows.Children.Add(SplitToolbarRow(firstRow));
            }
            toolbarRows.Children.Add(CreateStyleRow());
            ApplyFeatureVisibility();
        }

        _selectionToolbar = new Border
        {
            Padding = new Thickness(6),
            Background = new SolidColorBrush(Color.FromArgb(
                242,
                MacTheme.PanelBackground.R,
                MacTheme.PanelBackground.G,
                MacTheme.PanelBackground.B)),
            BorderBrush = new SolidColorBrush(MacTheme.Border),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            IsVisible = false,
            Child = toolbarRows,
        };
        _selectionToolbar.PointerPressed += OnToolbarPointerPressed;
        _selectionToolbar.PointerMoved += OnToolbarPointerMoved;
        _selectionToolbar.PointerReleased += OnToolbarPointerReleased;
        retry.Click += (_, _) =>
        {
            _selectionToolbar.IsVisible = false;
            _selectionCanvas.ClearSelection();
        };
        cancel.Click += (_, _) => _completion.TrySetResult(null);
        confirm.Click += (_, _) => ConfirmSelection(_defaultAction);
        UpdateUndoRedo();
        Content = new Grid
        {
            Children =
            {
                _selectionCanvas,
                hint,
                _colorBadge,
                _sizeBadge,
                _selectionToolbar,
            },
        };
        KeyDown += HandleKeyDown;
        Closed += (_, _) =>
        {
            _colorPicker?.CancelAndClose();
            _completion.TrySetResult(null);
        };
        Opened += (_, _) => MacNativeUi.ExcludeFromScreenCapture(this);
    }

    public MacDisplay Display { get; }

    public Size SelectionSurfaceSize { get; private set; }

    public async Task<MacCaptureSelection?> SelectAsync()
    {
        Show();
        Activate();
        var result = await _completion.Task;
        SelectionSurfaceSize = ClientSize;
        Hide();
        Close();
        return result;
    }

    private void HandleKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            _completion.TrySetResult(null);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter && _selectionCanvas.HasSelection)
        {
            ConfirmSelection(_defaultAction);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.C && !_selectionCanvas.HasSelection)
        {
            var value = ToHex(_sampledColor);
            _colorText.Text = MacNativeUi.CopyText(value)
                ? $"{value}  已复制"
                : $"{value}  复制失败";
            e.Handled = true;
        }

        if (e.Key is Key.Delete or Key.Back)
        {
            _selectionCanvas.DeleteSelectedAnnotation();
            e.Handled = true;
        }
    }

    private void UpdateSelectionSize(Rect selection)
    {
        var surfaceSize = _selectionCanvas.Bounds.Size;
        if (selection.Width < 1 || selection.Height < 1 ||
            surfaceSize.Width <= 0 || surfaceSize.Height <= 0)
        {
            _sizeBadge.IsVisible = false;
            _selectionToolbar.IsVisible = false;
            _colorBadge.IsVisible = true;
            return;
        }

        var pixels = SelectionGeometry.ToPixelSize(
            selection,
            surfaceSize,
            Display.PixelWidth,
            Display.PixelHeight);
        _sizeText.Text = $"{pixels.Width} × {pixels.Height}";
        _sizeBadge.Margin = new Thickness(
            Math.Clamp(selection.Left, 8, Math.Max(8, ClientSize.Width - 130)),
            Math.Clamp(selection.Top - 34, 8, Math.Max(8, ClientSize.Height - 34)),
            0,
            0);
        _sizeBadge.IsVisible = true;
        _colorBadge.IsVisible = false;
        PositionToolbar(selection);
    }

    private void UpdateColorSample(Color color, Point position)
    {
        _sampledColor = color;
        _colorSwatch.Background = new SolidColorBrush(color);
        _colorText.Text = $"{ToHex(color)}  C 复制";
        _colorBadge.Margin = new Thickness(
            Math.Clamp(position.X + 18, 8, Math.Max(8, ClientSize.Width - 142)),
            Math.Clamp(position.Y + 18, 8, Math.Max(8, ClientSize.Height - 48)),
            0,
            0);
        _colorBadge.IsVisible = true;
    }

    private void UpdateWindowSnap(Point position)
    {
        if (ClientSize.Width <= 0 || ClientSize.Height <= 0)
        {
            return;
        }

        var global = new CGPoint
        {
            X = Display.Bounds.Left +
                (position.X * Display.Bounds.Size.Width / ClientSize.Width),
            Y = Display.Bounds.Top +
                (position.Y * Display.Bounds.Size.Height / ClientSize.Height),
        };
        var window = MacWindowSnapService.FindWindowAt(global);
        if (window is null)
        {
            _selectionCanvas.SetSuggestedSelection(default);
            return;
        }

        var bounds = window.Value;
        _selectionCanvas.SetSuggestedSelection(new Rect(
            (bounds.Left - Display.Bounds.Left) *
                ClientSize.Width / Display.Bounds.Size.Width,
            (bounds.Top - Display.Bounds.Top) *
                ClientSize.Height / Display.Bounds.Size.Height,
            bounds.Size.Width * ClientSize.Width / Display.Bounds.Size.Width,
            bounds.Size.Height * ClientSize.Height / Display.Bounds.Size.Height));
    }

    private static string ToHex(Color color) =>
        $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    private void ShowSelectionTools(Rect selection)
    {
        _selectionToolbar.IsVisible = true;
        PositionToolbar(selection);
    }

    private void PositionToolbar(Rect selection)
    {
        if (!_selectionToolbar.IsVisible)
        {
            return;
        }

        double toolbarWidth = _scrollCapture
            ? 160
            : _settings.ToolbarRows == 1 ? 1060 : 620;
        toolbarWidth = Math.Min(toolbarWidth, Math.Max(160, ClientSize.Width - 16));
        var toolbarHeight = _scrollCapture ? 46 : _settings.ToolbarRows == 1 ? 82 : 122;
        var left = _settings.ToolbarPositionXRatio is >= 0 and <= 1
            ? _settings.ToolbarPositionXRatio *
                Math.Max(0, ClientSize.Width - toolbarWidth)
            : Math.Clamp(
                selection.Right - toolbarWidth,
                8,
                Math.Max(8, ClientSize.Width - toolbarWidth - 8));
        var top = _settings.ToolbarPositionYRatio is >= 0 and <= 1
            ? _settings.ToolbarPositionYRatio *
                Math.Max(0, ClientSize.Height - toolbarHeight)
            : selection.Bottom + 8;
        if (top + toolbarHeight > ClientSize.Height - 8)
        {
            top = Math.Max(8, selection.Top - toolbarHeight - 8);
        }

        _selectionToolbar.Margin = new Thickness(left, top, 0, 0);
    }

    private void OnToolbarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var isGrip = e.Source is Control { Tag: "ToolbarDragGrip" };
        if ((!ReferenceEquals(e.Source, _selectionToolbar) && !isGrip) ||
            !e.GetCurrentPoint(_selectionToolbar).Properties.IsLeftButtonPressed)
        {
            return;
        }

        _toolbarDragging = true;
        _toolbarDragStart = e.GetPosition(this);
        _toolbarMarginStart = _selectionToolbar.Margin;
        e.Pointer.Capture(_selectionToolbar);
        e.Handled = true;
    }

    private void OnToolbarPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_toolbarDragging ||
            !e.GetCurrentPoint(_selectionToolbar).Properties.IsLeftButtonPressed)
        {
            return;
        }

        var point = e.GetPosition(this);
        var x = Math.Clamp(
            _toolbarMarginStart.Left + point.X - _toolbarDragStart.X,
            8,
            Math.Max(8, ClientSize.Width - _selectionToolbar.Bounds.Width - 8));
        var y = Math.Clamp(
            _toolbarMarginStart.Top + point.Y - _toolbarDragStart.Y,
            8,
            Math.Max(8, ClientSize.Height - _selectionToolbar.Bounds.Height - 8));
        _selectionToolbar.Margin = new Thickness(x, y, 0, 0);
        _settings.ToolbarPositionXRatio = ClientSize.Width <= _selectionToolbar.Bounds.Width
            ? 0
            : x / (ClientSize.Width - _selectionToolbar.Bounds.Width);
        _settings.ToolbarPositionYRatio = ClientSize.Height <= _selectionToolbar.Bounds.Height
            ? 0
            : y / (ClientSize.Height - _selectionToolbar.Bounds.Height);
        e.Handled = true;
    }

    private void OnToolbarPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_toolbarDragging)
        {
            return;
        }

        _toolbarDragging = false;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    private void ConfirmSelection(
        MacCaptureAction action = MacCaptureAction.Complete)
    {
        var selection = _selectionCanvas.Selection;
        if (selection.Width >= 6 && selection.Height >= 6)
        {
            _completion.TrySetResult(new MacCaptureSelection(
                selection,
                _selectionCanvas.Annotations.ToArray(),
                action));
        }
    }

    private void AddTool(
        StackPanel panel,
        MacEditorTool tool,
        string icon,
        string tooltip,
        string feature)
    {
        var button = CreateToolbarButton(icon, tooltip);
        button.Tag = feature;
        button.Click += (_, _) => SelectTool(tool);
        if (tool == MacEditorTool.Arrow)
        {
            button.ContextMenu = CreateMenu(
                ("实心箭头", MacArrowStyle.Filled),
                ("空心箭头", MacArrowStyle.Hollow));
        }
        else if (tool is MacEditorTool.Rectangle or MacEditorTool.Ellipse)
        {
            button.ContextMenu = CreateMenu(
                ("矩形", MacEditorTool.Rectangle),
                ("椭圆", MacEditorTool.Ellipse));
        }
        else if (tool == MacEditorTool.Emoji)
        {
            button.ContextMenu = CreateEmojiMenu();
        }
        _toolButtons.Add(tool, button);
        RegisterFeature(feature, button);
        panel.Children.Add(button);
    }

    private void SelectTool(MacEditorTool tool)
    {
        SelectTool(tool, allowToggle: true);
    }

    private void SelectTool(MacEditorTool tool, bool allowToggle)
    {
        MacEditorTool? next = allowToggle && _selectionCanvas.ActiveTool == tool
            ? null
            : tool;
        _selectionCanvas.SelectTool(next);
        foreach (var (candidate, button) in _toolButtons)
        {
            var selected = candidate == next ||
                candidate == MacEditorTool.Rectangle && next == MacEditorTool.Ellipse;
            button.Background = selected
                ? new SolidColorBrush(Color.Parse("#3D5F8DFF"))
                : Brushes.Transparent;
            button.BorderBrush = selected
                ? MacTheme.AccentBrush
                : Brushes.Transparent;
            button.BorderThickness = selected
                ? new Thickness(1)
                : new Thickness(0);
        }

        if (next == MacEditorTool.Text)
        {
            _textInput?.Focus();
            _textInput?.SelectAll();
        }
    }

    private ContextMenu CreateMenu<T>(params (string Label, T Value)[] choices)
    {
        var menu = new ContextMenu();
        foreach (var (label, value) in choices)
        {
            var item = new MenuItem { Header = label };
            item.Click += (_, _) =>
            {
                switch (value)
                {
                    case MacArrowStyle arrow:
                        _selectionCanvas.ArrowStyle = arrow;
                        _settings.ArrowStyle = arrow == MacArrowStyle.Hollow
                            ? "Hollow"
                            : "Filled";
                        if (_toolButtons.TryGetValue(MacEditorTool.Arrow, out var arrowButton))
                        {
                            arrowButton.Content = arrow == MacArrowStyle.Hollow
                                ? "⇢⌄"
                                : "➜⌄";
                        }
                        SelectTool(MacEditorTool.Arrow, allowToggle: false);
                        break;
                    case MacEditorTool shape when shape is MacEditorTool.Rectangle or MacEditorTool.Ellipse:
                        if (_toolButtons.TryGetValue(MacEditorTool.Rectangle, out var shapeButton))
                        {
                            shapeButton.Content = shape == MacEditorTool.Ellipse
                                ? "○⌄"
                                : "□⌄";
                        }
                        SelectTool(shape, allowToggle: false);
                        break;
                }
            };
            menu.Items.Add(item);
        }

        return menu;
    }

    private ContextMenu CreateEmojiMenu()
    {
        var menu = new ContextMenu();
        foreach (var emoji in new[] { "😊", "😀", "😂", "😍", "👍", "🔥", "✅", "❌" })
        {
            var item = new MenuItem { Header = emoji, FontSize = 18 };
            item.Click += (_, _) =>
            {
                _selectionCanvas.EmojiValue = emoji;
                if (_emojiInput is not null)
                {
                    _emojiInput.Text = emoji;
                }
                SelectTool(MacEditorTool.Emoji, allowToggle: false);
            };
            menu.Items.Add(item);
        }

        return menu;
    }

    private StackPanel CreateStyleRow()
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var colors = new[]
        {
            "#FF3B30", "#FFCC00", "#34C759", "#0A84FF", "#AF52DE", "#FFFFFF",
        };
        foreach (var value in colors)
        {
            var color = Color.Parse(value);
            var colorButton = new Button
            {
                Width = 24,
                Height = 24,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(12),
                Background = new SolidColorBrush(color),
                BorderBrush = value == "#FFFFFF"
                    ? new SolidColorBrush(Color.Parse("#708090"))
                    : Brushes.Transparent,
                BorderThickness = new Thickness(1),
            };
            colorButton.Click += (_, _) =>
            {
                _selectionCanvas.AnnotationColor = color;
                _settings.AnnotationColor = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
            };
            ToolTip.SetTip(colorButton, value);
            row.Children.Add(colorButton);
        }

        var customColor = CreateToolbarButton("◉", "打开调色盘");
        customColor.Width = 30;
        customColor.Height = 30;
        customColor.Click += async (_, _) =>
        {
            _colorPicker = new MacColorPickerWindow(
                _selectionCanvas.AnnotationColor,
                _settings);
            var selected = await _colorPicker.ShowAsync(this);
            _colorPicker = null;
            if (selected is not { } color)
            {
                return;
            }

            _selectionCanvas.AnnotationColor = color;
            _settings.AnnotationColor = color.A == byte.MaxValue
                ? $"#{color.R:X2}{color.G:X2}{color.B:X2}"
                : $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";
            customColor.Foreground = new SolidColorBrush(color);
        };
        row.Children.Add(customColor);

        row.Children.Add(CreateSeparator());
        row.Children.Add(new TextBlock
        {
            Text = "粗细",
            Foreground = new SolidColorBrush(Color.Parse("#D8DEE9")),
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 12,
        });
        var widthSlider = new Slider
        {
            Minimum = 1,
            Maximum = 10,
            Value = _selectionCanvas.AnnotationWidth,
            Width = 88,
            VerticalAlignment = VerticalAlignment.Center,
        };
        widthSlider.PropertyChanged += (_, args) =>
        {
            if (args.Property == Slider.ValueProperty)
            {
                _selectionCanvas.AnnotationWidth = widthSlider.Value;
                _settings.AnnotationWidth = widthSlider.Value;
            }
        };
        row.Children.Add(widthSlider);

        _textInput = new TextBox
        {
            Width = 100,
            Height = 27,
            Watermark = "文字内容",
            Text = string.Empty,
            FontSize = 12,
            Padding = new Thickness(7, 3),
        };
        _textInput.TextChanged += (_, _) =>
            _selectionCanvas.TextValue = _textInput.Text ?? string.Empty;
        row.Children.Add(_textInput);
        _emojiInput = new TextBox
        {
            Width = 48,
            Height = 27,
            Watermark = "表情",
            Text = _selectionCanvas.EmojiValue,
            FontSize = 12,
            Padding = new Thickness(7, 3),
        };
        _emojiInput.TextChanged += (_, _) =>
            _selectionCanvas.EmojiValue = _emojiInput.Text ?? string.Empty;
        row.Children.Add(_emojiInput);
        return row;
    }

    private static StackPanel SplitToolbarRow(StackPanel firstRow)
    {
        var secondRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = firstRow.Spacing,
        };
        var buttonCount = firstRow.Children.OfType<Button>().Count();
        var target = Math.Max(1, (buttonCount + 1) / 2);
        var seen = 0;
        var split = firstRow.Children.Count;
        for (var index = 0; index < firstRow.Children.Count; index++)
        {
            if (firstRow.Children[index] is Button)
            {
                seen++;
            }

            if (seen >= target)
            {
                split = index + 1;
                break;
            }
        }

        while (firstRow.Children.Count > split)
        {
            var control = firstRow.Children[split];
            firstRow.Children.RemoveAt(split);
            secondRow.Children.Add(control);
        }

        return secondRow;
    }

    private void ApplyToolbarOrder(StackPanel row)
    {
        var order = _settings.ToolbarFeatureOrder;
        if (order is null || order.Length == 0)
        {
            return;
        }

        var rank = order.Select((feature, index) => (feature, index))
            .ToDictionary(item => item.feature, item => item.index, StringComparer.Ordinal);
        var controls = row.Children
            .OfType<Control>()
            .Where(control => control.Tag is string)
            .OrderBy(control => rank.TryGetValue((string)control.Tag!, out var index) ? index : int.MaxValue)
            .ToArray();
        var slots = row.Children
            .Select((control, index) => (control, index))
            .Where(item => item.control.Tag is string)
            .Select(item => item.index)
            .ToArray();
        // Detach first; assigning an Avalonia control that still has a visual
        // parent raises an exception instead of moving it.
        foreach (var index in slots.Reverse())
        {
            row.Children.RemoveAt(index);
        }

        for (var index = 0; index < slots.Length; index++)
        {
            row.Children.Insert(slots[index], controls[index]);
        }
    }

    private void AddAction(
        StackPanel panel,
        string label,
        string tooltip,
        MacCaptureAction action,
        string feature)
    {
        var button = CreateToolbarButton(label, tooltip);
        button.Tag = feature;
        button.Width = label.Length > 3 ? 50 : 44;
        button.FontSize = 12;
        button.Click += (_, _) => ConfirmSelection(action);
        RegisterFeature(feature, button);
        panel.Children.Add(button);
    }

    private void RegisterFeature(string feature, Control control)
    {
        if (!_featureControls.TryGetValue(feature, out var controls))
        {
            controls = [];
            _featureControls.Add(feature, controls);
        }

        controls.Add(control);
    }

    private void ApplyFeatureVisibility()
    {
        var visible = _settings.VisibleToolbarFeatures.ToHashSet(StringComparer.Ordinal);
        foreach (var (feature, controls) in _featureControls)
        {
            foreach (var control in controls)
            {
                control.IsVisible = visible.Contains(feature);
            }
        }
    }

    private void UpdateUndoRedo()
    {
        _undoButton.IsEnabled = _selectionCanvas.CanUndo;
        _redoButton.IsEnabled = _selectionCanvas.CanRedo;
    }

    private void LoadSelectedAnnotation(MacAnnotation? annotation)
    {
        switch (annotation)
        {
            case MacTextAnnotation { Tool: MacEditorTool.Text } text:
                if (_textInput is not null && _textInput.Text != text.Text)
                {
                    _textInput.Text = text.Text;
                    _textInput.Focus();
                    _textInput.SelectAll();
                }
                break;
            case MacTextAnnotation { Tool: MacEditorTool.Emoji } emoji:
                if (_emojiInput is not null && _emojiInput.Text != emoji.Text)
                {
                    _emojiInput.Text = emoji.Text;
                }
                break;
        }
    }

    private static Border CreateToolbarGrip()
    {
        var grip = new TextBlock
        {
            Tag = "ToolbarDragGrip",
            Text = "⠿",
            Width = 20,
            Height = 34,
            FontSize = 17,
            Foreground = new SolidColorBrush(Color.Parse("#9AA8B8")),
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            Cursor = new Cursor(StandardCursorType.SizeAll),
        };
        ToolTip.SetTip(grip, "拖动工具栏");
        return new Border
        {
            Width = 24,
            Height = 34,
            Child = grip,
        };
    }

    private static Border CreateSeparator() => new()
    {
        Width = 1,
        Height = 24,
        Margin = new Thickness(3, 5),
        Background = new SolidColorBrush(Color.Parse("#52627486")),
    };

    private static Button CreateToolbarButton(string icon, string tooltip)
    {
        var button = new Button
        {
            Content = icon,
            Width = 42,
            Height = 34,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(Color.Parse("#243247")),
            BorderBrush = new SolidColorBrush(Color.Parse("#48607B")),
            BorderThickness = new Thickness(1),
            Foreground = new SolidColorBrush(Color.Parse("#F2F5F9")),
            FontSize = 17,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        ToolTip.SetTip(button, tooltip);
        return button;
    }
}
