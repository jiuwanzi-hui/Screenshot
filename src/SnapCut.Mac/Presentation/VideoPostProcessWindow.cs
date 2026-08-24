using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using SnapCut.Mac.App;
using SnapCut.Mac.Native;
using SnapCut.Mac.Recording;

namespace SnapCut.Mac.Presentation;

internal sealed class VideoPostProcessWindow : Window, IDisposable
{
    private readonly string _inputPath;
    private readonly MacSettings _settings;
    private readonly Image _preview = new() { Stretch = Stretch.Uniform };
    private readonly StackPanel _frames = new()
    {
        Orientation = Orientation.Horizontal,
        Spacing = 3,
    };
    private readonly Slider _start = new() { Minimum = 0 };
    private readonly Slider _end = new() { Minimum = 0 };
    private readonly TextBlock _rangeText = new();
    private readonly TextBlock _status = new();
    private readonly ComboBox _format = new()
    {
        ItemsSource = Enum.GetValues<MacVideoExportFormat>(),
        SelectedItem = MacVideoExportFormat.Mp4,
        MinWidth = 100,
    };
    private readonly string _temporaryDirectory = Path.Combine(
        Path.GetTempPath(),
        $"SnapCut-VideoEdit-{Guid.NewGuid():N}");
    private CancellationTokenSource? _previewCancellation;
    private double _duration;
    private bool _disposed;

