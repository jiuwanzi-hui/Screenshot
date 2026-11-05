using System.IO;
using Screenshot.App.Core;
using Screenshot.App.Infrastructure;
using Screenshot.App.Presentation;
using Screenshot.App.Text;

namespace Screenshot.App.Tests;

public sealed partial class ImmediateSettingsTests
{
    [Fact]
    public void RuntimeFailureUpdatesSettingsRowAndSurvivesLaterSettingsSave()
    {
        Directory.CreateDirectory(_testDirectory);
        var store = new SettingsStore(Path.Combine(_testDirectory, "health.json"));
        var profile = new AiTranslationProfile { LastAvailability = true, Endpoint = "https://example.test/chat/completions", Model = "test" };
        var initial = CreateSettings() with { TranslationProfiles = [profile] };
        WpfTestHost.Invoke(() =>
        {
            using var hotkeys = new GlobalHotKeyManager();
            var window = new MainWindow(initial, store, new FakeStartupRegistrationService(), hotkeys, new FakeTranslationCredentialStore());
            try
            {
                var observer = TranslationProfileHealth.Observe(profile, "test-key");
                observer.Complete(observer.BeginRequest(), false, "HTTP 429：余额不足");
                window.ApplyTranslationProfileHealth(TranslationProfileHealth.Latest(profile)!);
                var vm = (SettingsViewModel)window.DataContext;
                var row = Assert.Single(vm.TranslationProfiles);
                Assert.Equal("不可用", row.AvailabilityLabel);
                Assert.Contains("余额不足", row.AvailabilityReason);
                store.Save(initial with { TranslationProfiles = [row.ToProfile()] });
                Assert.False(Assert.Single(store.Load().Settings.TranslationProfiles).LastAvailability);
                observer.Complete(observer.BeginRequest(), true, "最近一次翻译请求成功。");
                window.ApplyTranslationProfileHealth(TranslationProfileHealth.Latest(profile)!);
                Assert.Equal("可用", row.AvailabilityLabel);
            }
            finally { window.RequestExit(); TranslationProfileHealth.Invalidate(profile.Id); }
        });
    }
}
