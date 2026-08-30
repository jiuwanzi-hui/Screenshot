using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfColor = System.Windows.Media.Color;

namespace Screenshot.App.Capture;

public partial class PresetCaptureWindow : Window
{
    private const int MaximumRegions = 5;
    private readonly ScreenRegion _virtualBounds = VirtualScreen.GetBounds();
    private readonly List<ScreenRegion> _regions = [];
    private readonly TaskCompletionSource<IReadOnlyList<ScreenRegion>?> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private System.Windows.Point _start;
    private ScreenRegion _dragStartRegion;
    private int _dragRegionIndex = -1;
    private DragMode _dragMode;
    private bool _dragging;
    private System.Windows.Shapes.Rectangle? _activeRectangle;
    private int _selectedIndex = -1;

    private PresetCaptureWindow(IReadOnlyList<ScreenRegion> initialRegions)
    {
        InitializeComponent();
        _regions.AddRange(initialRegions.Take(MaximumRegions));
        Left = _virtualBounds.X; Top = _virtualBounds.Y;
        Width = _virtualBounds.Width; Height = _virtualBounds.Height;
        Loaded += (_, _) =>
        {
            RenderRegions();
            Activate(); Focus(); Keyboard.Focus(this);
        };
    }

    public static Task<IReadOnlyList<ScreenRegion>?> ShowAsync(
        IReadOnlyList<ScreenRegion> initialRegions, int selectedIndex = -1)
    {
        var window = new PresetCaptureWindow(initialRegions)
        {
            _selectedIndex = selectedIndex,
        };
        window.Show();
        return window._completion.Task;
    }

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (FindVisualParent<System.Windows.Controls.Button>(e.OriginalSource as DependencyObject) is not null)
        {
            return;
        }

