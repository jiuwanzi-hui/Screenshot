using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Screenshot.App.Text;

namespace Screenshot.App.Tests;

public sealed class TranslationThroughputTests
{
    [Fact]
    public async Task ShortArticleTitlesShareRequestsWithoutChangingInputOrder()
    {
        var source = Enumerable.Range(1, 24).Select(index => $"Please open document {index}.").ToArray();
        using var handler = new BatchHandler();
        using var client = new HttpClient(handler);
        var provider = new OpenAiCompatibleTranslationProvider("https://example.com/v1", "chosen-model", "key", client);
        var result = await provider.TranslateSegmentsAsync(source, "en", "zh-Hans");
        Assert.Empty(result.FailedSegmentIndices!);
        Assert.Equal(3, handler.Calls);
        Assert.Equal(Enumerable.Range(1, 24).Select(index => $"请打开文档 {index}。"), result.Segments);
    }

    [Theory]
    [InlineData("Vulkan0: NVIDIA GeForce RTX 5070 Ti (15995 MiB, 15227 MiB free)", "Vulkan0", 8)]
    [InlineData("Vulkan0: NVIDIA GeForce GTX 1650 (4096 MiB, 3500 MiB free)", "Vulkan0", 2)]
    [InlineData("Vulkan0: Intel(R) Graphics (36993 MiB, 36225 MiB free)", "Vulkan0", 1)]
    [InlineData("Vulkan0: Intel Arc 140V Graphics (36993 MiB, 36225 MiB free)", "Vulkan0", 1)]
    [InlineData("Vulkan0: Intel(R) Graphics (36993 MiB, 36225 MiB free)\nVulkan1: AMD Radeon RX 6600 (8192 MiB, 7000 MiB free)", "Vulkan1", 4)]
    public void BoundsNativeParallelismToAvailableDiscreteMemory(string listing, string device, int parallelism)
    {
        var result = LocalTranslationInferenceSession.SelectDevice(listing);
        Assert.Equal(device, result!.Id);
        Assert.Equal(parallelism, result.Parallelism);
    }

    [Fact]
    public void MissingNativeDeviceInformationKeepsTheConservativeFallback() =>
        Assert.Null(LocalTranslationInferenceSession.SelectDevice("Available devices:\n"));

    private sealed class BatchHandler : HttpMessageHandler
    {
        internal int Calls;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            Assert.Equal("chosen-model", json.RootElement.GetProperty("model").GetString());
            using var payload = JsonDocument.Parse(json.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!);
            var translations = payload.RootElement.GetProperty("segments").EnumerateArray().Select(segment =>
                "请打开文档 " + new string(segment.GetString()!.Where(char.IsDigit).ToArray()) + "。").ToArray();
            await Task.Yield();
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new
                { choices = new[] { new { message = new { content = JsonSerializer.Serialize(new { translations }) } } } }), Encoding.UTF8, "application/json") };
        }
    }
}
