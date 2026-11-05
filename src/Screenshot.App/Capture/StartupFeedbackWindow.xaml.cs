using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Screenshot.App.Capture;

public partial class StartupFeedbackWindow : Window
{
    private readonly int _visibleMilliseconds;

    private StartupFeedbackWindow(string message, int visibleMilliseconds)
    {
        InitializeComponent();
        _visibleMilliseconds = visibleMilliseconds;
        MessageText.Text = message;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Loaded += OnLoaded;
    }

    internal static Task ShowAsync(string message, int visibleMilliseconds = 1300)
    {
        var window = new StartupFeedbackWindow(message, visibleMilliseconds);
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
        await Task.Delay(_visibleMilliseconds);
        Toast.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(220)));
        await Task.Delay(240);
        Close();
    }
}
