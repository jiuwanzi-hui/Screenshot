using System.Windows;
using System.Net.Http;
using Screenshot.App.Core;
using Screenshot.App.Text;

namespace Screenshot.App.Presentation;

public partial class TranslationProfileEditorWindow : Window
{
    private readonly AiTranslationProfileItem _item;
    private readonly ITranslationCredentialStore _credentialStore;

    public TranslationProfileEditorWindow(AiTranslationProfileItem item, ITranslationCredentialStore? credentialStore = null)
    {
        InitializeComponent();
        _item = item;
        _credentialStore = credentialStore ?? new DpapiTranslationCredentialStore();
        NameBox.Text = item.Name;
        EndpointBox.Text = item.Endpoint;
        ModelBox.Text = item.Model;
        ProviderBox.ItemsSource = TranslationProviderFactory.ProviderDefinitions
            .Select(provider => new SettingOption(provider.Id, provider.DisplayName))
            .ToArray();
        ProviderBox.SelectedValue = item.Provider;
        ApiKeyBox.Password = _credentialStore.GetApiKey(item.Id, item.Provider) ?? string.Empty;
        ProviderBox.SelectionChanged += (_, _) =>
        {
            if (ProviderBox.SelectedValue is not string id) return;
            var definition = TranslationProviderFactory.GetDefinition(id);
            // Only fill an endpoint that has never been configured. Custom
            // endpoints must remain attached to this profile when another
            // provider is selected or the editor is reopened.
            if (string.IsNullOrWhiteSpace(EndpointBox.Text))
            {
                EndpointBox.Text = definition.OfficialEndpoint;
            }
            if (string.IsNullOrWhiteSpace(ModelBox.Text)) ModelBox.Text = definition.DefaultModel;
        };
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (ProviderBox.SelectedValue is not string provider) return;
        _item.Name = string.IsNullOrWhiteSpace(NameBox.Text) ? "在线翻译" : NameBox.Text.Trim();
        _item.Provider = provider;
        _item.Endpoint = EndpointBox.Text.Trim();
        _item.Model = ModelBox.Text.Trim();
        _credentialStore.SetApiKey(_item.Id, provider, ApiKeyBox.Password);
        DialogResult = true;
        Close();
    }

    private async void OnTestClick(object sender, RoutedEventArgs e)
    {
        if (ProviderBox.SelectedValue is not string ||
            string.IsNullOrWhiteSpace(EndpointBox.Text) ||
            string.IsNullOrWhiteSpace(ModelBox.Text) ||
            string.IsNullOrWhiteSpace(ApiKeyBox.Password))
        {
            TestStatusText.Text = "请先填写接口地址、模型名称和 API Key。";
            return;
        }

        TestButton.IsEnabled = false;
        TestStatusText.Text = "正在测试模型...";
        try
        {
            var result = await TranslationModelCatalogService.TestAsync(
                EndpointBox.Text.Trim(), ModelBox.Text.Trim(), ApiKeyBox.Password.Trim(),
                new HttpClient());
            TestStatusText.Text = result.IsSuccess
                ? "模型测试成功，可以使用。"
                : result.Message;
        }
        catch (Exception exception)
        {
            TestStatusText.Text = $"测试失败：{exception.Message}";
        }
        finally
        {
            TestButton.IsEnabled = true;
        }
    }

    private async void OnFetchModelsClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(EndpointBox.Text) ||
            string.IsNullOrWhiteSpace(ApiKeyBox.Password))
        {
            TestStatusText.Text = "请先填写接口地址和 API Key。";
            return;
        }

        FetchModelsButton.IsEnabled = false;
        TestStatusText.Text = "正在获取模型列表...";
        try
        {
            var result = await TranslationModelCatalogService.FetchAsync(
                EndpointBox.Text.Trim(),
                ApiKeyBox.Password.Trim(),
                new HttpClient());
            if (!result.IsSuccess)
            {
                TestStatusText.Text = result.ErrorMessage ?? "获取模型失败。";
                return;
            }

            var previousModel = ModelBox.Text.Trim();
            ModelBox.ItemsSource = result.Models;
            if (result.Models.Count == 1)
            {
                ModelBox.Text = result.Models[0];
            }
            else if (result.Models.Contains(previousModel, StringComparer.OrdinalIgnoreCase))
            {
                ModelBox.Text = previousModel;
            }
            else
            {
                ModelBox.Text = string.Empty;
            }

            TestStatusText.Text = result.Models.Count == 1
                ? $"已获取并选择模型：{result.Models[0]}。"
                : $"已获取 {result.Models.Count} 个模型，请在下拉框中选择。";
            ModelBox.IsDropDownOpen = result.Models.Count > 1;
        }
        catch (Exception exception)
        {
            TestStatusText.Text = $"获取失败：{exception.Message}";
        }
        finally
        {
            FetchModelsButton.IsEnabled = true;
        }
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
