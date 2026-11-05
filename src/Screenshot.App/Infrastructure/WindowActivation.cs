using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace Screenshot.App.Infrastructure;

/// <summary>Restore explicitly requested windows and finish user activation in their existing Z-order band.</summary>
internal static class WindowActivation
{
    private static readonly ConditionalWeakTable<Window, ActivationState> States = new();

    internal static void Initialize() => EventManager.RegisterClassHandler(typeof(Window),
        FrameworkElement.LoadedEvent, new RoutedEventHandler((sender, _) =>
        {
            if (sender is Window window && window.ShowInTaskbar && window.ShowActivated) Track(window);
        }));

    internal static void Track(Window window) => _ = States.GetValue(window, owner => new ActivationState(owner));

    internal static void Show(Window window)
    {
        Track(window);
        var handle = new WindowInteropHelper(window).Handle;
        if (window.WindowState == WindowState.Minimized && handle != IntPtr.Zero)
            _ = ShowWindow(handle, 9); // SW_RESTORE preserves a previously maximized window.
        window.Show();
        _ = WindowPlacementService.EnsureVisible(window);
        handle = new WindowInteropHelper(window).Handle;
        // An owner disabled by ShowDialog must activate its live modal child.
        var target = !IsWindowEnabled(handle) ? GetLastActivePopup(handle) : handle;
        if (target != IntPtr.Zero)
        {
            if (IsIconic(target)) _ = ShowWindow(target, 9);
            _ = SetForegroundWindow(target);
            _ = BringWindowToTop(target);
        }
        if (target == handle) _ = window.Activate();
    }

    private sealed class ActivationState
    {
        private readonly Window _window;
        private bool _queued;
        private bool _closed;

        internal ActivationState(Window window)
        {
            _window = window;
            window.Activated += OnActivated;
            window.Closed += OnClosed;
        }

        private void OnActivated(object? sender, EventArgs e)
        {
            if (_queued || _closed) return;
            _queued = true;
            // Finish after taskbar/menu native activation messages, without
            // toggling Topmost or repeatedly stealing focus from another app.
            _ = _window.Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
            {
                _queued = false;
                if (_closed || !_window.IsVisible || _window.WindowState == WindowState.Minimized) return;
                var handle = new WindowInteropHelper(_window).Handle;
                if (handle != IntPtr.Zero && GetForegroundWindow() == handle)
                    _ = BringWindowToTop(handle);
            });
        }

        private void OnClosed(object? sender, EventArgs e)
        {
            _closed = true;
            _window.Activated -= OnActivated;
            _window.Closed -= OnClosed;
            States.Remove(_window);
        }
    }

    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr handle, int command);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr handle);
    [DllImport("user32.dll")] private static extern bool IsWindowEnabled(IntPtr handle);
    [DllImport("user32.dll")] private static extern IntPtr GetLastActivePopup(IntPtr handle);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr handle);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr handle);
}
