using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Screenshot.App.Presentation;
using Button = System.Windows.Controls.Button;
using Image = System.Windows.Controls.Image;

namespace Screenshot.App.Text;

public partial class ImageQuestionWindow
{
    private readonly List<Window> _previews = [];
    private bool _loadingImages;

    private sealed class QuestionImage(BitmapSource pixels)
    {
        internal BitmapSource Pixels { get; } = pixels;
        private byte[]? _png;
        internal byte[] Encode() => _png ??= ImageQuestionService.EncodeImage(Pixels);
        internal long PixelBytes => (long)Pixels.PixelWidth * Pixels.PixelHeight * 4;
    }

    internal bool AddImage(BitmapSource source)
    {
        if (_closed) return false;
        if (_images.Count >= 5) { StatusText.Text = "最多添加五张图片，请先删除一张。"; return false; }
        var held = _images.Concat(_pending.SelectMany(p => p.Images)).Concat(_active?.Images ?? []).Distinct().Sum(p => p.PixelBytes);
        var budget = Math.Clamp(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 8, 64L * 1024 * 1024, 512L * 1024 * 1024);
        if (held + (long)source.PixelWidth * source.PixelHeight * 4 > budget)
        { StatusText.Text = "图片占用超过当前可用内存预算，请删除图片或等当前任务完成后再添加。"; return false; }
        var pixels = source.IsFrozen ? source : source.Clone();
        if (!pixels.IsFrozen) pixels.Freeze();
        _images.Add(new(pixels)); RefreshImages(); SetImagesExpanded(true); return true;
    }

    internal void RemoveImage(int index)
    {
        if (index < 0 || index >= _images.Count) return;
        _images.RemoveAt(index); RefreshImages();
    }

    private void OnToggleImagesClick(object sender, RoutedEventArgs e) => SetImagesExpanded(AttachmentBody.Visibility != Visibility.Visible);
    private void SetImagesExpanded(bool expanded)
    {
        AttachmentBody.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        ImagesToggleLabel.Text = expanded ? "收起 ▴" : "展开 ▾";
    }

    private void RefreshImages()
    {
        AttachmentsPanel.Children.Clear();
        for (var i = 0; i < _images.Count; i++)
        {
            var item = _images[i];
            var card = new Grid { Width = 96, Height = 72, Margin = new Thickness(0, 0, 8, 0) };
            var open = new Button { Padding = new Thickness(3), Content = new Image { Source = item.Pixels, Stretch = Stretch.Uniform },
                ToolTip = $"图片 {i + 1} · {item.Pixels.PixelWidth} × {item.Pixels.PixelHeight} · 点击放大" };
            open.Click += (_, _) => ShowImagePreview(item.Pixels);
            var remove = new Button { Content = "×", Width = 22, Height = 22, Padding = new Thickness(0),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, ToolTip = "删除图片" };
            remove.Click += (_, e) => { e.Handled = true; RemoveImage(_images.IndexOf(item)); };
            card.Children.Add(open); card.Children.Add(remove); AttachmentsPanel.Children.Add(card);
        }
        ImageDetails.Text = $"本轮图片 · {_images.Count} / 5";
    }

    private async Task LoadImagesAsync(string[] paths)
    {
        if (_loadingImages || _closed) return;
        _loadingImages = true;
        var token = _lifetime.Token;
        try
        {
            foreach (var path in paths)
            {
                if (_images.Count >= 5) { StatusText.Text = "最多添加五张图片，其余图片未添加。"; break; }
                try
                {
                    var image = await Task.Run(() =>
                    {
                        token.ThrowIfCancellationRequested();
                        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                        if (file.Length > 64 * 1024 * 1024) throw new InvalidOperationException("单个图片文件不能超过 64 MB。");
                        var decoder = BitmapDecoder.Create(file, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnDemand);
                        var frame = decoder.Frames[0];
                        if ((long)frame.PixelWidth * frame.PixelHeight > 40_000_000)
                            throw new InvalidOperationException("这张图片超过 4000 万像素，请裁剪后再添加。");
                        var pixels = new WriteableBitmap(frame); pixels.Freeze();
                        token.ThrowIfCancellationRequested(); return pixels;
                    }, token);
                    if (_closed || token.IsCancellationRequested) return;
                    AddImage(image);
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex) when (ex is IOException or NotSupportedException or FileFormatException or ArgumentException or InvalidOperationException)
                { if (!_closed) StatusText.Text = ex is InvalidOperationException ? ex.Message : "无法读取该图片，请选择 PNG、JPG、BMP、GIF、TIFF 等图片文件。"; }
            }
        }
        finally { _loadingImages = false; }
    }

