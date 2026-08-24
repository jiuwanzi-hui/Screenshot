using System.Windows.Controls;
using System.Windows;
using Screenshot.App.Core;
using Screenshot.App.Text;

namespace Screenshot.App.Tests;

public sealed class TextTranslationWindowTests
{
    [Fact]
    public void TargetLanguageSelectionIsPersistedWithoutSourceText()
    {
        string? savedLanguage = null;

        WpfTestHost.Invoke(() =>
        {
            var window = new TextTranslationWindow(
                () => new AppSettings(),
                language => savedLanguage = language);
            try
            {
                var target = Assert.IsType<ComboBox>(
                    window.FindName("TargetLanguageComboBox"));

                target.SelectedValue = "en";

                Assert.Equal("en", savedLanguage);
            }
            finally
            {
                window.Dispose();
            }
        });
    }

    [Fact]
    public void TypingSchedulesAutomaticTranslationInsteadOfRequiringAButtonClick()
    {
        WpfTestHost.Invoke(() =>
        {
            var window = new TextTranslationWindow(() => new AppSettings());
            try
            {
                var source = Assert.IsType<TextBox>(
                    window.FindName("SourceTextBox"));
                var status = Assert.IsType<TextBlock>(
                    window.FindName("StatusText"));

                source.Text = "Text entered by the user";

                Assert.Equal("等待输入完成…", status.Text);
                Assert.Equal(
                    "立即翻译",
                    Assert.IsType<Button>(window.FindName("TranslateButton")).Content);
            }
            finally
            {
                window.Dispose();
            }
        });
    }

    [Fact]
    public void InstallModelButtonStartsHiddenAndHasAnExplicitAction()
    {
        WpfTestHost.Invoke(() =>
        {
            var window = new TextTranslationWindow(() => new AppSettings());
            try
            {
                var button = Assert.IsType<Button>(
                    window.FindName("InstallModelButton"));

                Assert.Equal(Visibility.Collapsed, button.Visibility);
                Assert.Equal("安装当前语言包", button.Content);
            }
            finally
            {
                window.Dispose();
            }
        });
    }

    [Fact]
    public void InstallModelButtonRequestsTheSelectedTargetLanguage()
    {
        string? requestedLanguage = null;
        WpfTestHost.Invoke(() =>
        {
            var window = new TextTranslationWindow(
                () => new AppSettings
                {
                    TranslationTargetLanguage = "ja",
                },
                installModelRequested: language =>
                    requestedLanguage = language);
            try
            {
                var button = Assert.IsType<Button>(
                    window.FindName("InstallModelButton"));
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

                Assert.Equal("ja", requestedLanguage);
            }
            finally
            {
                window.Dispose();
            }
        });
    }
}
