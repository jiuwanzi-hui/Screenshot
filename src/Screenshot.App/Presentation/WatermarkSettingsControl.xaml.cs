using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Screenshot.App.Core;
using Screenshot.App.Watermarks;
using Button = System.Windows.Controls.Button;
using Image = System.Windows.Controls.Image;
using RadioButton = System.Windows.Controls.RadioButton;
using UserControl = System.Windows.Controls.UserControl;
using Orientation = System.Windows.Controls.Orientation;

namespace Screenshot.App.Presentation;

public partial class WatermarkSettingsControl : UserControl, IDisposable
{
    private readonly WatermarkImageStore _store = new();
    private readonly WatermarkPreview _preview = new();
    private readonly Dictionary<string, BitmapSource> _thumbnails = [];
    private readonly Action<WatermarkSettings> _save;
    private CancellationTokenSource? _lifetime;
    private CancellationTokenSource? _locationRequest;
    private WatermarkSettings _settings;
    private string? _location;
    private readonly DateTimeOffset _timestamp = DateTimeOffset.Now;
    private bool _ready;
    private bool _disposed;
    private bool _importing;
    private readonly Func<WatermarkLocationDisplay, CancellationToken, Task<WatermarkLocationResult>> _readLocation;

    public WatermarkSettingsControl(WatermarkSettings settings, Action<WatermarkSettings> save)
        : this(settings, save, WatermarkLocationService.ReadAsync) { }

    internal WatermarkSettingsControl(WatermarkSettings settings, Action<WatermarkSettings> save,
        Func<WatermarkLocationDisplay, CancellationToken, Task<WatermarkLocationResult>> readLocation)
    {
        _readLocation = readLocation;
        _settings = settings.Normalize();
        _save = save;
        InitializeComponent();
        PreviewHost.Content = _preview;
        AutoApply.IsChecked = _settings.AutoApply;
        WatermarkText.Text = _settings.Text;
        IncludeTime.IsChecked = _settings.IncludeTime;
        TimeFormat.ItemsSource = WatermarkSettings.TimeFormats;
        TimeFormat.SelectedItem = _settings.TimeFormat;
        IncludeLocation.IsChecked = _settings.IncludeLocation;
        LocationMode.SelectedIndex = _settings.UseSystemLocation ? (int)_settings.LocationDisplay : 2;
        ManualLocation.Text = _settings.ManualLocation;
        ElementSelector.SelectedIndex = (int)_preview.SelectedElement;
        WatermarkOpacity.Value = _settings.Opacity * 100;
        DarkText.IsChecked = _settings.DarkText;
        _ready = true;
        _preview.PlacementChanged += OnPlacementChanged;
        _preview.SelectionChanged += element => { ElementSelector.SelectedIndex = (int)element; UpdateElementHint(); };
        IsVisibleChanged += OnVisibilityChanged;
        Unloaded += OnUnloaded;
        UpdateEnabledState();
    }

