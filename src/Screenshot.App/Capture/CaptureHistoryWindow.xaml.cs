using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Screenshot.App.Infrastructure;
using WpfButton = System.Windows.Controls.Button;
using WpfKeyEventArgs = System.Windows.Input.KeyEventArgs;
using WpfTextBox = System.Windows.Controls.TextBox;

namespace Screenshot.App.Capture;

public partial class CaptureHistoryWindow : Window
{
    private readonly CaptureHistoryService _captureHistoryService;
    private readonly VideoHistoryService _videoHistoryService = new();
    private string _saveDirectory;
    private string _videoDirectory;
    private VideoHistorySortMode _videoSortMode = VideoHistorySortMode.NewestFirst;
    private bool _isCommittingVideoName;

    public CaptureHistoryWindow(
        CaptureHistoryService historyService,
        string? saveDirectory = null,
        string? videoDirectory = null,
        int screenshotRetentionDays = 7,
        int screenshotLimit = 50,
        int videoRetentionDays = 7,
        int videoLimit = 50)
    {
        ArgumentNullException.ThrowIfNull(historyService);

        _captureHistoryService = historyService;
        _saveDirectory = ResolveDirectory(
            saveDirectory,
            Core.AppMetadata.DefaultCaptureDirectory);
        _videoDirectory = ResolveDirectory(
            videoDirectory,
            Core.AppMetadata.DefaultVideoDirectory);

        InitializeComponent();
        WindowPlacementService.Track(this, WindowPlacementKeys.CaptureHistory);
        DataContext = this;
        VideoSortComboBox.SelectionChanged += OnVideoSortSelectionChanged;
        _captureHistoryService.Items.CollectionChanged += OnHistoryItemsChanged;
        _videoHistoryService.Items.CollectionChanged += OnHistoryItemsChanged;
        RefreshVideoHistory(updateStatus: false);
        UpdateRetentionPolicy(
            screenshotRetentionDays,
            screenshotLimit,
            videoRetentionDays,
            videoLimit);
        UpdateEmptyStates();
        ShowHistorySection(showVideo: false, updateStatus: false);
    }

    public ObservableCollection<CaptureHistoryItem> ScreenshotItems =>
        _captureHistoryService.Items;

    public ObservableCollection<VideoHistoryItem> VideoItems =>
        _videoHistoryService.Items;

