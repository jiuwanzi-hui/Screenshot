using Screenshot.App.Core;
using Screenshot.App.Infrastructure;
using Screenshot.App.Text.Realtime;

namespace Screenshot.App;

public partial class App
{
    private RealtimeTranslationController? _realtimeTranslationWindow;

    private void OnRealtimeTranslationRequested(object? sender, EventArgs args)
    {
        var target = RealtimeWindowTarget.Foreground;
        if (_featureHost is not null)
        {
            _ = SendFeatureAsync(new("command") { Command = "realtime", HotKey = HotKeyAction.RealtimeTranslation, TargetWindow = target.ToInt64() });
            return;
        }
        _ = ToggleRealtimeTranslationAsync(target);
    }

    private async Task ToggleRealtimeTranslationAsync(nint target, Capture.ScreenRegion? region = null)
    {
        if (_realtimeTranslationWindow is { } previous) { await previous.DisposeAsync(); return; }
        if (_isShuttingDown || _isCaptureInProgress || _translationHttpClient is null) return;
        _idleEfficiency?.Wake();
        // A visible settings page may itself be the selected content. Hiding it
        // here changes the live pixels after the user has selected the region.
        var window = new RealtimeTranslationController(() => _currentSettings, _translationHttpClient);
        _realtimeTranslationWindow = window;
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_realtimeTranslationWindow, window)) _realtimeTranslationWindow = null;
            _featureWorker?.ScheduleIdle();
        };
        try { await window.StartAsync(target, region); }
        catch (Exception exception)
        {
            window.Close();
            if (!_isShuttingDown) await Capture.StartupFeedbackWindow.ShowAsync("无法开始实时翻译：" + exception.Message);
        }
    }

    private void RefreshRealtimeTranslationSettings(AppSettings before, AppSettings after)
    {
        if (before.RealtimeTranslation.Glossary != after.RealtimeTranslation.Glossary ||
            before.RealtimeTranslation.GlossaryLanguage != after.RealtimeTranslation.GlossaryLanguage || before.OcrEngine != after.OcrEngine ||
            before.OcrLanguageTag != after.OcrLanguageTag || before.TranslationTargetLanguage != after.TranslationTargetLanguage ||
            before.TranslationMode != after.TranslationMode || before.TranslationProvider != after.TranslationProvider ||
            before.TranslationEndpoint != after.TranslationEndpoint || before.TranslationModel != after.TranslationModel ||
            before.SendTextToOnlineTranslation != after.SendTextToOnlineTranslation ||
            before.OfflineTranslationEngine != after.OfflineTranslationEngine || before.OfflineTranslationQuality != after.OfflineTranslationQuality ||
            !before.TranslationProfiles.SequenceEqual(after.TranslationProfiles) ||
            !before.TranslationProviderPriority.SequenceEqual(after.TranslationProviderPriority))
            _realtimeTranslationWindow?.SettingsChanged();
        else if (before.RealtimeTranslation != after.RealtimeTranslation || before.Theme != after.Theme)
            _realtimeTranslationWindow?.RefreshOverlay();
    }
}
