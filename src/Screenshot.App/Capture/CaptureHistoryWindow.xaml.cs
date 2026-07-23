using System.Windows;

namespace Screenshot.App.Capture;

public partial class CaptureHistoryWindow : Window
{
    public CaptureHistoryWindow(CaptureHistoryService historyService)
    {
        ArgumentNullException.ThrowIfNull(historyService);

        InitializeComponent();
        DataContext = historyService;
    }
}
