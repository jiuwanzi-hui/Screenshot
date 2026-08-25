using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace Screenshot.App.Infrastructure;

internal static class WindowPlacementKeys
{
    public const string Settings = "settings";
    public const string ImageEditor = "imageEditor";
    public const string OcrResult = "ocrResult";
    public const string CaptureHistory = "captureHistory";
    public const string CapturePreview = "capturePreview";
    public const string FloatingCapture = "floatingCapture";
    public const string VideoRecordingControls = "videoRecordingControls";
    public const string TextTranslation = "textTranslation";
}

internal sealed record WindowPlacementRecord(
    int Left,
    int Top,
    int Right,
    int Bottom,
    bool IsMaximized)
{
    [JsonIgnore]
    public int Width => Right - Left;

    [JsonIgnore]
    public int Height => Bottom - Top;

    public bool HasValidBounds =>
        Width > 0 &&
        Height > 0 &&
        Width <= 100_000 &&
        Height <= 100_000;
}

internal sealed record WindowPlacementFile
{
    public int Version { get; init; } = 1;

    public Dictionary<string, WindowPlacementRecord> Windows { get; init; } =
        new(StringComparer.Ordinal);
}

internal sealed class WindowPlacementStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly object _sync = new();
    private readonly string _path;
    private WindowPlacementFile _file;

    public WindowPlacementStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
        _file = Load(path);
    }

    public bool TryGet(string key, out WindowPlacementRecord placement)
    {
        lock (_sync)
        {
            if (_file.Windows.TryGetValue(key, out var stored) &&
                stored.HasValidBounds)
            {
                placement = stored;
                return true;
            }
        }

        placement = new WindowPlacementRecord(0, 0, 0, 0, false);
        return false;
    }

    public bool TrySave(string key, WindowPlacementRecord placement)
    {
        if (string.IsNullOrWhiteSpace(key) || !placement.HasValidBounds)
        {
            return false;
        }

        lock (_sync)
        {
            _file.Windows[key] = placement;
            return TryWriteFile();
        }
    }

    public bool TryRemove(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return false;
        }

        lock (_sync)
        {
            if (!_file.Windows.Remove(key))
            {
                return true;
            }

            return TryWriteFile();
        }
    }

    private static WindowPlacementFile Load(string path)
    {
        if (!File.Exists(path))
        {
            return new WindowPlacementFile();
        }

        try
        {
            var json = File.ReadAllText(path);
            var file = JsonSerializer.Deserialize<WindowPlacementFile>(
                json,
                SerializerOptions);
            return file is { Version: 1, Windows: not null }
                ? file
                : new WindowPlacementFile();
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or JsonException
                or ArgumentException
                or NotSupportedException)
        {
            return new WindowPlacementFile();
        }
    }

    private bool TryWriteFile()
    {
        var directory = Path.GetDirectoryName(_path);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return false;
        }

        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(_path)}.{Guid.NewGuid():N}.tmp");

        try
        {
            Directory.CreateDirectory(directory);
            var json = JsonSerializer.Serialize(_file, SerializerOptions);
            File.WriteAllText(
                temporaryPath,
                json,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temporaryPath, _path, overwrite: true);
            return true;
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or ArgumentException
                or NotSupportedException)
        {
            return false;
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}

public static class WindowPlacementService
{
    private const int SwShowNormal = 1;
    private const int SwShowMaximized = 3;
    private const int WindowPlacementRestoreToMaximized = 0x0002;
    private const uint MonitorDefaultToNearest = 0x00000002;
    private static readonly object Sync = new();
    private static WindowPlacementStore? _store;

    public static void Initialize(string path)
    {
        lock (Sync)
        {
            _store = new WindowPlacementStore(path);
        }
    }

    internal static void ResetForTests()
    {
        lock (Sync)
        {
            _store = null;
        }
    }

    public static bool Track(Window window, string key)
    {
        return Track(window, key, restoreSize: true);
    }

    public static bool TrackPosition(Window window, string key)
    {
        return Track(window, key, restoreSize: false);
    }

    public static bool EnsureVisible(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        return TryCapture(window, out var placement) &&
               TryRestore(window, placement);
    }

    public static bool EnsurePositionVisible(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        return TryCapture(window, out var placement) &&
               TryRestorePosition(window, placement);
    }

    internal static bool TrackPosition(
        Window window,
        string key,
        out Action resetTracking)
    {
        return Track(window, key, restoreSize: false, out resetTracking);
    }

    private static bool Track(Window window, string key, bool restoreSize)
    {
        return Track(window, key, restoreSize, out _);
    }

    private static bool Track(
        Window window,
        string key,
        bool restoreSize,
        out Action resetTracking)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        WindowPlacementStore? store;
        lock (Sync)
        {
            store = _store;
        }

        if (store is null)
        {
            resetTracking = static () => { };
            return false;
        }