    private async void OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_disposed) return;
        if (!IsVisible) { ReleasePreview(); return; }
        _lifetime ??= new CancellationTokenSource();
        RefreshPreview();
        await LoadThumbnailsAsync(_lifetime.Token);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => ReleasePreview();

    private async Task LoadThumbnailsAsync(CancellationToken cancellationToken)
    {
        try
        {
            foreach (var asset in _settings.Images)
            {
                if (_thumbnails.ContainsKey(asset.Id)) continue;
                try
                {
                    var thumbnail = await _store.LoadAsync(asset.Id, true, cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (thumbnail is not null && _settings.Images.Any(image => image.Id == asset.Id)) _thumbnails[asset.Id] = thumbnail;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception)
                {
                    if (_settings.Images.Any(image => image.Id == asset.Id))
                        Status.Text = $"图片“{asset.Name}”无法读取，请删除后重新导入。";
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            RebuildImageSlots();
            RefreshPreview();
        }
        catch (OperationCanceledException) { }
    }

    private void RebuildImageSlots()
    {
        ImageSlots.Children.Clear();
        foreach (var asset in _settings.Images)
        {
            var content = new StackPanel { Width = 138 };
            content.Children.Add(new Image { Source = _thumbnails.GetValueOrDefault(asset.Id), Height = 76, Stretch = Stretch.Uniform });
            content.Children.Add(new TextBlock { Text = asset.Name, Tag = "UserContent", TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 6, 0, 0) });
            var select = new RadioButton { Content = content, GroupName = "WatermarkImage", IsChecked = _settings.SelectedImageId == asset.Id, Padding = new Thickness(10), Margin = new Thickness(0), ToolTip = asset.Name };
            select.SetResourceReference(StyleProperty, "ThemeCardButton");
            select.Checked += (_, _) => Save(_settings with { SelectedImageId = asset.Id });
            var remove = new Button { Content = "删除", Margin = new Thickness(0, 6, 0, 0) };
            remove.SetResourceReference(StyleProperty, "SecondaryButton");
            remove.Click += async (_, _) =>
            {
                Save(_settings with { Images = _settings.Images.Where(image => image.Id != asset.Id).ToArray() });
                _thumbnails.Remove(asset.Id);
                RebuildImageSlots();
                try { await Task.Run(() => _store.Delete(asset.Id)); }
                catch (IOException) { if (!_disposed) Status.Text = "预设已移除，旧图片文件暂时无法删除。"; }
                catch (UnauthorizedAccessException) { if (!_disposed) Status.Text = "预设已移除，旧图片文件暂时无法删除。"; }
            };
            var slot = new StackPanel { Width = 160, Margin = new Thickness(0, 0, 12, 8) };
            slot.Children.Add(select);
            slot.Children.Add(remove);
            ImageSlots.Children.Add(slot);
        }
        if (_settings.Images.Length == 0)
            ImageSlots.Children.Add(new TextBlock { Text = "尚未导入图片，可以仅使用文字、时间和位置。", TextWrapping = TextWrapping.Wrap, Opacity = .75 });
        ImportButton.IsEnabled = !_importing && _settings.Images.Length < 3;
    }

    private async void OnImportClick(object sender, RoutedEventArgs e)
    {
        if (_importing || _settings.Images.Length >= 3 || _lifetime is null) return;
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = UiLocalization.Get("导入水印图片"),
            Filter = UiLocalization.Get("水印图片") + "|*.png;*.jpg;*.jpeg;*.bmp", Multiselect = false };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        var cancellationToken = _lifetime.Token;
        _importing = true;
        ImportButton.IsEnabled = false;
        Status.Text = "正在导入水印图片…";
        WatermarkImage? imported = null;
        try
        {
            imported = await _store.ImportAsync(dialog.FileName, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            Save(_settings with { Images = [.. _settings.Images, imported], SelectedImageId = imported.Id });
            imported = null; // The settings now own this local copy.
            await LoadThumbnailsAsync(cancellationToken);
            if (!cancellationToken.IsCancellationRequested) Status.Text = "图片已导入；可在下方预览中调整位置与大小。";
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { if (!cancellationToken.IsCancellationRequested) Status.Text = "导入失败：" + exception.Message; }
        finally
        {
            if (imported is not null)
            {
                var orphanId = imported.Id;
                try { await Task.Run(() => _store.Delete(orphanId)); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
            _importing = false;
            if (!_disposed) ImportButton.IsEnabled = _settings.Images.Length < 3;
        }
    }

    private async void OnLocateClick(object sender, RoutedEventArgs e) => await LocateAsync();

    internal async Task LocateAsync()
    {
        if (_lifetime is null) return;
        if (LocationMode.SelectedIndex == 2) LocationMode.SelectedIndex = (int)_settings.LocationDisplay;
        CancelLocation();
        using var request = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _locationRequest = request;
        LocateButton.IsEnabled = false;
        Status.Text = _settings.LocationDisplay == WatermarkLocationDisplay.PlaceName ? "正在读取系统定位并查询城市／地区…" : "正在读取系统经纬度…";
        try
        {
            var result = await _readLocation(_settings.LocationDisplay, request.Token);
            request.Token.ThrowIfCancellationRequested();
            _location = result.Location;
            Status.Text = result.Message;
            RefreshPreview();
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (ReferenceEquals(_locationRequest, request)) _locationRequest = null;
            if (!_disposed) UpdateEnabledState();
        }
    }

    private void OnEdited(object sender, RoutedEventArgs e) => ReadEditors();
    private void OnTextEdited(object sender, TextChangedEventArgs e) => ReadEditors();
    private void OnManualLocationEdited(object sender, TextChangedEventArgs e)
    {
        if (!_ready || _disposed) return;
        // Manual input is an explicit source choice. Cancel before publishing so a
        // late system/network result cannot replace text the user is now entering.
        CancelLocation();
        _location = null;
        _ready = false;
        LocationMode.SelectedIndex = 2;
        _ready = true;
        ReadEditors();
        Status.Text = "已使用手填地点，预览实时更新。可切换显示方式后重新获取当前位置。";
    }
    private void OnLocationModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _disposed) return;
        CancelLocation();
        _location = null;
        ReadEditors();
        Status.Text = LocationMode.SelectedIndex == 2 ? "使用手填地点。" : "点击“获取当前位置”预览；截图时按需读取所选位置。";
    }
    private void OnFormatChanged(object sender, SelectionChangedEventArgs e) => ReadEditors();
    private void OnSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => ReadEditors();
    private void ReadEditors()
    {
        if (!_ready || _disposed) return;
        Save(_settings with
        {
            AutoApply = AutoApply.IsChecked == true, Text = WatermarkText.Text,
            IncludeTime = IncludeTime.IsChecked == true, TimeFormat = TimeFormat.SelectedItem as string ?? WatermarkSettings.TimeFormats[0],
            IncludeLocation = IncludeLocation.IsChecked == true, UseSystemLocation = LocationMode.SelectedIndex != 2,
            LocationDisplay = LocationMode.SelectedIndex == 2 ? _settings.LocationDisplay : (WatermarkLocationDisplay)LocationMode.SelectedIndex,
            ManualLocation = ManualLocation.Text, Opacity = WatermarkOpacity.Value / 100,
            DarkText = DarkText.IsChecked == true,
        });
        if (!_settings.IncludeLocation || !_settings.UseSystemLocation) { CancelLocation(); _location = null; }
        UpdateEnabledState();
    }

    private void UpdateEnabledState()
    {
        LocationOptions.IsEnabled = _settings.IncludeLocation;
        LocateButton.IsEnabled = _settings.IncludeLocation && _locationRequest is null;
        TimeFormat.IsEnabled = _settings.IncludeTime;
        OpacityLabel.Text = $"{_settings.Opacity:P0}";
    }

    private void Save(WatermarkSettings value)
    {
        _settings = value.Normalize();
        _save(_settings);
        RefreshPreview();
    }

    private void RefreshPreview()
    {
        if (!IsVisible || _disposed) return;
        _preview.Placement = _settings;
        _preview.Composition = new WatermarkComposition(_settings, _timestamp, _location, _thumbnails.GetValueOrDefault(_settings.SelectedImageId));
        _preview.InvalidateVisual();
        UpdateElementHint();
    }

    private void OnPlacementChanged(WatermarkSettings value)
    {
        // Position-only edits reuse immutable glyphs and pixels while dragging.
        _settings = value.Normalize();
        _save(_settings);
    }

    private void OnElementChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || ElementSelector.SelectedIndex < 0) return;
        _preview.SelectElement((WatermarkElement)ElementSelector.SelectedIndex);
        UpdateElementHint();
    }

    private void UpdateElementHint() => ElementHint.Text = _preview.Composition?.Parts.Any(part => part.Element == _preview.SelectedElement) == true
        ? "在预览中拖动或拉动右下角" : "添加或启用该内容后即可调整";

    private void OnClearImageClick(object sender, RoutedEventArgs e) { Save(_settings with { SelectedImageId = string.Empty }); RebuildImageSlots(); }
    private void OnResetPositionClick(object sender, RoutedEventArgs e) => Save(_settings.WithPlacement(_preview.SelectedElement, WatermarkPlacement.Default(_preview.SelectedElement)));
    private void CancelLocation() { _locationRequest?.Cancel(); _locationRequest = null; }

    private void ReleasePreview()
    {
        CancelLocation();
        _lifetime?.Cancel();
        _lifetime?.Dispose();
        _lifetime = null;
        _location = null;
        _thumbnails.Clear();
        ImageSlots.Children.Clear();
        _preview.ClearContent();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _preview.PlacementChanged -= OnPlacementChanged;
        IsVisibleChanged -= OnVisibilityChanged;
        Unloaded -= OnUnloaded;
        ReleasePreview();
        GC.SuppressFinalize(this);
    }
}
