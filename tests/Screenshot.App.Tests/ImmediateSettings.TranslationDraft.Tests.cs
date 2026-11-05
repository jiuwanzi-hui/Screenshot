using System.IO;
using System.Reflection;
using System.Windows.Controls;
using Screenshot.App.Infrastructure;
using Screenshot.App.Presentation;

namespace Screenshot.App.Tests;

public sealed partial class ImmediateSettingsTests
{
    [Theory]
    [InlineData(false, null)]
    [InlineData(false, false)]
    [InlineData(true, null)]
    [InlineData(true, false)]
    public void ClosingNewTranslationEditorDoesNotAddOrPersistADraft(bool isPublic, bool? result)
    {
        WpfTestHost.Invoke(() =>
        {
            using var keys = new GlobalHotKeyManager(deferLowLevelInputHooks: true);
            var store = new SettingsStore(Path.Combine(_testDirectory, "draft.json"));
            var settings = CreateSettings();
            store.Save(settings);
            var originalFile = File.ReadAllText(store.SettingsPath);
            var window = new MainWindow(settings, store, new FakeStartupRegistrationService(), keys, new FakeTranslationCredentialStore());
            try
            {
                var model = (SettingsViewModel)window.DataContext;
                var count = model.TranslationProfiles.Count;
                var selected = model.SelectedTranslationProfileId;
                for (var attempt = 0; attempt < 3; attempt++)
                    window.AddTranslationProfile(isPublic, editor =>
                    {
                        ((TextBox)editor.FindName("NameBox")).Text = "unsaved draft";
                        Assert.Equal(count, model.TranslationProfiles.Count);
                        return result;
                    });
                Assert.Equal(count, model.TranslationProfiles.Count);
                Assert.Equal(selected, model.SelectedTranslationProfileId);
                store.FlushPendingSave();
                Assert.Equal(originalFile, File.ReadAllText(store.SettingsPath));
            }
            finally { window.RequestExit(); }
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SavingNewTranslationDraftAddsExactlyOneAndPersistsIt(bool isPublic)
    {
        WpfTestHost.Invoke(() =>
        {
            using var keys = new GlobalHotKeyManager(deferLowLevelInputHooks: true);
            var store = new SettingsStore(Path.Combine(_testDirectory, "saved-draft.json"));
            var window = new MainWindow(CreateSettings(), store, new FakeStartupRegistrationService(), keys, new FakeTranslationCredentialStore());
            try
            {
                var model = (SettingsViewModel)window.DataContext;
                var count = model.TranslationProfiles.Count;
                window.AddTranslationProfile(isPublic, _ => true);
                Assert.Equal(count + 1, model.TranslationProfiles.Count);
                Assert.Equal(model.TranslationProfiles.Last().Id, model.SelectedTranslationProfileId);
                store.FlushPendingSave();
                Assert.Equal(count + 1, store.Load().Settings.TranslationProfiles.Length);
            }
            finally { window.RequestExit(); }
        });
    }

    [Fact]
    public void RetiringTranslationEditorCancelsWorkAndReleasesSensitiveFields()
    {
        WpfTestHost.Invoke(() =>
        {
            var model = new SettingsViewModel(CreateSettings());
            using var editor = new TranslationProfileEditorWindow(model.CreateTranslationProfileDraft(), new FakeTranslationCredentialStore());
            var lifetime = (CancellationTokenSource)typeof(TranslationProfileEditorWindow)
                .GetField("_lifetime", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(editor)!;
            var token = lifetime.Token;
            var password = (PasswordBox)editor.FindName("ApiKeyBox");
            password.Password = "not-saved";
            editor.Dispose();
            Assert.True(token.IsCancellationRequested);
            Assert.Empty(password.Password);
            Assert.Null(((ComboBox)editor.FindName("ModelBox")).ItemsSource);
        });
    }
}
