using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfColor = System.Windows.Media.Color;

namespace Screenshot.App.Capture;

public partial class PresetCaptureExecuteWindow : Window
{
    public sealed record Result(ScreenRegion? Region, int? EditIndex, bool ClearAll = false);

    private readonly ScreenRegion _virtualBounds;
    private readonly IReadOnlyList<ScreenRegion> _regions;
    private readonly TaskCompletionSource<Result?> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private PresetCaptureExecuteWindow(IReadOnlyList<ScreenRegion> regions)
    {
        InitializeComponent();
        _regions = regions;
        _virtualBounds = VirtualScreen.GetBounds();
        Left = _virtualBounds.X;
        Top = _virtualBounds.Y;
        Width = _virtualBounds.Width;
        Height = _virtualBounds.Height;
        Loaded += (_, _) =>
        {
            RenderRegions();
            Focus();
            Keyboard.Focus(this);
        };
    }

    public static Task<Result?> ShowAsync(IReadOnlyList<ScreenRegion> regions)
    {
        var window = new PresetCaptureExecuteWindow(regions);
        window.Show();
        return window._completion.Task;
    }

    private void RenderRegions()
    {
        RegionCanvas.Children.Clear();
        PresetButtons.Children.Clear();
        var scale = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice.M11 ?? 1;
        for (var index = 0; index < 5; index++)
        {
            var buttonIndex = index;
            var region = index < _regions.Count ? _regions[index] : (ScreenRegion?)null;
            var button = new System.Windows.Controls.Button
            {
                Content = region is null
                    ? "+"
                    : (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture),
                Width = 42,
                Height = 34,
                Margin = new Thickness(1),
                Tag = buttonIndex,
                FontSize = 16,
                ToolTip = region is null
                    ? "未设置预设区域"
                    : $"左键截图；右键编辑\n{region.Value.Width} × {region.Value.Height}，范围 ({region.Value.X}, {region.Value.Y})",
            };
            button.MouseEnter += (_, _) => SetRegionHighlight(buttonIndex, true);
            button.MouseLeave += (_, _) => SetRegionHighlight(buttonIndex, false);
            button.Click += OnPresetButtonClick;
            button.MouseRightButtonDown += OnPresetButtonRightClick;
            PresetButtons.Children.Add(button);

            if (region is not { } actualRegion)
            {
                continue;
            }

            var border = new Border
            {
                Width = actualRegion.Width * scale,
                Height = actualRegion.Height * scale,
                BorderBrush = GetThemeBrush("AppAccentBrush", WpfBrushes.DeepSkyBlue),
                BorderThickness = new Thickness(2),
                Background = WpfBrushes.Transparent,
                Tag = new RegionTag(index, actualRegion),
                Visibility = Visibility.Collapsed,
                IsHitTestVisible = false,
                Child = new TextBlock
                {
                    Text = $"{index + 1}\n{actualRegion.Width} × {actualRegion.Height}\n({actualRegion.X}, {actualRegion.Y})",
                    Foreground = WpfBrushes.White,
                    FontSize = 14,
                    FontWeight = FontWeights.SemiBold,
                    Background = WpfBrushes.Transparent,
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                    VerticalAlignment = System.Windows.VerticalAlignment.Top,
                    Padding = new Thickness(7),
                },
            };
            Canvas.SetLeft(border, (actualRegion.X - _virtualBounds.X) * scale);
            Canvas.SetTop(border, (actualRegion.Y - _virtualBounds.Y) * scale);
            RegionCanvas.Children.Add(border);
        }
    }

    private readonly record struct RegionTag(int Index, ScreenRegion Region);

    private void SetRegionHighlight(int index, bool highlighted)
    {
        foreach (var border in RegionCanvas.Children.OfType<Border>())
        {
            if (border.Tag is RegionTag tag && tag.Index == index)
            {
                var accent = GetThemeBrush("AppAccentBrush", WpfBrushes.DeepSkyBlue);
                border.BorderBrush = highlighted
                    ? GetThemeBrush("AppAccentMutedBrush", WpfBrushes.White)
                    : accent;
                border.Background = WpfBrushes.Transparent;
                border.Visibility = highlighted ? Visibility.Visible : Visibility.Collapsed;
                if (highlighted && tag.Region is { } region)
                {
                    RangeText.Text = $"区域 {index + 1}：{region.Width} × {region.Height}，范围 ({region.X}, {region.Y})";
                    RangeText.Visibility = Visibility.Visible;
                }
                else if (!highlighted)
                {
                    RangeText.Visibility = Visibility.Collapsed;
                }
            }
        }
    }

    private void OnPresetButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { Tag: int index } && index < _regions.Count)
        {
            _completion.TrySetResult(new Result(_regions[index], null));
        }
        else
        {
            _completion.TrySetResult(new Result(null, -1));
        }
        Close();
    }

    private void OnPresetButtonRightClick(object sender, MouseButtonEventArgs e)
    {
        var index = sender is System.Windows.Controls.Button { Tag: int value } ? value : -1;
        _completion.TrySetResult(new Result(null, index < _regions.Count ? index : -1));
        Close();
        e.Handled = true;
    }

    private void OnClearAllClick(object sender, RoutedEventArgs e)
    {
        _completion.TrySetResult(new Result(null, null, true));
        Close();
        e.Handled = true;
    }

    private void OnWindowPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;
        if (FindVisualParent<System.Windows.Controls.Button>(source) is not null)
        {
            return;
        }

        if (FindVisualParent<Border>(source) is { } border &&
            (ReferenceEquals(border, PresetPanel) || border.Tag is RegionTag))
        {
            return;
        }

        // Any mouse click outside the floating panel intentionally dismisses
        // the preset chooser. The panel and its buttons are filtered above.
        _completion.TrySetResult(null);
        Close();
        e.Handled = true;
    }

    protected override void OnClosed(EventArgs e)
    {
        _completion.TrySetResult(null);
        base.OnClosed(e);
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
}
