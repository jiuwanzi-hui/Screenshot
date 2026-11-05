using Screenshot.App.Core;

namespace Screenshot.App.Text;

// Owns only work started by this translate action. Disposing after blank OCR,
// cancellation, or already-target text releases an unused native model promptly.
internal sealed class TranslationPreparation : IDisposable
{
    private readonly LocalTranslationInferenceSession _session;
    private readonly CancellationTokenSource _cancellation;
    private readonly Task _preparation;
    private readonly Guid _owner = Guid.NewGuid();
    private int _disposed;

    private TranslationPreparation(LocalTranslationInferenceSession session, CancellationToken token)
    {
        _session = session;
        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        _preparation = Task.Run(() => session.PrepareDuringRecognitionAsync(_owner, _cancellation.Token), _cancellation.Token);
    }

    internal static bool IsEligible(AppSettings settings) =>
        settings.ResolveTranslationProviderPriority()[0] == TranslationProviderKind.Offline &&
        settings.OfflineTranslationEngine is OfflineTranslationEngine.HunyuanLightweight or OfflineTranslationEngine.QwenLargeModel;

    internal static TranslationPreparation? Begin(AppSettings settings, CancellationToken token)
    {
        if (!IsEligible(settings)) return null;
        var manager = settings.OfflineTranslationEngine == OfflineTranslationEngine.HunyuanLightweight
            ? LocalLargeTranslationModelManager.LightweightShared : LocalLargeTranslationModelManager.Shared;
        return new(manager.InferenceSession, token);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _cancellation.Cancel();
        _ = ReleaseAsync();
    }

    private async Task ReleaseAsync()
    {
        try
        {
            try { await _preparation.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            await _session.CancelUnusedPreparationAsync(_owner).ConfigureAwait(false);
        }
        finally { _cancellation.Dispose(); }
    }
}