    public VideoPostProcessWindow(string inputPath, MacSettings settings)
    {
        _inputPath = inputPath;
        _settings = settings;
        Title = "SnapCut 视频编辑";
        Width = 900;
        Height = 680;
        MinWidth = 680;
        MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush(MacTheme.WindowBackground);
        Directory.CreateDirectory(_temporaryDirectory);
        _rangeText.Foreground = new SolidColorBrush(MacTheme.SecondaryText);
        _status.Foreground = new SolidColorBrush(MacTheme.SecondaryText);
        var play = MacTheme.CreateButton("播放预剪辑片段");
        play.Click += async (_, _) => await PlayPreviewAsync();
        var export = MacTheme.CreateButton("导出", primary: true);
        export.Click += async (_, _) => await ExportAsync();
        var close = MacTheme.CreateButton("关闭");
        close.Click += (_, _) => Close();
        var rangeGrid = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,Auto"),
            RowSpacing = 5,
            Children =
            {
                Labeled("开始", _start),
                Labeled("结束", _end),
            },
        };
        Grid.SetRow(rangeGrid.Children[1], 1);
        var actions = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto,Auto"),
            ColumnSpacing = 8,
            Children = { _status, _format, play, export, close },
        };
        Grid.SetColumn(_format, 1);
        Grid.SetColumn(play, 2);
        Grid.SetColumn(export, 3);
        Grid.SetColumn(close, 4);
        var root = new Grid
        {
            Margin = new Thickness(18),
            RowDefinitions = new RowDefinitions("*,Auto,Auto,Auto,Auto"),
            RowSpacing = 10,
            Children =
            {
                new Border
                {
                    Background = Brushes.Black,
                    CornerRadius = new CornerRadius(5),
                    Child = _preview,
                },
                new ScrollViewer
                {
                    HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                    VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                    Content = _frames,
                },
                rangeGrid,
                _rangeText,
                actions,
            },
        };
        for (var index = 1; index < root.Children.Count; index++)
        {
            Grid.SetRow(root.Children[index], index);
        }
        Content = root;
        _start.PropertyChanged += async (_, args) =>
        {
            if (args.Property == Slider.ValueProperty)
            {
                if (_start.Value > _end.Value - 0.05)
                {
                    _start.Value = Math.Max(0, _end.Value - 0.05);
                }
                await UpdatePreviewAsync(_start.Value);
            }
        };
        _end.PropertyChanged += async (_, args) =>
        {
            if (args.Property == Slider.ValueProperty)
            {
                if (_end.Value < _start.Value + 0.05)
                {
                    _end.Value = Math.Min(_duration, _start.Value + 0.05);
                }
                await UpdatePreviewAsync(_end.Value);
            }
        };
        Opened += async (_, _) =>
        {
            MacNativeUi.ExcludeFromScreenCapture(this);
            await InitializeAsync();
        };
        Closed += (_, _) =>
        {
            Dispose();
            try
            {
                Directory.Delete(_temporaryDirectory, recursive: true);
            }
            catch (IOException)
            {
            }
        };
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _previewCancellation?.Cancel();
        _previewCancellation?.Dispose();
    }

    private async Task InitializeAsync()
    {
        _status.Text = "正在读取视频…";
        _duration = await MacVideoPostProcessingService.ProbeDurationAsync(_inputPath);
        if (_duration <= 0)
        {
            _status.Text = "无法读取视频时长";
            return;
        }
        _start.Maximum = _duration;
        _end.Maximum = _duration;
        _end.Value = _duration;
        for (var index = 0; index < 10; index++)
        {
            var time = _duration * index / 9;
            var path = Path.Combine(_temporaryDirectory, $"frame-{index:D2}.jpg");
            if (await MacVideoPostProcessingService.ExtractFrameAsync(
                    _inputPath, time, path))
            {
                using var stream = File.OpenRead(path);
                var image = new Image
                {
                    Source = new Bitmap(stream),
                    Width = 80,
                    Height = 52,
                    Stretch = Stretch.UniformToFill,
                };
                image.PointerPressed += (_, _) => _start.Value = time;
                _frames.Children.Add(image);
            }
        }
        await UpdatePreviewAsync(0);
        _status.Text = "可拖动开始和结束滑块实时查看对应帧";
    }

    private async Task UpdatePreviewAsync(double seconds)
    {
        if (_duration <= 0)
        {
            return;
        }
        _rangeText.Text = $"{FormatTime(_start.Value)}  -  {FormatTime(_end.Value)}  ·  时长 {FormatTime(_end.Value - _start.Value)}";
        _previewCancellation?.Cancel();
        _previewCancellation = new CancellationTokenSource();
        var path = Path.Combine(_temporaryDirectory, "preview.jpg");
        try
        {
            if (await MacVideoPostProcessingService.ExtractFrameAsync(
                    _inputPath,
                    seconds,
                    path,
                    _previewCancellation.Token))
            {
                using var stream = File.OpenRead(path);
                _preview.Source = new Bitmap(stream);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task PlayPreviewAsync()
    {
        var path = Path.Combine(_temporaryDirectory, "preview-clip.mp4");
        _status.Text = "正在生成预剪辑片段…";
        var result = await MacVideoPostProcessingService.ExportAsync(
            _inputPath,
            _start.Value,
            _end.Value,
            path,
            MacVideoExportFormat.Mp4,
            _settings.VideoCodec,
            _settings.VideoFrameRate);
        _status.Text = result.IsSuccess ? "正在播放预剪辑片段" : result.Error;
        if (result.IsSuccess)
        {
            MacNativeUi.OpenPath(path);
        }
    }

    private async Task ExportAsync()
    {
        var format = _format.SelectedItem is MacVideoExportFormat selected
            ? selected
            : MacVideoExportFormat.Mp4;
        var extension = format switch
        {
            MacVideoExportFormat.Gif => ".gif",
            MacVideoExportFormat.WebP => ".webp",
            _ => ".mp4",
        };
        var output = Path.Combine(
            Path.GetDirectoryName(_inputPath) ?? _temporaryDirectory,
            Path.GetFileNameWithoutExtension(_inputPath) + "-trimmed" + extension);
        _status.Text = "正在导出…";
        var result = await MacVideoPostProcessingService.ExportAsync(
            _inputPath,
            _start.Value,
            _end.Value,
            output,
            format,
            _settings.VideoCodec,
            _settings.VideoFrameRate);
        _status.Text = result.IsSuccess ? $"已导出：{output}" : result.Error;
    }

    private static Grid Labeled(string label, Control control)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("52,*"),
            Children =
            {
                new TextBlock
                {
                    Text = label,
                    VerticalAlignment = VerticalAlignment.Center,
                },
                control,
            },
        };
        Grid.SetColumn(control, 1);
        return grid;
    }

    private static string FormatTime(double seconds) =>
        TimeSpan.FromSeconds(Math.Max(0, seconds)).ToString(@"mm\:ss\.f");
}
