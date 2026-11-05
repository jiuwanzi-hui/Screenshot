using System.Net;
using System.Net.Http;
using System.Text;
using Screenshot.App.Core;
using Screenshot.App.Presentation;
using Screenshot.App.Text;

namespace Screenshot.App.Tests;

public sealed class TranslationProfileHealthTests
{
    [Fact]
    public async Task RealRequestQuotaFailureUpdatesOnlyItsProfileAndSuccessRecovers()
    {
        var profile = new AiTranslationProfile { Endpoint = "https://example.test/chat/completions", Model = "test", LastAvailability = true };
        var other = profile with { Id = Guid.NewGuid().ToString("N") };
        var requests = 0;
        using var client = new HttpClient(new Handler(_ => Task.FromResult(++requests == 1
            ? new HttpResponseMessage((HttpStatusCode)429) { Content = new StringContent("{\"error\":{\"message\":\"余额不足或无可用资源包\"}}", Encoding.UTF8, "application/json") }
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"请保存文件。\"}}]}", Encoding.UTF8, "application/json") })));
        var updates = new List<TranslationProfileHealthUpdate>();
        void Changed(TranslationProfileHealthUpdate update) { if (update.Profile.Id == profile.Id) updates.Add(update); }
        TranslationProfileHealth.Changed += Changed;
        try
        {
            var provider = TranslationProviderFactory.CreateOnlineProvider(profile, "test-secret", client);
            var failure = await provider.TranslateAsync("Please save the file.", "en", "zh-Hans");
            Assert.False(failure.IsSuccess);
            var failed = TranslationProfileHealth.Latest(profile)!;
            Assert.False(failed.IsAvailable);
            Assert.Contains("429", failed.Reason);
            Assert.Contains("余额不足", failed.Reason);
            Assert.Null(TranslationProfileHealth.Latest(other));
            var row = new AiTranslationProfileItem(profile);
            row.SetAvailability(failed.IsAvailable, failed.Reason);
            Assert.Equal("不可用", row.AvailabilityLabel);
            Assert.False(row.ToProfile().LastAvailability);
            Assert.Equal(failed.Reason, row.ToProfile().LastAvailabilityReason);
            var success = await provider.TranslateAsync("Please save the file.", "en", "zh-Hans");
            Assert.True(success.IsSuccess, success.ErrorMessage);
            var recovered = TranslationProfileHealth.Latest(profile)!;
            Assert.True(recovered.IsAvailable);
            row.SetAvailability(recovered.IsAvailable, recovered.Reason);
            Assert.Equal("可用", row.AvailabilityLabel);
            Assert.Equal(2, updates.Count);
        }
        finally { TranslationProfileHealth.Changed -= Changed; TranslationProfileHealth.Invalidate(profile.Id); }
    }

    [Fact]
    public void LateRequestsOldCredentialsAndRemovedProfilesCannotOverwriteCurrentStatus()
    {
        var profile = new AiTranslationProfile { Endpoint = "https://example.test/chat/completions", Model = "test" };
        try
        {
            var observer = TranslationProfileHealth.Observe(profile, "old-key");
            var old = observer.BeginRequest();
            observer.Complete(observer.BeginRequest(), false, "余额不足");
            observer.Complete(old, true, "旧请求成功");
            Assert.False(TranslationProfileHealth.Latest(profile)!.IsAvailable);
            var replacement = TranslationProfileHealth.Observe(profile, "new-key");
            Assert.Null(TranslationProfileHealth.Latest(profile));
            observer.Complete(observer.BeginRequest(), false, "旧密钥无效");
            Assert.Null(TranslationProfileHealth.Latest(profile));
            replacement.Complete(replacement.BeginRequest(), true, "新密钥成功");
            Assert.True(TranslationProfileHealth.Latest(profile)!.IsAvailable);
            Assert.Null(TranslationProfileHealth.Latest(profile with { Model = "another" }));
            TranslationProfileHealth.Invalidate(profile.Id);
            replacement.Complete(replacement.BeginRequest(), false, "迟到结果");
            Assert.Null(TranslationProfileHealth.Latest(profile));
        }
        finally { TranslationProfileHealth.Invalidate(profile.Id); }
    }

    [Fact]
    public async Task UserCancellationDoesNotMarkAServiceUnavailable()
    {
        var profile = new AiTranslationProfile { Endpoint = "https://example.test/chat/completions", Model = "test" };
        using var cancelled = new CancellationTokenSource();
        using var client = new HttpClient(new Handler(async token =>
        {
            cancelled.Cancel();
            await Task.Delay(Timeout.Infinite, token);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }));
        try
        {
            var provider = TranslationProviderFactory.CreateOnlineProvider(profile, "test-secret", client);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.TranslateAsync("Please save the file.", "en", "zh", cancelled.Token));
            Assert.Null(TranslationProfileHealth.Latest(profile));
        }
        finally { TranslationProfileHealth.Invalidate(profile.Id); }
    }

    private sealed class Handler(Func<CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(cancellationToken);
    }
}
