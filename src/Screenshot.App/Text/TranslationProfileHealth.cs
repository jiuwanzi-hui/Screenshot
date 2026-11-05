using System.Security.Cryptography;
using System.Text;
using Screenshot.App.Core;

namespace Screenshot.App.Text;

internal sealed record TranslationProfileHealthUpdate(AiTranslationProfile Profile, bool IsAvailable, string Reason);

// Event-driven observations of actual requests, not a background polling loop.
internal static class TranslationProfileHealth
{
    private static readonly object Sync = new();
    private static readonly Dictionary<string, Observer> Observers = new(StringComparer.Ordinal);
    internal static event Action<TranslationProfileHealthUpdate>? Changed;

    internal static bool SameConnection(AiTranslationProfile a, AiTranslationProfile b) =>
        a.Id == b.Id && a.Provider == b.Provider && a.Endpoint == b.Endpoint && a.Model == b.Model && a.AppId == b.AppId && a.Purpose == b.Purpose;

    internal static Observer Observe(AiTranslationProfile profile, string? apiKey)
    {
        var keyHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(apiKey ?? "")));
        lock (Sync)
        {
            if (Observers.TryGetValue(profile.Id, out var existing) &&
                SameConnection(existing.Profile, profile) && existing.KeyHash == keyHash) return existing;
            return Observers[profile.Id] = new Observer(profile, keyHash);
        }
    }

    internal static void Invalidate(string profileId) { lock (Sync) Observers.Remove(profileId); }

    internal static TranslationProfileHealthUpdate? Latest(AiTranslationProfile profile)
    {
        lock (Sync) return Observers.TryGetValue(profile.Id, out var observer) && SameConnection(observer.Profile, profile)
            ? observer.Last : null;
    }

    internal sealed class Observer(AiTranslationProfile profile, string keyHash)
    {
        internal AiTranslationProfile Profile { get; } = profile;
        internal string KeyHash { get; } = keyHash;
        internal TranslationProfileHealthUpdate? Last { get; private set; }
        private long _lastCompleted;
        private long _requestSequence;
        internal long BeginRequest() => Interlocked.Increment(ref _requestSequence);
        internal void Complete(long request, bool available, string reason)
        {
            TranslationProfileHealthUpdate update;
            lock (Sync)
            {
                if (!Observers.TryGetValue(Profile.Id, out var current) || !ReferenceEquals(current, this) || request < _lastCompleted) return;
                _lastCompleted = request;
                update = new(Profile, available, reason);
                if (Last == update) return;
                Last = update;
            }
            Changed?.Invoke(update);
        }
    }
}
