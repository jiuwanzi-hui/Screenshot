using System.Diagnostics;
using System.IO;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WpfButton = System.Windows.Controls.Button;
using WpfImage = System.Windows.Controls.Image;
using WpfMouseEventArgs = System.Windows.Input.MouseEventArgs;
using WpfOrientation = System.Windows.Controls.Orientation;
using WpfProgressBar = System.Windows.Controls.ProgressBar;

namespace Screenshot.App.Capture;

internal sealed class VideoPostProcessWindow : Window
{
    private readonly string _inputPath;
    private readonly Slider _startSlider = new();
    private readonly Slider _endSlider = new();
    private readonly TextBlock _rangeText = new();
    private readonly TextBlock _statusText = new();
    private readonly TextBlock _previewPositionText = new();
    private readonly TextBlock _previewStatusText = new();
    private readonly MediaElement _preview = new();
    private readonly WpfImage _previewFrame = new();
    private readonly WpfButton _previewPlayButton = new();
    private readonly WpfProgressBar _progress = new();
    private readonly StackPanel _actionPanel = new();
    private readonly ScrollViewer _timelineScrollViewer = new();
    private readonly Grid _timelineLayers = new();
    private readonly Canvas _timelineFramesCanvas = new();
    private readonly Canvas _timelineOverlayCanvas = new();
    private readonly Slider _timelineZoomSlider = new();
    private readonly TextBlock _timelineStatusText = new();
    private readonly Border _timelineLeftShade = new();
    private readonly Border _timelineRightShade = new();
    private readonly Border _timelineSelectionBorder = new();
    private readonly Border _timelinePlayhead = new();
    private readonly Thumb _timelineStartHandle = new();
    private readonly Thumb _timelineEndHandle = new();
    private readonly DispatcherTimer _previewTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(80),
    };
    private readonly DispatcherTimer _scrubTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(45),
    };
    private readonly DispatcherTimer _timelineZoomTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(180),
    };
    private readonly Stopwatch _framePlaybackClock = new();
    private TimeSpan _duration;
    private bool _updatingRange;
    private bool _mediaReady;
    private bool _isPreviewPlaying;
    private bool _isScrubbing;
    private double _pendingSeekSeconds;
    private string _pendingSeekEndpoint = "预览";
    private long _seekSerial;
    private VideoPreviewSession? _framePreviewSession;
    private bool _frameRequestInProgress;
    private bool _frameRequestPending;
    private int _pendingPreviewWidth = 960;
    private bool _closed;
    private double _timelineFrameDensity = 13;
    private double _timelineEffectivePixelsPerSecond = 64;
    private double _timelinePreviewSeconds;
    private double _framePlaybackStartSeconds;
    private bool _isExtractedFramePlayback;
    private int _timelineRenderGeneration;
    private bool _updatingTimelineZoom;

    public VideoPostProcessWindow(string inputPath)
    {
        _inputPath = inputPath;
        Title = "录屏裁剪与动图导出";
        Width = 920;
        Height = 820;
        MinWidth = 720;
        MinHeight = 620;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = true;
        SetResourceReference(BackgroundProperty, "AppBackgroundBrush");
        Content = BuildContent();
        _previewTimer.Tick += OnPreviewTimerTick;
        _scrubTimer.Tick += OnScrubTimerTick;
        _timelineZoomTimer.Tick += OnTimelineZoomTimerTick;
        _preview.MediaOpened += OnPreviewMediaOpened;
        _preview.MediaFailed += OnPreviewMediaFailed;
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private Grid BuildContent()
    {
        var root = new Grid { Margin = new Thickness(22) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var title = new TextBlock
        {
            Text = "裁掉开头和结尾，或导出动图",
            FontSize = 21,
            FontWeight = FontWeights.SemiBold,
        };
        title.SetResourceReference(TextBlock.ForegroundProperty,
            "AppTextPrimaryBrush");
        root.Children.Add(title);

        var file = new TextBlock
        {
            Text = Path.GetFileName(_inputPath),
            Margin = new Thickness(0, 7, 0, 20),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        file.SetResourceReference(TextBlock.ForegroundProperty,
            "AppTextSecondaryBrush");
        Grid.SetRow(file, 1);
        root.Children.Add(file);

        var controls = new StackPanel
        {
            Margin = new Thickness(0, 0, 8, 0),
        };
        controls.Children.Add(CreatePreviewPanel());
        controls.Children.Add(CreateTimelinePanel());
        ConfigureRangeSlider(_startSlider);
        ConfigureRangeSlider(_endSlider);
        _rangeText.FontFamily = new System.Windows.Media.FontFamily("Consolas");
        _rangeText.FontSize = 14;
        _rangeText.Margin = new Thickness(0, 0, 0, 16);
        _rangeText.SetResourceReference(TextBlock.ForegroundProperty,
            "AppTextPrimaryBrush");
        controls.Children.Add(_rangeText);
        _startSlider.ValueChanged += OnRangeChanged;
        _endSlider.ValueChanged += OnRangeChanged;
        _startSlider.PreviewMouseLeftButtonDown += OnSliderMouseLeftButtonDown;
        _endSlider.PreviewMouseLeftButtonDown += OnSliderMouseLeftButtonDown;
        _startSlider.PreviewMouseLeftButtonUp += OnSliderMouseLeftButtonUp;
        _endSlider.PreviewMouseLeftButtonUp += OnSliderMouseLeftButtonUp;
        _startSlider.LostMouseCapture += OnSliderLostMouseCapture;
        _endSlider.LostMouseCapture += OnSliderLostMouseCapture;
        _progress.Height = 5;
        _progress.Margin = new Thickness(0, 18, 0, 8);
        _progress.Visibility = Visibility.Collapsed;
        controls.Children.Add(_progress);
        _statusText.Text = "正在读取视频信息...";
        _statusText.TextWrapping = TextWrapping.Wrap;
        _statusText.SetResourceReference(TextBlock.ForegroundProperty,
            "AppTextSecondaryBrush");
        controls.Children.Add(_statusText);
        var scrollViewer = new ScrollViewer
        {
            Content = controls,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            PanningMode = PanningMode.VerticalOnly,
        };
        Grid.SetRow(scrollViewer, 2);
        root.Children.Add(scrollViewer);

        _actionPanel.Margin = new Thickness(0, 20, 0, 0);
        _actionPanel.HorizontalAlignment =
            System.Windows.HorizontalAlignment.Right;
        _actionPanel.Orientation = WpfOrientation.Horizontal;
        _actionPanel.Children.Add(CreateButton("打开文件夹", OnOpenFolderClick));
        _actionPanel.Children.Add(CreateButton("保存裁剪 MP4", OnTrimClick));
        _actionPanel.Children.Add(CreateButton("导出 GIF", OnGifClick));
        _actionPanel.Children.Add(CreateButton("导出 WebP", OnWebpClick));
        Grid.SetRow(_actionPanel, 3);
        root.Children.Add(_actionPanel);
        return root;
    }

    private StackPanel CreatePreviewPanel()
    {
        var panel = new StackPanel
        {
            Margin = new Thickness(0, 0, 0, 18),
        };
        var frame = new Border
        {
            Height = 318,
            Background = System.Windows.Media.Brushes.Black,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            ClipToBounds = true,
        };
        frame.SetResourceReference(Border.BorderBrushProperty, "AppBorderBrush");
        _preview.LoadedBehavior = MediaState.Manual;
        _preview.UnloadedBehavior = MediaState.Manual;
        _preview.ScrubbingEnabled = true;
        _preview.Stretch = Stretch.Uniform;
        _preview.Volume = 0;
        _previewFrame.Stretch = Stretch.Uniform;
        _previewFrame.Visibility = Visibility.Visible;
        var previewLayers = new Grid();
        previewLayers.Children.Add(_preview);
        previewLayers.Children.Add(_previewFrame);
        frame.Child = previewLayers;
        panel.Children.Add(frame);

        var footer = new Grid { Margin = new Thickness(0, 9, 0, 0) };
        footer.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star),
        });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var positionPanel = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
        };
        _previewPositionText.FontFamily = new System.Windows.Media.FontFamily("Consolas");
        _previewPositionText.FontSize = 13;
        _previewPositionText.Text = "预览  00:00:00.0";
        _previewPositionText.SetResourceReference(
            TextBlock.ForegroundProperty,
            "AppTextPrimaryBrush");
        positionPanel.Children.Add(_previewPositionText);
        _previewStatusText.Margin = new Thickness(0, 2, 0, 0);
        _previewStatusText.FontSize = 11;
        _previewStatusText.Text = "正在加载预览";
        _previewStatusText.SetResourceReference(
            TextBlock.ForegroundProperty,
            "AppTextSecondaryBrush");
        positionPanel.Children.Add(_previewStatusText);
        footer.Children.Add(positionPanel);

        ConfigureButton(
            _previewPlayButton,
            "播放所选片段",
            OnPreviewPlayClick);
        _previewPlayButton.MinWidth = 118;
        _previewPlayButton.Margin = new Thickness(12, 0, 0, 0);
        _previewPlayButton.IsEnabled = false;
        Grid.SetColumn(_previewPlayButton, 1);
        footer.Children.Add(_previewPlayButton);
        panel.Children.Add(footer);
        return panel;
    }

    private StackPanel CreateTimelinePanel()
    {
        var panel = new StackPanel
        {
            Margin = new Thickness(0, 0, 0, 16),
        };
        var header = new Grid { Margin = new Thickness(0, 0, 0, 7) };
        header.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star),
        });
        header.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = GridLength.Auto,
        });
        var titlePanel = new StackPanel
        {
            Orientation = WpfOrientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var title = new TextBlock
        {
            Text = "视频时间轴",
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        title.SetResourceReference(
            TextBlock.ForegroundProperty,
            "AppTextPrimaryBrush");
        titlePanel.Children.Add(title);
        _timelineStatusText.Margin = new Thickness(10, 0, 0, 0);
        _timelineStatusText.FontSize = 11;
        _timelineStatusText.VerticalAlignment = VerticalAlignment.Center;
        _timelineStatusText.Text = "正在准备缩略帧";
        _timelineStatusText.SetResourceReference(
            TextBlock.ForegroundProperty,
            "AppTextSecondaryBrush");
        titlePanel.Children.Add(_timelineStatusText);
        header.Children.Add(titlePanel);

        var zoomPanel = new StackPanel
        {
            Orientation = WpfOrientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var zoomOut = new TextBlock
        {
            Text = "−",
            FontSize = 17,
            Width = 18,
            TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        zoomOut.SetResourceReference(
            TextBlock.ForegroundProperty,
            "AppTextSecondaryBrush");
        zoomPanel.Children.Add(zoomOut);
        _timelineZoomSlider.Minimum = 5;
        _timelineZoomSlider.Maximum = 36;
        _timelineZoomSlider.Value = _timelineFrameDensity;
        _timelineZoomSlider.Width = 140;
        _timelineZoomSlider.SmallChange = 1;
        _timelineZoomSlider.LargeChange = 4;
        _timelineZoomSlider.ToolTip = "调整缩略帧数量";
        _timelineZoomSlider.SetResourceReference(
            FrameworkElement.StyleProperty,
            "VideoTrimSlider");
        _timelineZoomSlider.ValueChanged += OnTimelineZoomChanged;
        zoomPanel.Children.Add(_timelineZoomSlider);
        var zoomIn = new TextBlock
        {
            Text = "+",
            FontSize = 16,
            Width = 18,
            TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        zoomIn.SetResourceReference(
            TextBlock.ForegroundProperty,
            "AppTextSecondaryBrush");
        zoomPanel.Children.Add(zoomIn);
        Grid.SetColumn(zoomPanel, 1);
        header.Children.Add(zoomPanel);
        panel.Children.Add(header);

        _timelineLayers.Height = 104;
        _timelineLayers.MinWidth = 560;
        _timelineLayers.Background = System.Windows.Media.Brushes.Black;
        _timelineLayers.PreviewMouseLeftButtonDown += OnTimelineMouseLeftButtonDown;
        _timelineLayers.PreviewMouseWheel += OnTimelineMouseWheel;
        _timelineFramesCanvas.Height = 104;
        _timelineOverlayCanvas.Height = 104;
        _timelineLayers.Children.Add(_timelineFramesCanvas);
        _timelineLayers.Children.Add(_timelineOverlayCanvas);

        ConfigureTimelineOverlay();
        _timelineScrollViewer.Content = _timelineLayers;
        _timelineScrollViewer.Height = 122;
        _timelineScrollViewer.HorizontalScrollBarVisibility =
            ScrollBarVisibility.Disabled;
        _timelineScrollViewer.VerticalScrollBarVisibility =
            ScrollBarVisibility.Disabled;
        _timelineScrollViewer.PanningMode = PanningMode.None;
        _timelineScrollViewer.SizeChanged += OnTimelineViewportSizeChanged;
        var timelineBorder = new Border
        {
            Background = System.Windows.Media.Brushes.Black,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5),
            ClipToBounds = true,
            Child = _timelineScrollViewer,
        };
        timelineBorder.SetResourceReference(
            Border.BorderBrushProperty,
            "AppBorderBrush");
        panel.Children.Add(timelineBorder);
        return panel;
    }

    private void ConfigureTimelineOverlay()
    {
        foreach (var shade in new[] { _timelineLeftShade, _timelineRightShade })
        {
            shade.Height = 104;
            shade.Background = System.Windows.Media.Brushes.Black;
            shade.Opacity = 0.58;
            shade.IsHitTestVisible = false;
            _timelineOverlayCanvas.Children.Add(shade);
        }

        _timelineSelectionBorder.Height = 104;
        _timelineSelectionBorder.BorderThickness = new Thickness(2);
        _timelineSelectionBorder.IsHitTestVisible = false;
        _timelineSelectionBorder.SetResourceReference(
            Border.BorderBrushProperty,
            "AppAccentBrush");
        _timelineOverlayCanvas.Children.Add(_timelineSelectionBorder);

        _timelinePlayhead.Width = 2;
        _timelinePlayhead.Height = 104;
        _timelinePlayhead.IsHitTestVisible = false;
        _timelinePlayhead.SetResourceReference(
            Border.BackgroundProperty,
            "AppAccentForegroundBrush");
        _timelineOverlayCanvas.Children.Add(_timelinePlayhead);

        ConfigureTimelineHandle(
            _timelineStartHandle,
            "拖动选择保留起点",
            OnTimelineStartHandleDragDelta);
        ConfigureTimelineHandle(
            _timelineEndHandle,
            "拖动选择保留终点",
            OnTimelineEndHandleDragDelta);
        _timelineOverlayCanvas.Children.Add(_timelineStartHandle);
        _timelineOverlayCanvas.Children.Add(_timelineEndHandle);
    }

    private static void ConfigureTimelineHandle(
        Thumb handle,
        string toolTip,
        DragDeltaEventHandler dragHandler)
    {
        handle.ToolTip = toolTip;
        handle.SetResourceReference(
            FrameworkElement.StyleProperty,
            "VideoTrimTimelineHandle");
        handle.DragDelta += dragHandler;
    }

    private static void ConfigureRangeSlider(Slider slider)
    {
        slider.Minimum = 0;
        slider.SmallChange = 0.1;
        slider.LargeChange = 1;
        slider.TickFrequency = 0.5;
        slider.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty,
            "AppControlForegroundBrush");
        slider.SetResourceReference(System.Windows.Controls.Control.BackgroundProperty,
            "ImageEditorSliderTrackBrush");
        slider.SetResourceReference(FrameworkElement.StyleProperty,
            "VideoTrimSlider");
    }

    private static WpfButton CreateButton(
        string label,
        RoutedEventHandler handler)
    {
        var button = new WpfButton();
        ConfigureButton(button, label, handler);
        return button;
    }

    private static void ConfigureButton(
        WpfButton button,
        string label,
        RoutedEventHandler handler)
    {
        button.Content = label;
        button.MinWidth = 94;
        button.Height = 34;
        button.Margin = new Thickness(8, 0, 0, 0);
        button.Click += handler;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        try
        {
            _preview.Source = new Uri(_inputPath, UriKind.Absolute);
            _framePreviewSession = await VideoPreviewSession.CreateAsync(_inputPath);
            _duration = _framePreviewSession.Duration;
            _previewPlayButton.IsEnabled = true;
            _updatingRange = true;
            try
            {
                _startSlider.Maximum = _duration.TotalSeconds;
                _endSlider.Maximum = _duration.TotalSeconds;
                _endSlider.Value = _duration.TotalSeconds;
            }
            finally
            {
                _updatingRange = false;
            }
            InitializeTimeline();
            UpdateRangeText();
            SeekPreview(_startSlider.Value, "起点");
            _statusText.Text = "GIF/WebP 会按时长自动控制帧率和尺寸。";
        }
        catch (Exception exception)
        {
            _statusText.Text = $"无法读取视频：{exception.Message}";
            SetActionsEnabled(false);
        }
    }

    private void OnRangeChanged(object sender,
        RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingRange || _duration == TimeSpan.Zero)
        {
            return;
        }
        _updatingRange = true;
        if (_startSlider.Value > _endSlider.Value - 0.1)
        {
            if (ReferenceEquals(sender, _startSlider))
            {
                _endSlider.Value = Math.Min(
                    _duration.TotalSeconds,
                    _startSlider.Value + 0.1);
            }
            else
            {
                _startSlider.Value = Math.Max(0, _endSlider.Value - 0.1);
            }
        }
        _updatingRange = false;
        UpdateRangeText();
        QueuePreviewSeek(
            ReferenceEquals(sender, _startSlider)
                ? _startSlider.Value
                : _endSlider.Value,
            ReferenceEquals(sender, _startSlider) ? "起点" : "终点");
    }

    private void UpdateRangeText()
    {
        var start = TimeSpan.FromSeconds(_startSlider.Value);
        var end = TimeSpan.FromSeconds(_endSlider.Value);
        _rangeText.Text =
            $"{FormatTime(start)}  -  {FormatTime(end)}   " +
            $"保留 {FormatTime(end - start)}";
        UpdateTimelineOverlays();
    }

    private static string FormatTime(TimeSpan value) =>
        value.ToString(@"hh\:mm\:ss\.f", CultureInfo.InvariantCulture);

    private void InitializeTimeline()
    {
        var viewportWidth = Math.Max(
            560,
            _timelineScrollViewer.ViewportWidth > 0
                ? _timelineScrollViewer.ViewportWidth
                : _timelineScrollViewer.ActualWidth);
        _timelineFrameDensity = Math.Clamp(
            Math.Round(viewportWidth / 72),
            _timelineZoomSlider.Minimum,
            _timelineZoomSlider.Maximum);
        _updatingTimelineZoom = true;
        try
        {
            _timelineZoomSlider.Value = _timelineFrameDensity;
        }
        finally
        {
            _updatingTimelineZoom = false;
        }

        UpdateTimelineLayout();
        QueueTimelineRender(immediate: true);
    }

    private void OnTimelineZoomChanged(
        object sender,
        RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingTimelineZoom || _duration == TimeSpan.Zero)
        {
            return;
        }

        _timelineFrameDensity = e.NewValue;
        QueueTimelineRender(immediate: false);
    }

    private void OnTimelineMouseWheel(object sender, MouseWheelEventArgs e)
    {
        var change = e.Delta > 0 ? 1 : -1;
        _timelineZoomSlider.Value = Math.Clamp(
            _timelineZoomSlider.Value + change,
            _timelineZoomSlider.Minimum,
            _timelineZoomSlider.Maximum);
        e.Handled = true;
    }

    private void OnTimelineViewportSizeChanged(
        object sender,
        SizeChangedEventArgs e)
    {
        if (_duration == TimeSpan.Zero ||
            Math.Abs(e.NewSize.Width - e.PreviousSize.Width) < 2)
        {
            return;
        }

        UpdateTimelineLayout();
        QueueTimelineRender(immediate: false);
    }

    private void OnTimelineZoomTimerTick(object? sender, EventArgs e)
    {
        _timelineZoomTimer.Stop();
        _ = RenderTimelineFramesAsync(_timelineRenderGeneration);
    }

    private void QueueTimelineRender(bool immediate)
    {
        _timelineRenderGeneration++;
        _timelineZoomTimer.Stop();
        if (immediate)
        {
            _ = RenderTimelineFramesAsync(_timelineRenderGeneration);
        }
        else
        {
            _timelineStatusText.Text = "正在调整时间轴";
            _timelineZoomTimer.Start();
        }
    }

    private void UpdateTimelineLayout()
    {
        if (_duration == TimeSpan.Zero)
        {
            return;
        }

        var viewportWidth = Math.Max(
            560,
            _timelineScrollViewer.ViewportWidth > 0
                ? _timelineScrollViewer.ViewportWidth
                : _timelineScrollViewer.ActualWidth);
        var timelineWidth = viewportWidth;
        _timelineEffectivePixelsPerSecond =
            timelineWidth / _duration.TotalSeconds;
        _timelineLayers.Width = timelineWidth;
        _timelineFramesCanvas.Width = timelineWidth;
        _timelineOverlayCanvas.Width = timelineWidth;
        UpdateTimelineOverlays();
        _timelineScrollViewer.ScrollToLeftEnd();
    }

    private void UpdateTimelineOverlays()
    {
        if (_duration == TimeSpan.Zero ||
            _timelineEffectivePixelsPerSecond <= 0)
        {
            return;
        }

        var timelineWidth = _timelineLayers.Width;
        var startX = Math.Clamp(
            _startSlider.Value * _timelineEffectivePixelsPerSecond,
            0,
            timelineWidth);
        var endX = Math.Clamp(
            _endSlider.Value * _timelineEffectivePixelsPerSecond,
            startX,
            timelineWidth);
        _timelineLeftShade.Width = startX;
        Canvas.SetLeft(_timelineLeftShade, 0);
        _timelineRightShade.Width = Math.Max(0, timelineWidth - endX);
        Canvas.SetLeft(_timelineRightShade, endX);
        _timelineSelectionBorder.Width = Math.Max(1, endX - startX);
        Canvas.SetLeft(_timelineSelectionBorder, startX);
        Canvas.SetLeft(
            _timelineStartHandle,
            Math.Clamp(startX - 7, 0, Math.Max(0, timelineWidth - 14)));
        Canvas.SetLeft(
            _timelineEndHandle,
            Math.Clamp(endX - 7, 0, Math.Max(0, timelineWidth - 14)));
        var playheadX = Math.Clamp(
            _timelinePreviewSeconds * _timelineEffectivePixelsPerSecond,
            0,
            timelineWidth);
        Canvas.SetLeft(_timelinePlayhead, Math.Max(0, playheadX - 1));
    }

    private void OnTimelineStartHandleDragDelta(
        object sender,
        DragDeltaEventArgs e)
    {
        if (_timelineEffectivePixelsPerSecond <= 0)
        {
            return;
        }

        _startSlider.Value = Math.Clamp(
            _startSlider.Value +
                (e.HorizontalChange / _timelineEffectivePixelsPerSecond),
            0,
            Math.Max(0, _endSlider.Value - 0.1));
        e.Handled = true;
    }

    private void OnTimelineEndHandleDragDelta(
        object sender,
        DragDeltaEventArgs e)
    {
        if (_timelineEffectivePixelsPerSecond <= 0)
        {
            return;
        }

        _endSlider.Value = Math.Clamp(
            _endSlider.Value +
                (e.HorizontalChange / _timelineEffectivePixelsPerSecond),
            Math.Min(_duration.TotalSeconds, _startSlider.Value + 0.1),
            _duration.TotalSeconds);
        e.Handled = true;
    }

    private void OnTimelineMouseLeftButtonDown(
        object sender,
        MouseButtonEventArgs e)
    {
        if (_duration == TimeSpan.Zero ||
            FindVisualAncestor<Thumb>(e.OriginalSource as DependencyObject) is not null)
        {
            return;
        }

        var position = e.GetPosition(_timelineLayers);
        var seconds = Math.Clamp(
            position.X / _timelineEffectivePixelsPerSecond,
            0,
            _duration.TotalSeconds);
        SeekPreview(seconds, "预览");
        e.Handled = true;
    }

    private static T? FindVisualAncestor<T>(DependencyObject? element)
        where T : DependencyObject
    {
        while (element is not null)
        {
            if (element is T match)
            {
                return match;
            }

            element = VisualTreeHelper.GetParent(element);
        }

        return null;
    }

    private async Task RenderTimelineFramesAsync(int generation)
    {
        var session = _framePreviewSession;
        if (session is null || _closed || generation != _timelineRenderGeneration)
        {
            return;
        }

        var timelineWidth = _timelineLayers.Width;
        var frameCount = Math.Clamp(
            (int)Math.Round(_timelineFrameDensity),
            (int)_timelineZoomSlider.Minimum,
            (int)_timelineZoomSlider.Maximum);
        var tileWidth = timelineWidth / frameCount;
        var labelStride = Math.Max(1, (int)Math.Ceiling(52 / tileWidth));
        _timelineFramesCanvas.Children.Clear();
        _timelineStatusText.Text = $"正在生成 0/{frameCount} 帧";
        try
        {
            for (var index = 0; index < frameCount; index++)
            {
                var position = TimeSpan.FromTicks(
                    _duration.Ticks * ((index * 2L) + 1) /
                    (frameCount * 2L));
                var bytes = await session.GetFrameAsync(position, 192);
                if (_closed || generation != _timelineRenderGeneration)
                {
                    return;
                }

                var image = new WpfImage
                {
                    Source = CreatePreviewBitmap(bytes),
                    Stretch = Stretch.UniformToFill,
                    Width = tileWidth + 1,
                    Height = 78,
                };
                var frame = new Border
                {
                    Width = tileWidth + 1,
                    Height = 78,
                    BorderThickness = new Thickness(0, 0, 1, 0),
                    BorderBrush = new SolidColorBrush(
                        System.Windows.Media.Color.FromArgb(90, 255, 255, 255)),
                    ClipToBounds = true,
                    Child = image,
                };
                Canvas.SetLeft(frame, index * tileWidth);
                Canvas.SetTop(frame, 0);
                _timelineFramesCanvas.Children.Add(frame);

                if (index % labelStride == 0 || index == frameCount - 1)
                {
                    var time = new TextBlock
                    {
                        Text = FormatTimelineTime(position),
                        FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                        FontSize = 10,
                        Foreground = System.Windows.Media.Brushes.White,
                        Opacity = 0.84,
                    };
                    Canvas.SetLeft(time, (index * tileWidth) + 4);
                    Canvas.SetTop(time, 82);
                    _timelineFramesCanvas.Children.Add(time);
                }
                if (index == frameCount - 1 || index % 8 == 7)
                {
                    _timelineStatusText.Text =
                        $"正在生成 {index + 1}/{frameCount} 帧";
                }
            }

            _timelineStatusText.Text = $"已生成 {frameCount} 个缩略帧";
        }
        catch (Exception exception)
        {
            if (generation == _timelineRenderGeneration)
            {
                _timelineStatusText.Text =
                    $"无法生成时间轴：{exception.Message}";
            }
        }
    }

    private static string FormatTimelineTime(TimeSpan value) =>
        value.TotalHours >= 1
            ? value.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture)
            : value.ToString(@"mm\:ss\.f", CultureInfo.InvariantCulture);

    private void OnPreviewMediaOpened(object sender, RoutedEventArgs e)
    {
        _mediaReady = true;
        _previewStatusText.Text = "预览已就绪";
        _previewPlayButton.IsEnabled = true;
        _preview.Play();
        _ = Dispatcher.BeginInvoke(
            DispatcherPriority.Render,
            () =>
            {
                if (!_mediaReady)
                {
                    return;
                }

                _preview.Pause();
                SeekPreview(_startSlider.Value, "起点");
            });
    }

    private void OnPreviewMediaFailed(object? sender, ExceptionRoutedEventArgs e)
    {
        _mediaReady = false;
        _isPreviewPlaying = false;
        _framePlaybackClock.Stop();
        _previewTimer.Stop();
        _previewPlayButton.IsEnabled = _framePreviewSession is not null;
        _previewStatusText.Text = _framePreviewSession is null
            ? $"预览不可用：{e.ErrorException?.Message ?? "视频格式不受系统支持"}"
            : "使用逐帧模式播放与预览";
    }

    private void OnPreviewPlayClick(object sender, RoutedEventArgs e)
    {
        if (!_mediaReady && _framePreviewSession is null)
        {
            return;
        }

        if (_isPreviewPlaying)
        {
            PausePreview();
            return;
        }

        var currentSeconds = _startSlider.Value;

        _isPreviewPlaying = true;
        _isExtractedFramePlayback = _framePreviewSession is not null;
        _previewPlayButton.Content = "暂停预览";
        _previewStatusText.Text = "正在播放所选片段";
        if (!_isExtractedFramePlayback)
        {
            _preview.Position = TimeSpan.FromSeconds(currentSeconds);
            _previewFrame.Visibility = Visibility.Collapsed;
            _preview.Play();
        }
        else
        {
            _previewFrame.Visibility = Visibility.Visible;
            _framePlaybackStartSeconds = currentSeconds;
            _framePlaybackClock.Restart();
        }
        _previewTimer.Start();
    }

    private void OnPreviewTimerTick(object? sender, EventArgs e)
    {
        if (!_isPreviewPlaying)
        {
            return;
        }

        if (!_isExtractedFramePlayback)
        {
            var end = TimeSpan.FromSeconds(_endSlider.Value);
            if (_preview.Position >= end)
            {
                _preview.Position = end;
                PausePreview();
            }

            _previewPositionText.Text = $"预览  {FormatTime(_preview.Position)}";
            _timelinePreviewSeconds = _preview.Position.TotalSeconds;
            UpdateTimelineOverlays();
            return;
        }

        var frameSeconds = _framePlaybackStartSeconds +
            _framePlaybackClock.Elapsed.TotalSeconds;
        if (frameSeconds >= _endSlider.Value)
        {
            frameSeconds = _endSlider.Value;
            PausePreview();
        }

        _pendingSeekSeconds = frameSeconds;
        _pendingSeekEndpoint = "所选片段";
        _timelinePreviewSeconds = frameSeconds;
        _previewPositionText.Text =
            $"预览  {FormatTime(TimeSpan.FromSeconds(frameSeconds))}";
        UpdateTimelineOverlays();
        _seekSerial++;
        RequestPreviewFrame(640);
    }

    private void OnSliderMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _isScrubbing = true;
        if (_isPreviewPlaying)
        {
            PausePreview();
        }
    }

    private void OnSliderMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _isScrubbing = false;
        FlushPendingSeek();
    }

    private void OnSliderLostMouseCapture(object sender, WpfMouseEventArgs e)
    {
        if (_isScrubbing)
        {
            _isScrubbing = false;
            FlushPendingSeek();
        }
    }

    private void OnScrubTimerTick(object? sender, EventArgs e)
    {
        if (!_mediaReady && _framePreviewSession is null)
        {
            return;
        }

        _scrubTimer.Stop();
        ApplyPendingSeek();
    }

    private void QueuePreviewSeek(double seconds, string endpoint)
    {
        _pendingSeekSeconds = Math.Max(0, seconds);
        _pendingSeekEndpoint = endpoint;
        _timelinePreviewSeconds = _pendingSeekSeconds;
        UpdateTimelineOverlays();
        _previewPositionText.Text =
            $"{endpoint}  {FormatTime(TimeSpan.FromSeconds(_pendingSeekSeconds))}";
        if (!_mediaReady && _framePreviewSession is null)
        {
            return;
        }

        if (_isPreviewPlaying)
        {
            PausePreview();
        }

        _scrubTimer.Start();
    }

    private void FlushPendingSeek()
    {
        _scrubTimer.Stop();
        if (_mediaReady || _framePreviewSession is not null)
        {
            ApplyPendingSeek();
        }
    }

    private void SeekPreview(double seconds, string endpoint)
    {
        _pendingSeekSeconds = Math.Max(0, seconds);
        _pendingSeekEndpoint = endpoint;
        _timelinePreviewSeconds = _pendingSeekSeconds;
        UpdateTimelineOverlays();
        _previewPositionText.Text =
            $"{endpoint}  {FormatTime(TimeSpan.FromSeconds(_pendingSeekSeconds))}";
        if (!_mediaReady && _framePreviewSession is null)
        {
            return;
        }

        if (_isPreviewPlaying)
        {
            PausePreview();
        }

        _scrubTimer.Stop();
        ApplyPendingSeek();
    }

    private void ApplyPendingSeek()
    {
        var position = TimeSpan.FromSeconds(_pendingSeekSeconds);
        var serial = ++_seekSerial;
        _previewStatusText.Text = $"正在提取{_pendingSeekEndpoint}帧";
        if (_mediaReady)
        {
            _preview.Position = position;
            // MediaElement often updates Position without repainting until it
            // enters the render queue. Keep this pulse for supported codecs.
            _preview.Play();
            _ = Dispatcher.BeginInvoke(
                DispatcherPriority.Render,
                () =>
                {
                    if (_mediaReady && serial == _seekSerial &&
                        !_isPreviewPlaying)
                    {
                        _preview.Pause();
                    }
                });
        }

        RequestPreviewFrame();
    }

    private void RequestPreviewFrame(int thumbnailWidth = 960)
    {
        if (_framePreviewSession is null || _closed)
        {
            return;
        }

        _pendingPreviewWidth = thumbnailWidth;
        _frameRequestPending = true;
        if (!_frameRequestInProgress)
        {
            _ = DrainPreviewFrameRequestsAsync();
        }
    }

    private async Task DrainPreviewFrameRequestsAsync()
    {
        if (_frameRequestInProgress)
        {
            return;
        }

        _frameRequestInProgress = true;
        try
        {
            while (_frameRequestPending && !_closed)
            {
                _frameRequestPending = false;
                var session = _framePreviewSession;
                if (session is null)
                {
                    return;
                }

                var serial = _seekSerial;
                var endpoint = _pendingSeekEndpoint;
                var position = TimeSpan.FromSeconds(_pendingSeekSeconds);
                var thumbnailWidth = _pendingPreviewWidth;
                try
                {
                    var bytes = await session.GetFrameAsync(
                        position,
                        thumbnailWidth);
                    if (_closed)
                    {
                        return;
                    }

                    if (serial != _seekSerial)
                    {
                        _frameRequestPending = true;
                        if (!_isPreviewPlaying || !_isExtractedFramePlayback)
                        {
                            continue;
                        }
                    }

                    _previewFrame.Source = CreatePreviewBitmap(bytes);
                    _previewFrame.Visibility = Visibility.Visible;
                    _previewStatusText.Text = $"正在预览{endpoint}";
                }
                catch (Exception exception)
                {
                    _previewStatusText.Text =
                        $"无法提取预览帧：{exception.Message}";
                    return;
                }
            }
        }
        finally
        {
            _frameRequestInProgress = false;
        }
    }

    private static BitmapImage CreatePreviewBitmap(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    private void PausePreview()
    {
        _preview.Pause();
        _previewTimer.Stop();
        _scrubTimer.Stop();
        _framePlaybackClock.Stop();
        _isPreviewPlaying = false;
        _isExtractedFramePlayback = false;
        _previewPlayButton.Content = "播放所选片段";
        _previewStatusText.Text = "预览已暂停";
    }

    private void OnOpenFolderClick(object sender, RoutedEventArgs e)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"/select,\"{_inputPath}\"",
            UseShellExecute = true,
        });
    }

    private async void OnTrimClick(object sender, RoutedEventArgs e)
    {
        await RunAsync(async progress =>
            await VideoPostProcessingService.TrimMp4Async(
                _inputPath,
                TimeSpan.FromSeconds(_startSlider.Value),
                TimeSpan.FromSeconds(_endSlider.Value),
                progress));
    }

    private async void OnGifClick(object sender, RoutedEventArgs e)
    {
        await ExportAsync(AnimatedImageFormat.Gif);
    }

    private async void OnWebpClick(object sender, RoutedEventArgs e)
    {
        await ExportAsync(AnimatedImageFormat.WebP);
    }

    private Task ExportAsync(AnimatedImageFormat format) => RunAsync(
        async progress => await VideoPostProcessingService.ExportAnimatedImageAsync(
            _inputPath,
            TimeSpan.FromSeconds(_startSlider.Value),
            TimeSpan.FromSeconds(_endSlider.Value),
            format,
            progress));

    private async Task RunAsync(Func<IProgress<double>, Task<string>> operation)
    {
        if (_isPreviewPlaying)
        {
            PausePreview();
        }
        SetActionsEnabled(false);
        _progress.Visibility = Visibility.Visible;
        _progress.Value = 0;
        _statusText.Text = "正在处理，请稍候...";
        try
        {
            var progress = new Progress<double>(value =>
                _progress.Value = Math.Clamp(value, 0, 1) * 100);
            var output = await operation(progress);
            try
            {
                await ClipboardFileService.SetFileAsync(output);
                _statusText.Text = $"已生成并复制：{output}";
            }
            catch
            {
                _statusText.Text = $"已生成：{output}（复制到剪贴板失败）";
            }
        }
        catch (Exception exception)
        {
            _statusText.Text = $"处理失败：{exception.Message}";
        }
        finally
        {
            SetActionsEnabled(true);
        }
    }

    private void SetActionsEnabled(bool enabled)
    {
        foreach (var button in _actionPanel.Children.OfType<WpfButton>())
        {
            button.IsEnabled = enabled;
        }
        _startSlider.IsEnabled = enabled;
        _endSlider.IsEnabled = enabled;
        _timelineStartHandle.IsEnabled = enabled;
        _timelineEndHandle.IsEnabled = enabled;
        _timelineZoomSlider.IsEnabled = enabled;
        _previewPlayButton.IsEnabled =
            enabled && (_mediaReady || _framePreviewSession is not null);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _closed = true;
        _timelineRenderGeneration++;
        _previewTimer.Stop();
        _scrubTimer.Stop();
        _timelineZoomTimer.Stop();
        _framePlaybackClock.Stop();
        _previewTimer.Tick -= OnPreviewTimerTick;
        _scrubTimer.Tick -= OnScrubTimerTick;
        _timelineZoomTimer.Tick -= OnTimelineZoomTimerTick;
        _preview.MediaOpened -= OnPreviewMediaOpened;
        _preview.MediaFailed -= OnPreviewMediaFailed;
        _timelineZoomSlider.ValueChanged -= OnTimelineZoomChanged;
        _timelineLayers.PreviewMouseLeftButtonDown -=
            OnTimelineMouseLeftButtonDown;
        _timelineLayers.PreviewMouseWheel -= OnTimelineMouseWheel;
        _timelineScrollViewer.SizeChanged -= OnTimelineViewportSizeChanged;
        _timelineStartHandle.DragDelta -= OnTimelineStartHandleDragDelta;
        _timelineEndHandle.DragDelta -= OnTimelineEndHandleDragDelta;
        _preview.Close();
    }
}
