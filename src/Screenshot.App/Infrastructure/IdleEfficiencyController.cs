using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using Screenshot.App.Presentation;

namespace Screenshot.App.Infrastructure;

// An event-driven idle policy. No repeating sampling, working-set trimming,
// affinity restrictions or changes to capture/model quality are involved.
internal sealed class IdleEfficiencyController : IDisposable
{
    private static WeakReference<IdleEfficiencyController>? _current;
    private readonly DispatcherTimer _idleTimer;
    private readonly HashSet<Window> _windows = [];
    private readonly Action<bool> _apply;
    private readonly object _stateGate = new();
    private bool _captureActive;
    private bool _disposed;
    internal bool IsEfficient { get; private set; }

    static IdleEfficiencyController()
    {
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
            new RoutedEventHandler(OnWindowLoaded));
    }

    internal IdleEfficiencyController(Action<bool>? apply = null)
    {
        _apply = apply ?? NativeEfficiency.Apply;
        _idleTimer = new DispatcherTimer(DispatcherPriority.Background)
        { Interval = TimeSpan.FromSeconds(20) };
        _idleTimer.Tick += OnIdle;
        _current = new(this);
        if (System.Windows.Application.Current is { } application)
            foreach (Window window in application.Windows) Track(window);
        Wake();
    }

    private static void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(sender, e.OriginalSource) && sender is Window window &&
            _current?.TryGetTarget(out var controller) == true) controller.Track(window);
    }

    private void Track(Window window)
    {
        if (_disposed || !_windows.Add(window)) return;
        window.IsVisibleChanged += OnVisibilityChanged;
        window.Closed += OnClosed;
        window.MouseEnter += OnPointerEntered;
        window.MouseLeave += OnPointerLeft;
        Wake();
    }

    internal void SetCaptureActive(bool active)
    {
        _captureActive = active;
        Wake();
    }

    internal void Wake()
    {
        if (_disposed) return;
        SetEfficient(false);
        if (!_idleTimer.Dispatcher.CheckAccess())
        {
            _ = _idleTimer.Dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(Wake));
            return;
        }
        _idleTimer.Stop();
        _idleTimer.Start();
    }

    private void OnIdle(object? sender, EventArgs e)
    {
        _idleTimer.Stop();
        if (_disposed) return;
        var foregroundWork = _windows.Any(window => window.IsVisible &&
            (window is not FloatingCaptureWindow || window.IsMouseOver));
        SetEfficient(CanUseEfficiency(_captureActive, foregroundWork));
        if (IsEfficient)
        {
            Screenshot.App.Editor.EmojiStickerRenderer.ReleaseIdleCache();
            Screenshot.App.Core.MemoryFootprint.TrimAfterHeavyOperation();
        }
        // A visible window/capture will schedule the next check when it closes.
    }

    internal static bool CanUseEfficiency(bool captureActive, bool visibleWork) =>
        !captureActive && !visibleWork;

    private void SetEfficient(bool value)
    {
        lock (_stateGate)
        {
            if (IsEfficient == value) return;
            _apply(value);
            IsEfficient = value;
        }
    }

    private void OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e) => Wake();
    private void OnPointerEntered(object sender, System.Windows.Input.MouseEventArgs e) => Wake();
    private void OnPointerLeft(object sender, System.Windows.Input.MouseEventArgs e) => Wake();
    private void OnClosed(object? sender, EventArgs e)
    {
        if (sender is Window window) Detach(window);
        Wake();
    }

    private void Detach(Window window)
    {
        window.IsVisibleChanged -= OnVisibilityChanged;
        window.Closed -= OnClosed;
        window.MouseEnter -= OnPointerEntered;
        window.MouseLeave -= OnPointerLeft;
        _windows.Remove(window);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _idleTimer.Stop();
        _idleTimer.Tick -= OnIdle;
        foreach (var window in _windows.ToArray()) Detach(window);
        SetEfficient(false);
        if (_current?.TryGetTarget(out var current) == true && ReferenceEquals(current, this)) _current = null;
    }
}

internal static class NativeEfficiency
{
    internal static void Apply(bool enabled)
    {
        // EcoQoS is an OS scheduling hint, not a memory cap. Unsupported systems
        // simply keep their normal policy. Do not change the timer resolution.
        var power = new PowerState { Version = 1, ControlMask = 1, StateMask = enabled ? 1u : 0u };
        if (!SetPower(GetCurrentProcess(), 4, ref power, 12)) return;
        var memory = new MemoryPriority { Priority = enabled ? 2u : 5u };
        _ = SetMemory(GetCurrentProcess(), 0, ref memory, 4);
        _ = SetPriorityClass(GetCurrentProcess(), enabled ? 0x4000u : 0x20u);
    }

    [StructLayout(LayoutKind.Sequential)] private struct PowerState { public uint Version, ControlMask, StateMask; }
    [StructLayout(LayoutKind.Sequential)] private struct MemoryPriority { public uint Priority; }
    [DllImport("kernel32.dll")] private static extern nint GetCurrentProcess();
    [DllImport("kernel32.dll", EntryPoint = "SetProcessInformation", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetPower(nint process, int information, ref PowerState state, uint size);
    [DllImport("kernel32.dll", EntryPoint = "SetProcessInformation", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetMemory(nint process, int information, ref MemoryPriority state, uint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetPriorityClass(nint process, uint priority);
}
