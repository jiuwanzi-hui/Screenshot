using System.Windows;
using Screenshot.App.Core;

namespace Screenshot.App.Presentation;

public partial class MainWindow
{
    private void InitializeRealtimeSettings() => RealtimeSettingsHost.Content = new RealtimeTranslationSettingsPanel(
        () => _settingsViewModel.RealtimeTranslation,
        options => { _settingsViewModel.RealtimeTranslation = options; ScheduleSettingsApply(); },
        () => _settingsViewModel.TranslationTargetLanguage);

    internal void SaveRealtimeTranslationSettings(RealtimeTranslationSettings options)
    {
        _settingsViewModel.RealtimeTranslation = options;
        ApplySettingsImmediately();
    }

    private void OnOpenRealtimeTranslationClick(object sender, RoutedEventArgs e)
    {
        ApplySettingsImmediately();
        RealtimeTranslationRequested?.Invoke(this, EventArgs.Empty);
    }
}
