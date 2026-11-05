using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Threading;

namespace Screenshot.App.Infrastructure;

// UIA clients can outlive a window. In particular WPF item peers are strongly
// held by their COM proxies, which then retain the retired window's entire tree.
// Disconnect only that destroyed tree; never disable UIA or disconnect live peers.
internal static class ClosedWindowAutomation
{
    private static bool _initialized;

    internal static void Initialize()
    {
        if (_initialized) return;
        _initialized = true;
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
            new RoutedEventHandler(OnLoaded));
    }

    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Window window || !ReferenceEquals(sender, e.OriginalSource)) return;
        window.Closed -= OnClosed;
        window.Closed += OnClosed;
    }

    private static void OnClosed(object? sender, EventArgs e)
    {
        if (sender is not Window window) return;
        window.Closed -= OnClosed;
        var providers = GetProviders(window);
        if (providers.Count == 0) return;
        // Closing can originate in SendMessage/WM_CLOSE. UIA explicitly forbids
        // outbound COM disconnect calls from that stack. Wait for it to unwind,
        // then release on a worker without blocking input or the dispatcher.
        _ = window.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            _ = Task.Run(() => Disconnect(providers))));
    }

    internal static List<IRawElementProviderSimple> GetProviders(Window window)
    {
        // Do not create an automation tree for windows never inspected by UIA.
        if (UIElementAutomationPeer.FromElement(window) is not { } root) return [];
        var accessor = new ProviderAccess(window);
        var providers = new HashSet<IRawElementProviderSimple>(ReferenceEqualityComparer.Instance);
        var visited = new HashSet<AutomationPeer>(ReferenceEqualityComparer.Instance);
        var pending = new Stack<AutomationPeer>();
        pending.Push(root);
        while (pending.TryPop(out var peer))
        {
            if (!visited.Add(peer)) continue;
            try
            {
                if (peer.EventsSource is { } source) pending.Push(source);
                if (peer.GetChildren() is { } children)
                    foreach (var child in children) pending.Push(child);
                if (accessor.GetProvider(peer) is { } provider) providers.Add(provider);
            }
            catch (ElementNotAvailableException) { }
            catch (InvalidOperationException) { }
        }
        return providers.ToList();
    }

    private static void Disconnect(List<IRawElementProviderSimple> providers)
    {
        try
        {
            foreach (var provider in providers)
            {
                try { _ = UiaDisconnectProvider(provider); }
                catch (COMException) { }
            }
        }
        finally
        {
            providers.Clear();
            Core.MemoryFootprint.TrimAfterHeavyOperation();
        }
    }

    private sealed class ProviderAccess(FrameworkElement owner) : FrameworkElementAutomationPeer(owner)
    {
        internal IRawElementProviderSimple? GetProvider(AutomationPeer peer) => ProviderFromPeer(peer);
    }

    [DllImport("UIAutomationCore.dll", ExactSpelling = true)]
    private static extern int UiaDisconnectProvider(
        [MarshalAs(UnmanagedType.Interface)] IRawElementProviderSimple provider);
}
