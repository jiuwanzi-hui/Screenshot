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
