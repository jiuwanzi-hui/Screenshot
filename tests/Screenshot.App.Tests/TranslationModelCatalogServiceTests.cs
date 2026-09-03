using System.Net;
using System.Net.Http;
using System.Text;
using Screenshot.App.Text;

namespace Screenshot.App.Tests;

public sealed class TranslationModelCatalogServiceTests
{
    [Theory]
    [InlineData(
        "https://vendor.example/v1",
        "https://vendor.example/v1/chat/completions",
        "https://vendor.example/v1/models")]
    [InlineData(
        "https://vendor.example/v1/chat/completions",
        "https://vendor.example/v1/chat/completions",
        "https://vendor.example/v1/models")]
    [InlineData(
        "https://vendor.example/openai/v1",
        "https://vendor.example/openai/v1/chat/completions",
        "https://vendor.example/openai/v1/models")]
    [InlineData(
        "https://vendor.example/api/paas/v4",
        "https://vendor.example/api/paas/v4/chat/completions",
        "https://vendor.example/api/paas/v4/models")]
    [InlineData(
        "https://generativelanguage.googleapis.com/v1beta/openai",
        "https://generativelanguage.googleapis.com/v1beta/openai/chat/completions",
        "https://generativelanguage.googleapis.com/v1beta/openai/models")]
    [InlineData(
        "https://api.deepseek.com",
        "https://api.deepseek.com/chat/completions",
        "https://api.deepseek.com/models")]
    public void ResolvesGenericOpenAiCompatibleEndpoints(
        string configuredEndpoint,
        string expectedChatEndpoint,
        string expectedModelsEndpoint)
    {
        Assert.Equal(
            expectedChatEndpoint,
            OpenAiCompatibleEndpointResolver
                .NormalizeChatCompletionsEndpoint(configuredEndpoint)
                .TrimEnd('/'));
        Assert.Equal(
            expectedModelsEndpoint,
            OpenAiCompatibleEndpointResolver
                .CreateModelsEndpoint(configuredEndpoint)?
                .ToString()
                .TrimEnd('/'));
    }

    [Fact]
    public void ListsCustomEndpointFirstAndIncludesMajorCompatibleVendors()
    {
        var definitions = TranslationProviderFactory.ProviderDefinitions;

        Assert.Equal(
            TranslationProviderFactory.OpenAiCompatibleProviderId,
            definitions[0].Id);
        Assert.Equal("自定义兼容接口", definitions[0].DisplayName);
        Assert.Contains(definitions, item => item.Id == "OpenAI");
        Assert.Contains(definitions, item => item.Id == "DeepSeek");
        Assert.Contains(definitions, item => item.Id == "DashScope");
        Assert.Contains(definitions, item => item.Id == "Zhipu");
        Assert.Contains(definitions, item => item.Id == "AnthropicClaude");
        Assert.Contains(definitions, item => item.Id == "GoogleGemini");
        Assert.Contains(definitions, item => item.Id == "XaiGrok");
        Assert.Equal(
            definitions.Count,
            definitions.Select(item => item.Id)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count());
        Assert.All(
            definitions.Skip(1),
            item => Assert.StartsWith("https://", item.OfficialEndpoint));
    }

    [Fact]
    public async Task FetchesModelsUsingTheConfiguredVendorAndApiKey()
    {
        var handler = new ModelListHandler(
            """{"data":[{"id":"vendor-large"},{"id":"vendor-fast"}]}""");
        using var client = new HttpClient(handler);

        var result = await TranslationModelCatalogService.FetchAsync(
            "https://vendor.example/v1",
            "vendor-key",
            client);

        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.Equal(["vendor-fast", "vendor-large"], result.Models);
        Assert.Equal(
            "https://vendor.example/v1/models",
            handler.RequestUri?.ToString().TrimEnd('/'));
        Assert.Equal("Bearer", handler.AuthorizationScheme);
        Assert.Equal("vendor-key", handler.AuthorizationParameter);
    }

    [Fact]
    public async Task ModelFetchFailureIncludesTheVendorMessage()
    {
        var handler = new ModelListHandler(
            """{"error":{"message":"model listing is disabled"}}""",
            HttpStatusCode.BadRequest);
        using var client = new HttpClient(handler);

        var result = await TranslationModelCatalogService.FetchAsync(
            "https://vendor.example/v1",
            "vendor-key",
            client);

        Assert.False(result.IsSuccess);
        Assert.Contains("HTTP 400", result.ErrorMessage);
        Assert.Contains("model listing is disabled", result.ErrorMessage);
    }

    [Fact]
    public async Task FetchesModelsFromNestedVendorResponse()
    {
        var handler = new ModelListHandler(
            """{"data":{"model_list":[{"model_id":"vendor-nested"}]}}""");
        using var client = new HttpClient(handler);

        var result = await TranslationModelCatalogService.FetchAsync(
            "https://vendor.example/v1",
            "vendor-key",
            client);

        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.Equal(["vendor-nested"], result.Models);
    }

    private sealed class ModelListHandler : HttpMessageHandler
    {
        private readonly string _responseBody;
        private readonly HttpStatusCode _statusCode;

        public ModelListHandler(
            string responseBody,
            HttpStatusCode statusCode = HttpStatusCode.OK)
        {
            _responseBody = responseBody;
            _statusCode = statusCode;
        }

        public Uri? RequestUri { get; private set; }

        public string? AuthorizationScheme { get; private set; }

        public string? AuthorizationParameter { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            AuthorizationScheme = request.Headers.Authorization?.Scheme;
            AuthorizationParameter = request.Headers.Authorization?.Parameter;
            return Task.FromResult(new HttpResponseMessage(_statusCode)
            {
                Content = new StringContent(
                    _responseBody,
                    Encoding.UTF8,
                    "application/json"),
            });
        }
    }
}
