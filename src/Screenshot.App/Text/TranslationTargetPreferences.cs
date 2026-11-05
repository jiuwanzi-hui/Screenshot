namespace Screenshot.App.Text;

/// <summary>UI-thread shared target; persistence remains owned by the application settings.</summary>
internal static class TranslationTargetPreferences
{
    private static Action<string>? _save;
    internal static string? Language { get; private set; }
    internal static long Version { get; private set; }
    internal static event EventHandler? Changed;
    internal static void Configure(string language, Action<string> save)
    {
        _save = save;
        Publish(language);
    }
    internal static void Select(string language)
    {
        if (!TranslationLanguageCatalog.Languages.Any(item => item.Tag == language) || Language == language) return;
        _save?.Invoke(language);
        Publish(language);
    }
    internal static void Publish(string language)
    {
        if (Language == language) return;
        Language = language;
        Version++;
        Changed?.Invoke(null, EventArgs.Empty);
    }
}
