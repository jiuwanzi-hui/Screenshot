using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Screenshot.App.Capture;

namespace Screenshot.App.Text;

public partial class RecognitionResultPopupWindow : Window
{
    private readonly bool _closeAfterCopy;
    internal bool WasClosed { get; private set; }

    public RecognitionResultPopupWindow(
        string title,
        string sourceText,
        string? translatedText,
        bool closeAfterCopy = true,
        string? notice = null)
    {
        _closeAfterCopy = closeAfterCopy;
        InitializeComponent();
        Presentation.WindowShell.ConfigureChrome(this, captionHeight: 0);
        Closed += (_, _) => { WasClosed = true; BusyRail.IsActive = false; };
        PreviewMouseDown += (_, _) => Presentation.UiTransitions.Finish(Shell);
        // Do not let WPF present the window at its default location for one
        // compositor frame. Position it first, then reveal it in OnLoaded.
        Opacity = 0;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = SystemParameters.WorkArea.Right - Width - 18;
        Top = SystemParameters.WorkArea.Bottom - Height - 18;
        Title = title;
        TitleText.Text = title;
        ResultTextBox.Text = string.IsNullOrWhiteSpace(translatedText)
            ? sourceText
            : translatedText;
        // WPF's default TextBox copy command calls Clipboard.SetText
        // synchronously on the UI thread. On machines with a clipboard
        // manager or Office integration that can block the whole popup for
        // several seconds. Route both Ctrl+C and the context-menu Copy item
        // through the existing asynchronous clipboard service instead.
        ResultTextBox.CommandBindings.Add(new CommandBinding(
            ApplicationCommands.Copy,
            OnCopySelectionCommand,
            OnCanCopySelectionCommand));
        SourceText.Text = string.IsNullOrWhiteSpace(translatedText)
            ? "识别内容"
            : "译文 · 已保留原文段落顺序";
        if (!string.IsNullOrWhiteSpace(notice))
        {
            SourceText.Text = notice;
            SourceText.ToolTip = notice;
        }
        Loaded += OnLoaded;
    }

    internal void ShowBusy(string title, string status)
    {
        Title = title;
        TitleText.Text = title.Contains("翻译", StringComparison.Ordinal) ? "翻译" : "识别文字";
        BusyText.Text = title;
        SourceText.Text = status;
        SourceText.ToolTip = status;
        ResultTextBox.Visibility = Visibility.Collapsed;
        BusyText.Visibility = Visibility.Visible;
        CopyAllButton.IsEnabled = false;
        BusyRail.IsActive = true;
    }

    internal void ShowResult(string title, string source, string? translation, string? notice)
    {
        if (WasClosed) return;
        BusyRail.IsActive = false;
        BusyText.Visibility = Visibility.Collapsed;
        Title = title;
        TitleText.Text = title;
        ResultTextBox.Text = string.IsNullOrWhiteSpace(translation) ? source : translation;
        ResultTextBox.Visibility = Visibility.Visible;
        CopyAllButton.IsEnabled = true;
        SourceText.Text = string.IsNullOrWhiteSpace(notice)
            ? translation is null ? "本机识别 · 可选择文字复制" : "译文 · 保留原文段落顺序"
            : notice;
        SourceText.ToolTip = SourceText.Text;
        Topmost = PinButton.IsChecked == true;
        Presentation.UiTransitions.Reveal(ResultTextBox, milliseconds: 100);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        UpdateLayout();
        Left = SystemParameters.WorkArea.Right - ActualWidth - 18;
        Top = SystemParameters.WorkArea.Bottom - ActualHeight - 18;
        BeginAnimation(OpacityProperty, null);
        Opacity = 1;
        Presentation.UiTransitions.Reveal(Shell, slide: true);
    }

    protected override void OnClosed(EventArgs e)
    {
        WasClosed = true;
        BusyRail.IsActive = false;
        Loaded -= OnLoaded;
        ResultTextBox.Clear();
        SourceText.Text = string.Empty;
        SourceText.ToolTip = null;
        BusyText.Text = string.Empty;
        base.OnClosed(e);
        Core.MemoryFootprint.TrimAfterHeavyOperation();
    }

    private void OnHeaderMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed &&
            !IsHeaderCommandSource(e.OriginalSource))
        {
            DragMove();
        }
    }

    private static bool IsHeaderCommandSource(object source)
    {
        for (var current = source as DependencyObject;
             current is not null;
             current = VisualTreeHelper.GetParent(current))
        {
            if (current is System.Windows.Controls.Primitives.ButtonBase)
            {
                return true;
            }
        }

        return false;
    }

    private void OnPinClick(object sender, RoutedEventArgs e)
    {
        Topmost = PinButton.IsChecked == true;
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnCanCopySelectionCommand(
        object sender,
        CanExecuteRoutedEventArgs e)
    {
        e.CanExecute = !string.IsNullOrEmpty(ResultTextBox.SelectedText);
        e.Handled = true;
    }

    private async void OnCopySelectionCommand(
        object sender,
        ExecutedRoutedEventArgs e)
    {
        e.Handled = true;
        var selectedText = ResultTextBox.SelectedText;
        if (string.IsNullOrEmpty(selectedText))
        {
            return;
        }

        try
        {
            await ClipboardTextService.SetTextAsync(selectedText);
            SourceText.Text = "已复制所选文字";
        }
        catch
        {
            SourceText.Text = "剪贴板正被其他程序使用，请重试";
        }
    }

    private async void OnCopyClick(object sender, RoutedEventArgs e)
    {
        try
        {
            await ClipboardTextService.SetTextAsync(ResultTextBox.Text);
            SourceText.Text = "已复制全部内容";
            // The user can pin an ordinary result popup after it is shown.  The
            // current pin state must take precedence over the creation default.
            if (_closeAfterCopy && PinButton.IsChecked != true)
            {
                Close();
            }
        }
        catch
        {
            SourceText.Text = "剪贴板正被其他程序使用，请重试";
        }
    }
}
