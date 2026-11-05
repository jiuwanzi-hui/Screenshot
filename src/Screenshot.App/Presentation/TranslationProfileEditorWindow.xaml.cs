using System.Windows;
using System.Net.Http;
using Screenshot.App.Core;
using Screenshot.App.Text;

namespace Screenshot.App.Presentation;

public partial class TranslationProfileEditorWindow : Window, IDisposable
{
    private readonly AiTranslationProfileItem _item;
    private readonly ITranslationCredentialStore _credentialStore;
    private string? _testedFingerprint;
    private bool? _testedAvailability;
    private string? _testedMessage;
    private readonly CancellationTokenSource _lifetime = new();
    private bool _closed;

    public TranslationProfileEditorWindow(AiTranslationProfileItem item, ITranslationCredentialStore? credentialStore = null)
    {
        InitializeComponent();
        Closed += (_, _) => Dispose();
        Loaded += (_, _) =>
        {
            if (Owner?.Topmost == true) Topmost = true;
            Activate();
            NameBox.Focus();
        };
        _item = item;
        _credentialStore = credentialStore ?? new DpapiTranslationCredentialStore();
        NameBox.Text = item.Name;
        EndpointBox.Text = item.Endpoint;
        ModelBox.Text = item.Model;
        PurposeBox.SelectedValue = item.Purpose.ToString();
        AppIdBox.Text = item.AppId;
        BaiduAdvancedBox.IsChecked = item.Model == "Advanced";
        ProviderBox.ItemsSource = TranslationProviderFactory.ProviderDefinitions
            .Select(provider => new SettingOption(provider.Id, provider.DisplayName))
            .ToArray();
        ProviderBox.SelectedValue = TranslationProviderFactory.ResolveProviderId(item.Provider);
        ApiKeyBox.Password = _credentialStore.GetApiKey(item.Id, item.Provider) ?? string.Empty;
        UpdateProviderFields();
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
            if (string.IsNullOrWhiteSpace(ModelBox.Text) || ModelBox.Text == "NMT") ModelBox.Text = definition.DefaultModel;
            UpdateProviderFields();
        };
    }

    private bool IsPublicTranslation => TranslationProviderFactory.IsPublicProvider(ProviderBox.SelectedValue as string);

    internal void ShowClipboardStatus(string message) => TestStatusText.Text = message;

    public void Dispose()
    {
        if (_closed) return;
        _closed = true;
        _lifetime.Cancel();
        _lifetime.Dispose();
        ModelBox.ItemsSource = null;
        ApiKeyBox.Clear();
        GC.SuppressFinalize(this);
    }

    private void UpdateProviderFields()
    {
        var isPublic = IsPublicTranslation;
        PurposePanel.Visibility = isPublic ? Visibility.Collapsed : Visibility.Visible;
        EndpointBox.IsEnabled = !isPublic;
        if (isPublic) EndpointBox.Text = TranslationProviderFactory.GetDefinition(ProviderBox.SelectedValue as string).OfficialEndpoint;
        else if (EndpointBox.Text is YoudaoTranslationProvider.Endpoint or BaiduTranslationProvider.Endpoint or BaiduTranslationProvider.LargeModelEndpoint)
            EndpointBox.Text = TranslationProviderFactory.GetDefinition(ProviderBox.SelectedValue as string ?? string.Empty).OfficialEndpoint;
        BaiduAdvancedBox.Visibility = ProviderBox.SelectedValue as string == BaiduTranslationProvider.ProviderId ? Visibility.Visible : Visibility.Collapsed;
        ModelLabel.Text = isPublic ? "应用 ID（App Key）" : "模型名称";
        KeyLabel.Text = isPublic && ProviderBox.SelectedValue as string != BaiduTranslationProvider.LargeModelProviderId ? "应用密钥（App Secret）" : "API Key";
        ModelBox.Visibility = isPublic ? Visibility.Collapsed : Visibility.Visible;
        AppIdBox.Visibility = isPublic ? Visibility.Visible : Visibility.Collapsed;
        FetchModelsButton.Visibility = isPublic ? Visibility.Collapsed : Visibility.Visible;
        TestButton.Content = isPublic ? "测试翻译" : "测试模型";
        TestStatusText.Text = ProviderBox.SelectedValue as string == BaiduTranslationProvider.ProviderId
            ? "请在 fanyi-api.baidu.com 开通通用文本翻译。标准版每月 5 万字符、1 次/秒；高级版每月 100 万字符、10 次/秒免费额度，以账户权益为准。"
            : ProviderBox.SelectedValue as string == BaiduTranslationProvider.LargeModelProviderId
            ? "请开通百度“大模型文本翻译”，填写开发者信息中的 APP ID 和“API Key 管理”中创建的 Key。使用大模型 llm，与通用翻译分别配置；额度、费用及服务限额以账户为准。"
            : isPublic
            ? "在 ai.youdao.com 创建应用并绑定“批量文本翻译”。按账户额度计费；可与在线 AI、离线模型一起排序使用。"
            : string.Empty;
    }

    private string PublicModel => ProviderBox.SelectedValue as string == BaiduTranslationProvider.ProviderId
        ? BaiduAdvancedBox.IsChecked == true ? "Advanced" : "Standard"
        : ProviderBox.SelectedValue as string == BaiduTranslationProvider.LargeModelProviderId ? "llm" : "NMT";

    private AiTranslationProfile GetEditedProfile() => _item.ToProfile() with
    {
        Provider = ProviderBox.SelectedValue as string ?? string.Empty,
        Endpoint = EndpointBox.Text.Trim(), Model = IsPublicTranslation ? PublicModel : ModelBox.Text.Trim(),
        AppId = AppIdBox.Text.Trim(),
        Purpose = Enum.TryParse<OnlineModelPurpose>(PurposeBox.SelectedValue as string, out var purpose) ? purpose : OnlineModelPurpose.Automatic,
    };

    public bool HasTestResultForCurrentValues =>
        _testedAvailability.HasValue &&
        string.Equals(_testedFingerprint, CreateFingerprint(), StringComparison.Ordinal);

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (ProviderBox.SelectedValue is not string provider)
        {
            TestStatusText.Text = "请选择服务类型后保存。";
            return;
        }
        _item.Name = string.IsNullOrWhiteSpace(NameBox.Text) ? "在线翻译" : NameBox.Text.Trim();
        _item.Provider = provider;
        _item.Endpoint = EndpointBox.Text.Trim();
        _item.Model = IsPublicTranslation ? PublicModel : ModelBox.Text.Trim();
        _item.AppId = AppIdBox.Text.Trim();
        _item.Purpose = GetEditedProfile().Purpose;
        _credentialStore.SetApiKey(_item.Id, provider, ApiKeyBox.Password);
        if (HasTestResultForCurrentValues)
        {
            _item.SetAvailability(
                _testedAvailability!.Value,
                _testedMessage ?? (_testedAvailability.Value ? "模型可用" : "模型不可用"));
        }
        DialogResult = true;
        Close();
    }

    private async void OnTestClick(object sender, RoutedEventArgs e)
    {
        if (ProviderBox.SelectedValue is not string ||
            string.IsNullOrWhiteSpace(EndpointBox.Text) ||
            string.IsNullOrWhiteSpace(IsPublicTranslation ? AppIdBox.Text : ModelBox.Text) ||
            string.IsNullOrWhiteSpace(ApiKeyBox.Password))
        {
            TestStatusText.Text = IsPublicTranslation ? "请先填写应用 ID 和应用密钥。" : "请先填写接口地址、模型名称和 API Key。";
            RecordTestResult(false, TestStatusText.Text);
            return;
        }

        TestButton.IsEnabled = false;
        TestStatusText.Text = "正在测试模型...";
        var fingerprint = CreateFingerprint();
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
            var result = await TranslationModelCatalogService.TestAsync(
                GetEditedProfile(), ApiKeyBox.Password.Trim(),
                client, _lifetime.Token);
            if (_closed) return;
            if (fingerprint != CreateFingerprint()) { TestStatusText.Text = "配置已更改，请按当前配置重新测试。"; return; }
            TestStatusText.Text = result.Message;
            _testedFingerprint = fingerprint;
            _testedAvailability = result.IsSuccess;
            _testedMessage = result.Message;
        }
        catch (Exception exception)
        {
            if (_closed) return;
            if (fingerprint != CreateFingerprint()) { TestStatusText.Text = "配置已更改，请按当前配置重新测试。"; return; }
            TestStatusText.Text = $"测试失败：{exception.Message}";
            RecordTestResult(false, TestStatusText.Text);
        }
        finally
        {
            if (!_closed) TestButton.IsEnabled = true;
        }
    }

    private string CreateFingerprint()
    {
        return string.Join("\n", [
            ProviderBox.SelectedValue as string ?? string.Empty,
            EndpointBox.Text.Trim(),
            ModelBox.Text.Trim(),
            AppIdBox.Text.Trim(),
            PublicModel,
            PurposeBox.SelectedValue as string ?? "Automatic",
            ApiKeyBox.Password.Trim(),
        ]);
    }

    private void RecordTestResult(bool isAvailable, string message)
    {
        _testedFingerprint = CreateFingerprint();
        _testedAvailability = isAvailable;
        _testedMessage = message;
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
            using var client = new HttpClient();
            var result = await TranslationModelCatalogService.FetchAsync(
                EndpointBox.Text.Trim(),
                ApiKeyBox.Password.Trim(),
                client, _lifetime.Token);
            if (_closed) return;
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
            if (_closed) return;
            TestStatusText.Text = $"获取失败：{exception.Message}";
        }
        finally
        {
            if (!_closed) FetchModelsButton.IsEnabled = true;
        }
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
