using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Screenshot.App.Core;
using Button = System.Windows.Controls.Button;

namespace Screenshot.App.Text;

public partial class ImageQuestionWindow
{
    private CancellationTokenSource? _modelListRequest;
    private readonly Dictionary<string, IReadOnlyList<string>> _modelCatalogs = [];
    private static string CatalogKey(AiTranslationProfile profile) => profile.Id + "\n" + profile.Provider + "\n" + profile.Endpoint;

    private void OnSpecificModelDropDownOpened(object? sender, EventArgs e)
    {
        if (_modelListRequest is null && ModelPicker.SelectedItem is AiTranslationProfile profile && !_modelCatalogs.ContainsKey(CatalogKey(profile)))
            OnFetchChatModelsClick(FetchChatModelsButton, new RoutedEventArgs());
    }

    private async void OnFetchChatModelsClick(object sender, RoutedEventArgs e)
    {
        if (_closed || ModelPicker.SelectedItem is not AiTranslationProfile profile) return;
        _modelListRequest?.Cancel();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _modelListRequest = cancellation; FetchChatModelsButton.IsEnabled = false;
        StatusText.Text = "正在获取此配置的模型列表…";
        var token = cancellation.Token;
        try
        {
            var result = await Task.Run(async () =>
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
                var key = new DpapiTranslationCredentialStore().GetApiKey(profile.Id, TranslationProviderFactory.ResolveProviderId(profile.Provider));
                return await TranslationModelCatalogService.FetchAsync(profile.Endpoint, key, client, token).ConfigureAwait(false);
            }, token);
            if (_closed || token.IsCancellationRequested) return;
            var current = SpecificModelBox.Text;
            if (result.IsSuccess)
            { _modelCatalogs[CatalogKey(profile)] = result.Models; SpecificModelBox.ItemsSource = result.Models; SpecificModelBox.Text = current; }
            StatusText.Text = result.IsSuccess ? "模型列表已更新，可选择或手动输入具体模型。" : result.ErrorMessage;
        }
        catch (OperationCanceledException) { }
        catch { if (!_closed && !token.IsCancellationRequested) StatusText.Text = "获取模型失败，可手动输入具体模型。"; }
        finally
        {
            if (ReferenceEquals(_modelListRequest, cancellation)) _modelListRequest = null;
            if (!_closed) FetchChatModelsButton.IsEnabled = true;
        }
    }

    private void AddGeneratedImage(StackPanel body, BitmapSource pixels)
    {
        var open = new Button { Content = new System.Windows.Controls.Image { Source = pixels, MaxHeight = 240, Stretch = Stretch.Uniform },
            Padding = new Thickness(4), Margin = new Thickness(0, 8, 0, 0), ToolTip = "点击放大查看" };
        open.Click += (_, _) => ShowImagePreview(pixels); body.Children.Add(open);
        var actions = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        var attach = new Button { Content = "用于下次提问", Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(0, 0, 8, 0) };
        attach.Click += (_, _) => AddImage(pixels);
        var save = new Button { Content = "保存图片", Padding = new Thickness(8, 3, 8, 3) };
        save.Click += async (_, _) =>
        {
            var picker = new Microsoft.Win32.SaveFileDialog { Filter = "PNG 图片|*.png", FileName = "AI图片.png", DefaultExt = ".png" };
            if (picker.ShowDialog(this) != true) return;
            try { var path = picker.FileName; await Task.Run(() => File.WriteAllBytes(path, ImageQuestionService.EncodeImage(pixels))); if (!_closed) StatusText.Text = "图片已保存。"; }
            catch { if (!_closed) StatusText.Text = "图片保存失败，请检查目标目录。"; }
        };
        actions.Children.Add(attach); actions.Children.Add(save); body.Children.Add(actions);
    }
}