        var hasSavedPlacement = store.TryGet(key, out _);
        var registration = new WindowPlacementRegistration(
            window,
            key,
            store,
            restoreSize);
        registration.Attach();
        resetTracking = registration.StopTrackingAndForget;
        return hasSavedPlacement;
    }

    private static bool TryRestorePosition(
        Window window,
        WindowPlacementRecord storedPlacement)
    {
        try
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero || !GetWindowRect(handle, out var currentBounds))
            {
                return false;
            }

            var storedBounds = new NativeRect(
                storedPlacement.Left,
                storedPlacement.Top,
                storedPlacement.Left + currentBounds.Width,
                storedPlacement.Top + currentBounds.Height);
            var monitor = MonitorFromRect(ref storedBounds, MonitorDefaultToNearest);
            if (monitor == IntPtr.Zero)
            {
                return false;
            }

            var monitorInfo = new NativeMonitorInfo
            {
                Size = Marshal.SizeOf<NativeMonitorInfo>(),
            };
            if (!GetMonitorInfo(monitor, ref monitorInfo))
            {
                return false;
            }

            var left = Math.Clamp(
                storedPlacement.Left,
                monitorInfo.WorkArea.Left,
                Math.Max(
                    monitorInfo.WorkArea.Left,
                    monitorInfo.WorkArea.Right - currentBounds.Width));
            var top = Math.Clamp(
                storedPlacement.Top,
                monitorInfo.WorkArea.Top,
                Math.Max(
                    monitorInfo.WorkArea.Top,
                    monitorInfo.WorkArea.Bottom - currentBounds.Height));
            return SetWindowPos(
                handle,
                IntPtr.Zero,
                left,
                top,
                0,
                0,
                SetWindowPositionNoSize |
                SetWindowPositionNoZOrder |
                SetWindowPositionNoActivate);
        }
        catch (Exception exception) when (
            exception is DllNotFoundException
                or EntryPointNotFoundException
                or ExternalException)
        {
            return false;
        }
    }

    internal static WindowPlacementRecord ConstrainToWorkArea(
        WindowPlacementRecord placement,
        NativeRect workArea)
    {
        if (!placement.HasValidBounds ||
            workArea.Width <= 0 ||
            workArea.Height <= 0)
        {
            return placement;
        }

        var minimumWidth = Math.Min(160, workArea.Width);
        var minimumHeight = Math.Min(120, workArea.Height);
        var width = Math.Clamp(placement.Width, minimumWidth, workArea.Width);
        var height = Math.Clamp(placement.Height, minimumHeight, workArea.Height);
        var left = Math.Clamp(
            placement.Left,
            workArea.Left,
            Math.Max(workArea.Left, workArea.Right - width));
        var top = Math.Clamp(
            placement.Top,
            workArea.Top,
            Math.Max(workArea.Top, workArea.Bottom - height));

        return new WindowPlacementRecord(
            left,
            top,
            left + width,
            top + height,
            placement.IsMaximized);
    }

    private static bool TryRestore(
        Window window,
        WindowPlacementRecord storedPlacement)
    {
        if (!storedPlacement.HasValidBounds)
        {
            return false;
        }

        try
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero)
            {
                return false;
            }

            var normalRect = new NativeRect(
                storedPlacement.Left,
                storedPlacement.Top,
                storedPlacement.Right,
                storedPlacement.Bottom);
            var monitor = MonitorFromRect(
                ref normalRect,
                MonitorDefaultToNearest);
            if (monitor == IntPtr.Zero)
            {
                return false;
            }

            var monitorInfo = new NativeMonitorInfo
            {
                Size = Marshal.SizeOf<NativeMonitorInfo>(),
            };
            if (!GetMonitorInfo(monitor, ref monitorInfo))
            {
                return false;
            }

            var placement = ConstrainToWorkArea(
                storedPlacement,
                monitorInfo.WorkArea);
            var nativePlacement = new NativeWindowPlacement
            {
                Length = Marshal.SizeOf<NativeWindowPlacement>(),
                ShowCommand = placement.IsMaximized
                    ? SwShowMaximized
                    : SwShowNormal,
                NormalPosition = new NativeRect(
                    placement.Left,
                    placement.Top,
                    placement.Right,
                    placement.Bottom),
            };
            return SetWindowPlacement(handle, ref nativePlacement);
        }
        catch (Exception exception) when (
            exception is DllNotFoundException
                or EntryPointNotFoundException
                or ExternalException)
        {
            return false;
        }
    }

    private static bool TryCapture(
        Window window,
        out WindowPlacementRecord placement)
    {
        placement = new WindowPlacementRecord(0, 0, 0, 0, false);

        try
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero)
            {
                return false;
            }

            var nativePlacement = new NativeWindowPlacement
            {
                Length = Marshal.SizeOf<NativeWindowPlacement>(),
            };
            if (!GetWindowPlacement(handle, ref nativePlacement))
            {
                return false;
            }

            var bounds = nativePlacement.NormalPosition;
            placement = new WindowPlacementRecord(
                bounds.Left,
                bounds.Top,
                bounds.Right,
                bounds.Bottom,
                nativePlacement.ShowCommand == SwShowMaximized ||
                (nativePlacement.Flags & WindowPlacementRestoreToMaximized) != 0);
            return placement.HasValidBounds;
        }
        catch (Exception exception) when (
            exception is DllNotFoundException
                or EntryPointNotFoundException
                or ExternalException)
        {
            return false;
        }
    }

    private sealed class WindowPlacementRegistration
    {
        private readonly Window _window;
        private readonly string _key;
        private readonly WindowPlacementStore _store;
        private readonly bool _restoreSize;
        private readonly DispatcherTimer _saveTimer;
        private bool _sourceInitialized;
        private bool _restoring;
        private bool _detached;

        public WindowPlacementRegistration(
            Window window,
            string key,
            WindowPlacementStore store,
            bool restoreSize)
        {
            _window = window;
            _key = key;
            _store = store;
            _restoreSize = restoreSize;
            _saveTimer = new DispatcherTimer(
                TimeSpan.FromMilliseconds(400),
                DispatcherPriority.Background,
                OnSaveTimerTick,
                window.Dispatcher);
            _saveTimer.Stop();
        }

        public void Attach()
        {
            _window.SourceInitialized += OnSourceInitialized;
            _window.Deactivated += OnDeactivated;
            _window.IsVisibleChanged += OnIsVisibleChanged;
            _window.LocationChanged += OnBoundsChanged;
            _window.SizeChanged += OnBoundsChanged;
            _window.StateChanged += OnStateChanged;
            _window.Closed += OnClosed;
            if (new WindowInteropHelper(_window).Handle != IntPtr.Zero)
            {
                InitializeFromCurrentSource();
            }
        }

        private void OnSourceInitialized(object? sender, EventArgs e)
        {
            InitializeFromCurrentSource();
        }

        private void InitializeFromCurrentSource()
        {
            if (_sourceInitialized)
            {
                return;
            }

            _sourceInitialized = true;
            if (!_store.TryGet(_key, out var placement))
            {
                return;
            }

            _restoring = true;
            try
            {
                _ = _restoreSize
                    ? TryRestore(_window, placement)
                    : TryRestorePosition(_window, placement);
            }
            finally
            {
                _restoring = false;
            }
        }

        private void OnDeactivated(object? sender, EventArgs e)
        {
            Save();
        }

        private void OnIsVisibleChanged(
            object sender,
            DependencyPropertyChangedEventArgs e)
        {
            if (e.NewValue is false)
            {
                Save();
            }
        }

        private void OnStateChanged(object? sender, EventArgs e)
        {
            Save();
        }

        private void OnBoundsChanged(object? sender, EventArgs e)
        {
            if (!_sourceInitialized || _restoring || _detached)
            {
                return;
            }

            _saveTimer.Stop();
            _saveTimer.Start();
        }

        private void OnSaveTimerTick(object? sender, EventArgs e)
        {
            _saveTimer.Stop();
            Save();
        }

        private void OnClosed(object? sender, EventArgs e)
        {
            Save();
            Detach();
        }

        private void Save()
        {
            if (!_sourceInitialized || _restoring || _detached)
            {
                return;
            }

            if (TryCapture(_window, out var placement))
            {
                _ = _store.TrySave(_key, placement);
            }
        }

        private void Detach()
        {
            if (_detached)
            {
                return;
            }

            _detached = true;
            _saveTimer.Stop();
            _saveTimer.Tick -= OnSaveTimerTick;
            _window.SourceInitialized -= OnSourceInitialized;
            _window.Deactivated -= OnDeactivated;
            _window.IsVisibleChanged -= OnIsVisibleChanged;
            _window.LocationChanged -= OnBoundsChanged;
            _window.SizeChanged -= OnBoundsChanged;
            _window.StateChanged -= OnStateChanged;
            _window.Closed -= OnClosed;
        }

        public void StopTrackingAndForget()
        {
            if (_detached)
            {
                return;
            }

            Detach();
            _ = _store.TryRemove(_key);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal readonly record struct NativePoint(int X, int Y);

    [StructLayout(LayoutKind.Sequential)]
    internal readonly record struct NativeRect(
        int Left,
        int Top,
        int Right,
        int Bottom)
    {
        public int Width => Right - Left;

        public int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeWindowPlacement
    {
        public int Length;
        public int Flags;
        public int ShowCommand;
        public NativePoint MinimumPosition;
        public NativePoint MaximumPosition;
        public NativeRect NormalPosition;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeMonitorInfo
    {
        public int Size;
        public NativeRect MonitorArea;
        public NativeRect WorkArea;
        public uint Flags;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowPlacement(
        IntPtr window,
        ref NativeWindowPlacement placement);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPlacement(
        IntPtr window,
        ref NativeWindowPlacement placement);

    private const uint SetWindowPositionNoSize = 0x0001;
    private const uint SetWindowPositionNoZOrder = 0x0004;
    private const uint SetWindowPositionNoActivate = 0x0010;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(
        IntPtr window,
        out NativeRect rectangle);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr window,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromRect(
        ref NativeRect rectangle,
        uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(
        IntPtr monitor,
        ref NativeMonitorInfo monitorInfo);
}