    private void OnImageDragOver(object sender, System.Windows.DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop)) return;
        e.Effects = _images.Count < 5 && !_loadingImages ? System.Windows.DragDropEffects.Copy : System.Windows.DragDropEffects.None;
        e.Handled = true;
    }
    private async void OnImageDrop(object sender, System.Windows.DragEventArgs e)
    {
        if (e.Data.GetData(System.Windows.DataFormats.FileDrop) is not string[] paths) return;
        e.Handled = true; await LoadImagesAsync(paths);
    }
    private async void OnAddImagesClick(object sender, RoutedEventArgs e)
    {
        var picker = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Filter = "图片|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff;*.webp|所有文件|*.*" };
        if (picker.ShowDialog(this) == true) await LoadImagesAsync(picker.FileNames);
    }

    private void ShowImagePreview(BitmapSource pixels)
    {
        var existing = _previews.FirstOrDefault(w => ReferenceEquals(w.Tag, pixels));
        if (existing is not null) { existing.Activate(); return; }
        var preview = new Window { Owner = this, Tag = pixels, Width = 900, Height = 700, MinWidth = 360, MinHeight = 300,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false, Title = "查看图片" };
        preview.SetResourceReference(BackgroundProperty, "AppPanelBackgroundBrush");
        preview.SetResourceReference(ForegroundProperty, "AppTextPrimaryBrush");
        var grid = new Grid { Margin = new Thickness(12) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); grid.RowDefinitions.Add(new RowDefinition());
        var commands = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        var fit = new Button { Content = "适应窗口", Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 0, 8, 0) };
        var actual = new Button { Content = "100%", Padding = new Thickness(10, 4, 10, 4) };
        var label = new TextBlock { Text = "滚轮缩放 · 按住图片拖动", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        commands.Children.Add(fit); commands.Children.Add(actual); commands.Children.Add(label); grid.Children.Add(commands);
        var image = new Image { Source = pixels, Stretch = Stretch.Uniform };
        var scroll = new ScrollViewer { Content = image, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetRow(scroll, 1); grid.Children.Add(scroll);
        double scale = 1; bool fitting = true;
        void SetScale(double value) { scale = Math.Clamp(value, .02, 8); image.Width = pixels.PixelWidth * scale; image.Height = pixels.PixelHeight * scale; }
        void Fit() => SetScale(Math.Min(1, Math.Min(Math.Max(1, scroll.ActualWidth - 20) / pixels.PixelWidth, Math.Max(1, scroll.ActualHeight - 20) / pixels.PixelHeight)));
        fit.Click += (_, _) => { fitting = true; Fit(); };
        actual.Click += (_, _) => { fitting = false; SetScale(1); };
        scroll.SizeChanged += (_, _) => { if (fitting) Fit(); };
        scroll.PreviewMouseWheel += (_, e) => { fitting = false; SetScale(scale * (e.Delta > 0 ? 1.2 : 1 / 1.2)); e.Handled = true; };
        System.Windows.Point? drag = null; double left = 0, top = 0;
        image.MouseLeftButtonDown += (_, e) => { drag = e.GetPosition(scroll); left = scroll.HorizontalOffset; top = scroll.VerticalOffset; image.CaptureMouse(); e.Handled = true; };
        image.MouseMove += (_, e) => { if (drag is { } start) { var p = e.GetPosition(scroll); scroll.ScrollToHorizontalOffset(left + start.X - p.X); scroll.ScrollToVerticalOffset(top + start.Y - p.Y); } };
        image.MouseLeftButtonUp += (_, _) => { drag = null; image.ReleaseMouseCapture(); };
        image.LostMouseCapture += (_, _) => drag = null;
        preview.Content = grid; WindowShell.Attach(preview, "查看图片");
        preview.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; preview.Close(); } };
        preview.Closed += (_, _) => { _previews.Remove(preview); image.Source = null; preview.Tag = null; };
        _previews.Add(preview); preview.Show();
    }
}
