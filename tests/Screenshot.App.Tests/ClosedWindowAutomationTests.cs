using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Interop;
using Screenshot.App.Infrastructure;

namespace Screenshot.App.Tests;

public sealed class ClosedWindowAutomationTests
{
    [Fact]
    public void RetirementDoesNotCreateAnUnusedAutomationTree()
    {
        WpfTestHost.Invoke(() =>
        {
            var window = new Window { Content = new ListBox { Items = { "first", "second" } } };
            Assert.Null(UIElementAutomationPeer.FromElement(window));
            Assert.Empty(ClosedWindowAutomation.GetProviders(window));
            Assert.Null(UIElementAutomationPeer.FromElement(window));
            window.Close();
        });
    }

    [DesktopFact]
    public async Task ClosingOneWindowDisconnectsItsItemsButPreservesTheOtherWindowsAccessibility()
    {
        Window? retired = null;
        Window? live = null;
        nint retiredHandle = 0, liveHandle = 0;
        try
        {
            WpfTestHost.Invoke(() =>
            {
                ClosedWindowAutomation.Initialize();
                retired = Create("retired-window", "retired-item");
                live = Create("live-window", "live-item");
                retired.Show();
                live.Show();
                retiredHandle = new WindowInteropHelper(retired).Handle;
                liveHandle = new WindowInteropHelper(live).Handle;
            });
            var oldItem = await Task.Run(() => FindItem(retiredHandle, "retired-item"));
            var liveItem = await Task.Run(() => FindItem(liveHandle, "live-item"));
            Assert.NotNull(oldItem);
            Assert.NotNull(liveItem);
            WpfTestHost.Invoke(() => retired!.Close());
            var disconnected = false;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            while (!disconnected)
            {
                disconnected = await Task.Run(() =>
                {
                    try { _ = oldItem.Current.Name; return false; }
                    catch (ElementNotAvailableException) { return true; }
                });
                if (!disconnected) await Task.Delay(40, timeout.Token);
            }
            Assert.Equal("live-item", await Task.Run(() => liveItem.Current.Name));
        }
        finally { WpfTestHost.Invoke(() => { retired?.Close(); live?.Close(); }); }
    }

    private static Window Create(string title, string item) => new()
    {
        Title = title, Width = 200, Height = 100, ShowActivated = false,
        ShowInTaskbar = false, Opacity = 0,
        Content = new ListBox { Items = { item } },
    };

    private static AutomationElement FindItem(nint handle, string name) =>
        AutomationElement.FromHandle(handle).FindFirst(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.NameProperty, name));
}