    public void UpdateDirectories(
        string? saveDirectory,
        string? videoDirectory)
    {
        _saveDirectory = ResolveDirectory(
            saveDirectory,
            Core.AppMetadata.DefaultCaptureDirectory);
        var nextVideoDirectory = ResolveDirectory(
            videoDirectory,
            Core.AppMetadata.DefaultVideoDirectory);
        if (string.Equals(
                _videoDirectory,
                nextVideoDirectory,
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _videoDirectory = nextVideoDirectory;
        RefreshVideoHistory(updateStatus: false);
    }

    public void UpdateRetentionPolicy(
        int screenshotRetentionDays,
        int screenshotLimit,
        int videoRetentionDays,
        int videoLimit)
    {
        ScreenshotHistoryScopeText.Text =
            $"{FormatRetentionDays(screenshotRetentionDays)} · 最多 {screenshotLimit} 张";
        HistoryStatusText.Text =
            $"截图：{FormatRetentionDays(screenshotRetentionDays)} / {screenshotLimit} 张；" +
            $"录屏：{FormatRetentionDays(videoRetentionDays)} / {videoLimit} 个。";
    }

    public void ShowVideoHistory()
    {
        VideoHistoryTab.IsChecked = true;
        ShowHistorySection(showVideo: true, updateStatus: false);
    }

    private static string FormatRetentionDays(int days)
    {
        return days <= 0 ? "全部保留" : $"保留 {days} 天";
    }

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        if (VideoHistoryTab.IsChecked == true)
        {
            RefreshVideoHistory(updateStatus: false);
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _captureHistoryService.Items.CollectionChanged -= OnHistoryItemsChanged;
        _videoHistoryService.Items.CollectionChanged -= OnHistoryItemsChanged;
        VideoSortComboBox.SelectionChanged -= OnVideoSortSelectionChanged;
        base.OnClosed(e);
    }

    private static string ResolveDirectory(string? directory, string fallback)
    {
        return string.IsNullOrWhiteSpace(directory) ? fallback : directory;
    }

    private void OnHistoryItemsChanged(
        object? sender,
        NotifyCollectionChangedEventArgs e)
    {
        UpdateEmptyStates();
    }

    private void UpdateEmptyStates()
    {
        ScreenshotEmptyText.Visibility = ScreenshotItems.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        VideoEmptyText.Visibility = VideoItems.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        ClearScreenshotHistoryButton.IsEnabled = ScreenshotItems.Count > 0;
    }

    private void OnScreenshotHistoryTabChecked(
        object sender,
        RoutedEventArgs e)
    {
        ShowHistorySection(showVideo: false, updateStatus: false);
    }

    private void OnVideoHistoryTabChecked(
        object sender,
        RoutedEventArgs e)
    {
        ShowHistorySection(showVideo: true, updateStatus: true);
    }

    private void ShowHistorySection(bool showVideo, bool updateStatus)
    {
        if (ScreenshotHistoryPanel is null || VideoHistoryPanel is null)
        {
            return;
        }

        ScreenshotHistoryPanel.Visibility = showVideo
            ? Visibility.Collapsed
            : Visibility.Visible;
        VideoHistoryPanel.Visibility = showVideo
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (showVideo)
        {
            RefreshVideoHistory(updateStatus);
        }
    }

    private void OnViewHistoryItemClick(object sender, RoutedEventArgs e)
    {
        if (sender is not WpfButton { Tag: CaptureHistoryItem item })
        {
            return;
        }

        try
        {
            var preview = new CapturePreviewWindow(
                item.CreateCapturedImage(),
                _saveDirectory,
                item);
            preview.ConfigureForHistoryView();
            preview.Show();
            HistoryStatusText.Text = "已打开完整截图。";
        }
        catch
        {
            HistoryStatusText.Text = "无法打开这张历史截图。";
        }
    }

    private async void OnCopyHistoryItemClick(object sender, RoutedEventArgs e)
    {
        if (sender is not WpfButton { Tag: CaptureHistoryItem item })
        {
            return;
        }

        try
        {
            using var image = item.CreateCapturedImage();
            await ClipboardImageService.SetImageAsync(image.Preview);
            item.MarkCopied();
            HistoryStatusText.Text = "已复制完整截图到剪贴板。";
        }
        catch
        {
            HistoryStatusText.Text = "复制失败，剪贴板可能正被其他程序使用。";
        }
    }

    private void OnDeleteHistoryItemClick(object sender, RoutedEventArgs e)
    {
        if (sender is not WpfButton { Tag: CaptureHistoryItem item })
        {
            return;
        }

        HistoryStatusText.Text = _captureHistoryService.Remove(item)
            ? "已删除这张截图。"
            : "这张截图已经不在历史中。";
    }

    private void OnClearScreenshotHistoryClick(object sender, RoutedEventArgs e)
    {
        var count = ScreenshotItems.Count;
        _captureHistoryService.Clear();
        HistoryStatusText.Text = count == 0
            ? "截图历史已经为空。"
            : $"已清空 {count} 张截图。";
    }

    private void OnRefreshVideoHistoryClick(object sender, RoutedEventArgs e)
    {
        RefreshVideoHistory(updateStatus: true);
    }

    private void OnVideoSortSelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (VideoSortComboBox.SelectedItem is not ComboBoxItem
            {
                Tag: VideoHistorySortMode sortMode,
            })
        {
            return;
        }

        _videoSortMode = sortMode;
        RefreshVideoHistory(updateStatus: true);
    }

    private void OnOpenVideoDirectoryClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(_videoDirectory);
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                UseShellExecute = true,
                ArgumentList = { _videoDirectory },
            });
            HistoryStatusText.Text = "已打开视频保存目录。";
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or System.ComponentModel.Win32Exception)
        {
            HistoryStatusText.Text = $"无法打开视频目录：{exception.Message}";
        }
    }

    private void RefreshVideoHistory(bool updateStatus)
    {
        _videoHistoryService.Refresh(_videoDirectory, _videoSortMode);
        if (updateStatus)
        {
            HistoryStatusText.Text = VideoItems.Count == 0
                ? "视频保存目录中暂无录屏。"
                : $"已读取 {VideoItems.Count} 个录屏文件。";
        }
    }

    private void OnViewVideoHistoryItemClick(object sender, RoutedEventArgs e)
    {
        if (sender is not WpfButton { Tag: VideoHistoryItem item })
        {
            return;
        }

        try
        {
            if (!File.Exists(item.FilePath))
            {
                RefreshVideoHistory(updateStatus: false);
                HistoryStatusText.Text = "录屏文件已被移动或删除。";
                return;
            }

            Process.Start(new ProcessStartInfo(item.FilePath)
            {
                UseShellExecute = true,
            });
            HistoryStatusText.Text = "已使用默认视频播放器打开。";
        }
        catch (Exception exception)
        {
            HistoryStatusText.Text = $"无法打开录屏：{exception.Message}";
        }
    }

    private void OnVideoNameMouseLeftButtonDown(
        object sender,
        MouseButtonEventArgs e)
    {
        if (sender is not TextBlock
            {
                Tag: VideoHistoryItem item,
                Parent: Grid container,
            })
        {
            return;
        }

        var editor = container.Children
            .OfType<WpfTextBox>()
            .FirstOrDefault();
        if (editor is null)
        {
            return;
        }

        editor.Text = Path.GetFileNameWithoutExtension(item.FileName);
        ((TextBlock)sender).Visibility = Visibility.Collapsed;
        editor.Visibility = Visibility.Visible;
        editor.Focus();
        editor.SelectAll();
        e.Handled = true;
    }

    private void OnVideoNameEditorKeyDown(object sender, WpfKeyEventArgs e)
    {
        if (sender is not WpfTextBox editor)
        {
            return;
        }

        if (e.Key == Key.Enter)
        {
            CommitVideoNameEdit(editor);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            CancelVideoNameEdit(editor);
            e.Handled = true;
        }
    }

    private void OnVideoNameEditorLostKeyboardFocus(
        object sender,
        KeyboardFocusChangedEventArgs e)
    {
        if (sender is WpfTextBox editor)
        {
            CommitVideoNameEdit(editor);
        }
    }

    private void CommitVideoNameEdit(WpfTextBox editor)
    {
        if (_isCommittingVideoName ||
            editor.Visibility != Visibility.Visible ||
            editor.Tag is not VideoHistoryItem item)
        {
            return;
        }

        _isCommittingVideoName = true;
        try
        {
            var renamedPath = VideoHistoryService.Rename(
                item,
                editor.Text);
            EndVideoNameEdit(editor);
            if (!string.Equals(
                    item.FilePath,
                    renamedPath,
                    StringComparison.OrdinalIgnoreCase))
            {
                RefreshVideoHistory(updateStatus: false);
            }

            HistoryStatusText.Text =
                $"已重命名为 {Path.GetFileName(renamedPath)}。";
        }
        catch (ArgumentException exception)
        {
            HistoryStatusText.Text = exception.Message;
            RefocusVideoNameEditor(editor);
        }
        catch (FileNotFoundException)
        {
            EndVideoNameEdit(editor);
            RefreshVideoHistory(updateStatus: false);
            HistoryStatusText.Text = "录屏文件已被移动或删除。";
        }
        catch (IOException exception)
        {
            HistoryStatusText.Text = $"无法重命名录屏：{exception.Message}";
            RefocusVideoNameEditor(editor);
        }
        catch (UnauthorizedAccessException)
        {
            HistoryStatusText.Text = "无法重命名录屏，请检查文件权限。";
            RefocusVideoNameEditor(editor);
        }
        finally
        {
            _isCommittingVideoName = false;
        }
    }

    private void CancelVideoNameEdit(WpfTextBox editor)
    {
        if (_isCommittingVideoName)
        {
            return;
        }

        _isCommittingVideoName = true;
        try
        {
            EndVideoNameEdit(editor);
            HistoryStatusText.Text = "已取消重命名。";
        }
        finally
        {
            _isCommittingVideoName = false;
        }
    }

    private static void EndVideoNameEdit(WpfTextBox editor)
    {
        editor.Visibility = Visibility.Collapsed;
        if (editor.Parent is not Grid container)
        {
            return;
        }

        var label = container.Children
            .OfType<TextBlock>()
            .FirstOrDefault();
        if (label is not null)
        {
            label.Visibility = Visibility.Visible;
        }
    }

    private void RefocusVideoNameEditor(WpfTextBox editor)
    {
        _ = Dispatcher.BeginInvoke(() =>
        {
            if (editor.IsVisible)
            {
                editor.Focus();
                editor.SelectAll();
            }
        });
    }

    private void OnDeleteVideoHistoryItemClick(object sender, RoutedEventArgs e)
    {
        if (sender is not WpfButton { Tag: VideoHistoryItem item })
        {
            return;
        }

        try
        {
            _ = _videoHistoryService.Delete(item);
            HistoryStatusText.Text = "已删除本地录屏文件。";
        }
        catch (IOException)
        {
            HistoryStatusText.Text = "无法删除录屏，文件可能正在播放或被其他程序占用。";
        }
        catch (UnauthorizedAccessException)
        {
            HistoryStatusText.Text = "无法删除录屏，请检查文件权限。";
        }
    }
}
