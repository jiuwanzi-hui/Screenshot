using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Screenshot.App.Capture;

public partial class StartupFeedbackWindow : Window
{
    private StartupFeedbackWindow(string message)
    {
        InitializeComponent();
        MessageText.Text = message;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Loaded += OnLoaded;
    }

    internal static Task ShowAsync(string message)
    {
        var window = new StartupFeedbackWindow(message);
        window.Show();
        return window.Dispatcher.InvokeAsync(
            () => { }, DispatcherPriority.Loaded).Task.ContinueWith(
                _ => { }, TaskScheduler.Default);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        var workArea = SystemParameters.WorkArea;
        Left = workArea.Left + ((workArea.Width - ActualWidth) / 2);
        Top = workArea.Top + ((workArea.Height - ActualHeight) / 2);
        await Dispatcher.Yield(DispatcherPriority.Render);
        Toast.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(180)));
        await Task.Delay(1300);
        Toast.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(220)));
        await Task.Delay(240);
        Close();
    }
}
