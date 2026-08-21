using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using SnapCut.Mac.Editor;
using SnapCut.Mac.Native;

namespace SnapCut.Mac.Presentation;

internal sealed class PinnedImageEditorToolbarWindow : Window
{
    private readonly TaskCompletionSource<bool> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly SelectionCanvas _canvas;
    private readonly Window _owner;
    private readonly Dictionary<MacEditorTool, Button> _tools = [];

    public PinnedImageEditorToolbarWindow(SelectionCanvas canvas, Window owner)
    {
        _canvas = canvas;
        _owner = owner;
        Title = "SnapCut 钉图编辑工具栏";
        Width = 710;
        Height = 48;
        SystemDecorations = SystemDecorations.None;
        CanResize = false;
        Topmost = true;
        ShowInTaskbar = false;
        Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
        };
        AddTool(row, MacEditorTool.Rectangle, "□", "矩形");
        AddTool(row, MacEditorTool.Ellipse, "○", "椭圆");
        AddTool(row, MacEditorTool.Arrow, "↗", "箭头");
        AddTool(row, MacEditorTool.Brush, "✎", "画笔");
        AddTool(row, MacEditorTool.Text, "T", "文字");
        AddTool(row, MacEditorTool.Emoji, "☺", "表情");
        AddTool(row, MacEditorTool.Number, "①", "序号");
        AddTool(row, MacEditorTool.Mosaic, "▦", "马赛克");
        row.Children.Add(Button("↶", "撤销", () => _canvas.Undo()));
        row.Children.Add(Button("↷", "重做", () => _canvas.Redo()));
        var color = Button("●", "切换颜色", CycleColor);
        color.Foreground = new SolidColorBrush(_canvas.AnnotationColor);
        row.Children.Add(color);
        var width = new Slider
        {
            Minimum = 1,
            Maximum = 10,
            Value = _canvas.AnnotationWidth,
            Width = 70,
            VerticalAlignment = VerticalAlignment.Center,
        };
        width.PropertyChanged += (_, args) =>
        {
            if (args.Property == Slider.ValueProperty)
            {
                _canvas.AnnotationWidth = width.Value;
            }
        };
        row.Children.Add(width);
        var text = new TextBox
        {
            Width = 82,
            Height = 30,
            Text = _canvas.TextValue,
            Watermark = "文字/表情",
            Padding = new Thickness(6, 2),
        };
        text.TextChanged += (_, _) =>
        {
            _canvas.TextValue = text.Text ?? string.Empty;
            _canvas.EmojiValue = text.Text ?? string.Empty;
        };
        row.Children.Add(text);
        row.Children.Add(Button("×", "取消", () => _completion.TrySetResult(false)));
        var apply = Button("✓", "应用编辑", () => _completion.TrySetResult(true));
        apply.Background = MacTheme.AccentBrush;
        row.Children.Add(apply);
        var surface = new Border
        {
            Padding = new Thickness(6),
            Background = new SolidColorBrush(Color.Parse("#F0212A38")),
            BorderBrush = new SolidColorBrush(Color.Parse("#64748799")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            Child = row,
        };
        surface.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed &&
                e.Source == surface)
            {
                BeginMoveDrag(e);
                e.Handled = true;
            }
        };
        Content = surface;
        Opened += (_, _) =>
        {
            MacNativeUi.ExcludeFromScreenCapture(this);
            SnapToOwner();
        };
        _owner.PositionChanged += HandleOwnerMoved;
        Closed += (_, _) =>
        {
            _owner.PositionChanged -= HandleOwnerMoved;
            _completion.TrySetResult(false);
        };
    }

    public async Task<bool> WaitAsync()
    {
        Show();
        Activate();
        var result = await _completion.Task;
        Close();
        return result;
    }

    private void AddTool(
        StackPanel row,
        MacEditorTool tool,
        string icon,
        string tooltip)
    {
        var button = Button(icon, tooltip, () => SelectTool(tool));
        _tools.Add(tool, button);
        row.Children.Add(button);
    }

    private void SelectTool(MacEditorTool tool)
    {
        MacEditorTool? selected = _canvas.ActiveTool == tool ? null : tool;
        _canvas.SelectTool(selected);
        foreach (var (candidate, button) in _tools)
        {
            button.Background = candidate == selected
                ? new SolidColorBrush(Color.Parse("#3D5F8DFF"))
                : Brushes.Transparent;
        }
    }

    private void CycleColor()
    {
        var colors = new[]
        {
            Color.Parse("#FF3B30"), Color.Parse("#FFCC00"),
            Color.Parse("#34C759"), Color.Parse("#0A84FF"),
            Color.Parse("#AF52DE"), Colors.White,
        };
        var current = Array.IndexOf(colors, _canvas.AnnotationColor);
        _canvas.AnnotationColor = colors[(current + 1 + colors.Length) % colors.Length];
    }

    private void HandleOwnerMoved(object? sender, PixelPointEventArgs e) => SnapToOwner();

    private void SnapToOwner()
    {
        var right = _owner.Position.X + (int)Math.Ceiling(_owner.Width) + 3;
        var x = right + Width <= (Screens.Primary?.Bounds.Right ?? right + Width)
            ? right
            : _owner.Position.X - (int)Math.Ceiling(Width) - 3;
        Position = new PixelPoint(Math.Max(0, x), _owner.Position.Y);
    }

    private static Button Button(string icon, string tooltip, Action action)
    {
        var button = new Button
        {
            Content = icon,
            Width = 36,
            Height = 34,
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = Brushes.White,
            FontSize = 14,
        };
        button.Click += (_, _) => action();
        ToolTip.SetTip(button, tooltip);
        return button;
    }
}