        var point = e.GetPosition(Root);
        Activate();
        Focus();
        var hit = FindRegionAt(point);
        if (hit >= 0)
        {
            _selectedIndex = hit;
            _dragRegionIndex = hit; _dragStartRegion = _regions[hit]; _start = point;
            _dragMode = IsResizeHandle(point, _dragStartRegion) ? DragMode.Resize : DragMode.Move;
            _dragging = true; Root.CaptureMouse(); RenderRegions(); e.Handled = true; return;
        }
        if (_regions.Count >= MaximumRegions) return;
        _start = point;
        _activeRectangle = new System.Windows.Shapes.Rectangle
        {
            Stroke = GetThemeBrush("AppAccentBrush", WpfBrushes.DeepSkyBlue),
            StrokeThickness = 2,
            Fill = WpfBrushes.Transparent,
        };
        OverlayCanvas.Children.Add(_activeRectangle);
        _dragMode = DragMode.Create; _dragging = true; Root.CaptureMouse(); e.Handled = true;
    }

    private void OnMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        var point = e.GetPosition(Root);
        if (!_dragging)
        {
            var hit = FindRegionAt(point);
            Cursor = hit >= 0
                ? (IsResizeHandle(point, _regions[hit])
                    ? System.Windows.Input.Cursors.SizeNWSE
                    : System.Windows.Input.Cursors.SizeAll)
                : System.Windows.Input.Cursors.Cross;
            return;
        }

        if (_dragMode is DragMode.Move or DragMode.Resize)
        {
            var scale = GetDeviceScale();
            var dx = (int)Math.Round((point.X - _start.X) * scale);
            var dy = (int)Math.Round((point.Y - _start.Y) * scale);
            var next = _dragStartRegion;
            if (_dragMode == DragMode.Move) next = next with { X = next.X + dx, Y = next.Y + dy };
            else next = next with { Width = Math.Max(2, next.Width + dx), Height = Math.Max(2, next.Height + dy) };
            _regions[_dragRegionIndex] = ScreenRegion.Intersect(next, _virtualBounds);
            RenderRegions(); return;
        }
        if (_activeRectangle is null) return;
        Canvas.SetLeft(_activeRectangle, Math.Min(_start.X, point.X));
        Canvas.SetTop(_activeRectangle, Math.Min(_start.Y, point.Y));
        _activeRectangle.Width = Math.Abs(point.X - _start.X);
        _activeRectangle.Height = Math.Abs(point.Y - _start.Y);
    }

    private void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        var point = e.GetPosition(Root); Root.ReleaseMouseCapture(); _dragging = false;
        if (_dragMode is DragMode.Move or DragMode.Resize)
        {
            _dragMode = DragMode.None; _dragRegionIndex = -1; RenderRegions(); e.Handled = true; return;
        }
        if (_activeRectangle is null) return;
        OverlayCanvas.Children.Remove(_activeRectangle); _activeRectangle = null;
        var scale = GetDeviceScale();
        var region = ScreenRegion.FromCorners(
            _virtualBounds.X + (int)Math.Round(Math.Min(_start.X, point.X) * scale),
            _virtualBounds.Y + (int)Math.Round(Math.Min(_start.Y, point.Y) * scale),
            _virtualBounds.X + (int)Math.Round(Math.Max(_start.X, point.X) * scale),
            _virtualBounds.Y + (int)Math.Round(Math.Max(_start.Y, point.Y) * scale));
        if (region.Width >= 2 && region.Height >= 2) _regions.Add(ScreenRegion.Intersect(region, _virtualBounds));
        RenderRegions(); e.Handled = true;
    }

    private void OnMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_dragging)
        {
            Root.ReleaseMouseCapture();
            _dragging = false;
            _dragMode = DragMode.None;
            _dragRegionIndex = -1;
            if (_activeRectangle is not null)
            {
                OverlayCanvas.Children.Remove(_activeRectangle);
                _activeRectangle = null;
            }

            RenderRegions();
            e.Handled = true;
            return;
        }

        // Right-click is the only way out of the setup layer. A configured
        // list is returned to the caller so it is persisted; an empty list
        // cancels the setup entirely.
        Complete(_regions.Count == 0 ? null : _regions);
        e.Handled = true;
    }

    private void RenderRegions()
    {
        OverlayCanvas.Children.Clear();
        var scale = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice.M11 ?? 1;
        for (var index = 0; index < _regions.Count; index++)
        {
            var region = _regions[index];
            var border = new Border
            {
                Width = region.Width * scale, Height = region.Height * scale,
                BorderBrush = index == _selectedIndex
                    ? GetThemeBrush("AppAccentMutedBrush", WpfBrushes.White)
                    : GetThemeBrush("AppAccentBrush", WpfBrushes.DeepSkyBlue),
                BorderThickness = new Thickness(2),
                Background = WpfBrushes.Transparent,
                Tag = index,
                IsHitTestVisible = false,
                Child = new TextBlock
                {
                    Text = $"{index + 1}  {region.Width} × {region.Height}\n({region.X}, {region.Y})",
                    Foreground = GetThemeBrush("AppTextPrimaryBrush", WpfBrushes.White),
                    Background = WpfBrushes.Transparent,
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                    VerticalAlignment = System.Windows.VerticalAlignment.Top,
                    Padding = new Thickness(6),
                },
            };
            Canvas.SetLeft(border, (region.X - _virtualBounds.X) * scale);
            Canvas.SetTop(border, (region.Y - _virtualBounds.Y) * scale);
            OverlayCanvas.Children.Add(border);
        }
    }

    private int FindRegionAt(System.Windows.Point point)
    {
        var scale = GetDeviceScale(); var x = _virtualBounds.X + (int)Math.Round(point.X * scale); var y = _virtualBounds.Y + (int)Math.Round(point.Y * scale);
        for (var i = _regions.Count - 1; i >= 0; i--)
        {
            if (_selectedIndex >= 0 && i != _selectedIndex)
            {
                continue;
            }

            if (_regions[i].Contains(x, y))
            {
                return i;
            }
        }

        return -1;
    }

    private bool IsResizeHandle(System.Windows.Point point, ScreenRegion region)
    {
        var scale = GetDeviceScale();
        var right = (region.X - _virtualBounds.X + region.Width) / scale;
        var bottom = (region.Y - _virtualBounds.Y + region.Height) / scale;
        return point.X >= right - 18 && point.Y >= bottom - 18;
    }

    private double GetDeviceScale() => PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice.M11 ?? 1;

    private static T? FindVisualParent<T>(DependencyObject? source)
        where T : DependencyObject
    {
        while (source is not null)
        {
            if (source is T match)
            {
                return match;
            }

            source = VisualTreeHelper.GetParent(source);
        }

        return null;
    }

    private System.Windows.Media.Brush GetThemeBrush(
        string key,
        System.Windows.Media.Brush fallback)
    {
        return TryFindResource(key) as System.Windows.Media.Brush ?? fallback;
    }

    private static System.Windows.Media.Brush WithOpacity(
        System.Windows.Media.Brush source,
        double opacity)
    {
        if (source is SolidColorBrush solid)
        {
            var color = solid.Color;
            color.A = (byte)Math.Clamp((int)Math.Round(color.A * opacity), 0, 255);
            return new SolidColorBrush(color);
        }

        return source;
    }
    private void OnDoneClick(object sender, RoutedEventArgs e) => Complete(_regions);

    private void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if (_selectedIndex >= 0 && _selectedIndex < _regions.Count)
        {
            _regions.RemoveAt(_selectedIndex);
            _selectedIndex = -1;
            RenderRegions();
        }
    }

    private void OnPreviewKeyDown(
        object sender,
        System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != Key.Delete || _selectedIndex < 0 || _selectedIndex >= _regions.Count)
        {
            return;
        }

        _regions.RemoveAt(_selectedIndex);
        _selectedIndex = -1;
        RenderRegions();
        e.Handled = true;
    }

    private void Complete(IReadOnlyList<ScreenRegion>? regions) { if (_completion.Task.IsCompleted) return; _completion.TrySetResult(regions); Close(); }
    protected override void OnClosed(EventArgs e) { if (!_completion.Task.IsCompleted) _completion.TrySetResult(null); base.OnClosed(e); }
    private enum DragMode { None, Create, Move, Resize }
}
