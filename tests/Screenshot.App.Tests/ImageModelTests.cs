using System.Net;
using System.Net.Http;
using System.Text.Json;
using Screenshot.App.Core;
using Screenshot.App.Text;
using static Screenshot.App.Tests.ImageQuestionTests;

namespace Screenshot.App.Tests;

public sealed class ImageModelTests
{
    private static byte[] Png() { byte[] png = []; WpfTestHost.Invoke(() => png = ImageQuestionService.EncodeImage(Pixels())); return png; }
    private static HttpResponseMessage Picture(byte[] png) => new(HttpStatusCode.OK)
    { Content = new StringContent(JsonSerializer.Serialize(new { data = new[] { new { b64_json = Convert.ToBase64String(png) } } })) };

    [Theory]
    [InlineData("images-2")]
    [InlineData("images-2.5")]
    [InlineData("gpt-image-2")]
    public async Task ImageOnlyModelTestUsesGenerationAndValidatesActualPixels(string model)
    {
        var png = Png();
        using var client = new HttpClient(new Handler(async (request, token) =>
        {
            Assert.EndsWith("/v1/images/generations", request.RequestUri!.AbsoluteUri);
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            Assert.Equal(model, json.RootElement.GetProperty("model").GetString());
            Assert.False(json.RootElement.TryGetProperty("messages", out _));
            return Picture(png);
        }));
        var profile = Profile() with { Model = model };
        var result = await TranslationModelCatalogService.TestAsync(profile, "test-key", client);
        Assert.True(result.IsSuccess, result.Message); Assert.Contains("图片生成", result.Message);
        Assert.False(OnlineModelCapabilities.CanTranslate(profile));
        Assert.True(OnlineModelCapabilities.CanTranslate(profile with { Purpose = OnlineModelPurpose.TextTranslation }));
    }

    [Fact]
    public async Task MultipartEditsPreserveMultipleImagesModelAndQuality()
    {
        var png = Png();
        using var client = new HttpClient(new Handler(async (request, token) =>
        {
            Assert.Equal("https://example.invalid/proxy/v1/images/edits", request.RequestUri!.AbsoluteUri);
            var parts = Assert.IsType<MultipartFormDataContent>(request.Content).ToArray();
            Assert.Equal("images-2.5", await parts.Single(p => p.Headers.ContentDisposition!.Name!.Trim('"') == "model").ReadAsStringAsync(token));
            Assert.Equal("high", await parts.Single(p => p.Headers.ContentDisposition!.Name!.Trim('"') == "quality").ReadAsStringAsync(token));
            var files = parts.Where(p => p.Headers.ContentDisposition!.FileName is not null).ToArray();
            Assert.Equal(2, files.Length);
            foreach (var file in files) { Assert.Equal("image[]", file.Headers.ContentDisposition!.Name!.Trim('"')); Assert.Equal(png, await file.ReadAsByteArrayAsync(token)); }
            return Picture(png);
        }));
        var profile = Profile() with { Model = "images-2.5", Endpoint = "https://example.invalid/proxy/v1/chat/completions" };
        var reply = await new ImageQuestionService(client, new Credentials()).AskWithImagesAsync(profile, [png, png], [], "改成蓝色", default, "high");
        Assert.True(reply.IsSuccess, reply.Error); Assert.Equal(20, Assert.Single(reply.Images).PixelWidth); Assert.True(reply.Images[0].IsFrozen);
    }

    [Fact]
    public async Task UrlResultDoesNotForwardApiKeyAndInvalidImageDoesNotPassTest()
    {
        var calls = 0;
        using var client = new HttpClient(new Handler((request, _) =>
        {
            if (++calls == 1) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("{\"data\":[{\"url\":\"https://images.example.invalid/result.png\"}]}") });
            Assert.Null(request.Headers.Authorization); Assert.Equal(HttpMethod.Get, request.Method);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) });
        }));
        var result = await TranslationModelCatalogService.TestAsync(Profile() with { Model = "images-2" }, "test-key", client);
        Assert.False(result.IsSuccess); Assert.Equal(2, calls);
    }

    [Fact]
    public async Task ExplicitVisionPurposeTestsAnImageAndChecksRecognition()
    {
        using var client = new HttpClient(new Handler(async (request, token) =>
        {
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            var content = json.RootElement.GetProperty("messages")[1].GetProperty("content");
            Assert.Equal("image_url", content[1].GetProperty("type").GetString());
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"red\"}}]}") };
        }));
        var result = await TranslationModelCatalogService.TestAsync(Profile() with { Purpose = OnlineModelPurpose.ImageQuestion }, "test-key", client);
        Assert.True(result.IsSuccess, result.Message);
    }

    [Fact]
    public async Task ChatStrengthIsExplicitAndTemporaryOverridesDoNotPoisonSavedProfileHealth()
    {
        var profile = Profile();
        TranslationProfileHealth.Observe(profile, "test-key").Complete(1, true, "原配置正常");
        using var client = new HttpClient(new Handler(async (request, token) =>
        {
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            Assert.Equal("other-model", json.RootElement.GetProperty("model").GetString());
            Assert.Equal("high", json.RootElement.GetProperty("reasoning_effort").GetString());
            return new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("{\"error\":{\"message\":\"unsupported reasoning_effort\"}}") };
        }));
        var result = await new ImageQuestionService(client, new Credentials()).AskWithImagesAsync(profile with { Model = "other-model" }, [[1]], [], "问题", default, "high", false);
        Assert.False(result.IsSuccess); Assert.Contains("reasoning_effort", result.Error);
        Assert.True(TranslationProfileHealth.Latest(profile)!.IsAvailable);
        TranslationProfileHealth.Invalidate(profile.Id);
    }

    [Theory]
    [InlineData("https://example.invalid/proxy/v1/images/edits")]
    [InlineData("https://example.invalid/proxy/v1/images/generations")]
    public void ImageEndpointsCanFetchModelsFromSameApiRoot(string endpoint) =>
        Assert.Equal("https://example.invalid/proxy/v1/models", OpenAiCompatibleEndpointResolver.CreateModelsEndpoint(endpoint)!.AbsoluteUri);
}
