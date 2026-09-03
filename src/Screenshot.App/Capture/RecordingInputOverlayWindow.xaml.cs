using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Screenshot.App.Infrastructure;
using DrawingRectangle = System.Drawing.Rectangle;
using WpfPoint = System.Windows.Point;
using WpfColor = System.Windows.Media.Color;
using WinForms = System.Windows.Forms;

namespace Screenshot.App.Capture;

public partial class RecordingInputOverlayWindow : Window
{
    private const int ExtendedWindowStyleIndex = -20;
    private const int NonClientHitTestMessage = 0x0084;
    private const int HitTestTransparent = -1;
    private const long ExtendedStyleTransparent = 0x00000020L;
    private const long ExtendedStyleToolWindow = 0x00000080L;
    private const long ExtendedStyleNoActivate = 0x08000000L;
    private const int TopmostWindow = -1;
    private const uint DoNotActivate = 0x0010;
    private const uint DoNotChangeOwnerZOrder = 0x0200;
    private const uint DoNotMove = 0x0002;
    private const uint DoNotResize = 0x0001;
    private const int MaximumTrailSegments = 32;
    private static readonly TimeSpan TrailLifetime = TimeSpan.FromMilliseconds(320);

    private readonly DispatcherTimer _clearTimer;
    private readonly DispatcherTimer _trailTimer;
    private readonly DispatcherTimer _mousePositionTimer;
    private readonly DrawingRectangle _windowBounds;
    private readonly MonitorDpiScale _dpi;
    private readonly Queue<TrailSegment> _trailSegments = [];
    private bool _showMouseTrail;
    private WpfPoint? _lastTrailPoint;
    private bool _isPaused;
    private HwndSource? _windowSource;

    public RecordingInputOverlayWindow(
        ScreenRegion recordingRegion)
    {
        var recordingBounds = new DrawingRectangle(
            recordingRegion.X,
            recordingRegion.Y,
            recordingRegion.Width,
            recordingRegion.Height);
        _windowBounds = recordingBounds;
        _dpi = MonitorGeometryService.GetDpiScale(recordingBounds);
        Width = _windowBounds.Width / _dpi.X;
        Height = _windowBounds.Height / _dpi.Y;
        Left = _windowBounds.X / _dpi.X;
        Top = _windowBounds.Y / _dpi.Y;
        InitializeComponent();
        _clearTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(850),
        };
        _clearTimer.Tick += OnClearTimerTick;
        var interactionFrameInterval =
            DisplayRefreshRateService.GetInteractionFrameInterval(recordingBounds);
        _trailTimer = new DispatcherTimer
        {
            Interval = interactionFrameInterval,
        };
        _trailTimer.Tick += OnTrailTimerTick;
        _mousePositionTimer = new DispatcherTimer
        {
            Interval = interactionFrameInterval,
        };
        _mousePositionTimer.Tick += OnMousePositionTimerTick;
        SourceInitialized += OnSourceInitialized;
    }

    internal IntPtr EnsureWindowHandle() =>
        new WindowInteropHelper(this).EnsureHandle();

    public void ShowInput(string displayText, bool isTransient)
    {
        if (_isPaused)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(displayText))
        {
            _clearTimer.Stop();
            _clearTimer.Start();
            return;
        }

        _clearTimer.Stop();
        InputText.Text = displayText;
        InputSurface.Visibility = Visibility.Visible;
        InputSurface.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(1, TimeSpan.FromMilliseconds(90)));
        if (isTransient)
        {
            _clearTimer.Start();
        }
    }

    public void SetPaused(bool isPaused)
    {
        _isPaused = isPaused;
        if (isPaused)
        {
            _mousePositionTimer.Stop();
            ClearInput();
            ClearMouseTrail();
        }
        else if (_showMouseTrail)
        {
            _mousePositionTimer.Start();
        }
    }

    public void SetMouseTrailEnabled(bool isEnabled)
    {
        _showMouseTrail = isEnabled;
        if (isEnabled && !_isPaused)
        {
            _mousePositionTimer.Start();
        }
        else
        {
            _mousePositionTimer.Stop();
            ClearMouseTrail();
        }
    }

    internal void EnsureTopmost()
    {
        if (!IsLoaded)
        {
            return;
        }

        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        _ = NativeMethods.SetWindowPos(
            handle,
            new IntPtr(TopmostWindow),
            0,
            0,
            0,
            0,
            DoNotActivate |
            DoNotChangeOwnerZOrder |
            DoNotMove |
            DoNotResize);
    }

    public void ShowMousePosition(int screenX, int screenY)
    {
        if (_isPaused || !_windowBounds.Contains(screenX, screenY))
        {
            _lastTrailPoint = null;
            return;
        }

        var point = new WpfPoint(
            (screenX - _windowBounds.X) / _dpi.X,
            (screenY - _windowBounds.Y) / _dpi.Y);
        if (_lastTrailPoint is not WpfPoint previous)
        {
            _lastTrailPoint = point;
            return;
        }

        if ((point - previous).Length < 2)
        {
            return;
        }

        var line = new Line
        {
            X1 = previous.X,
            Y1 = previous.Y,
            X2 = point.X,
            Y2 = point.Y,
            Stroke = CreateTrailBrush(),
            StrokeThickness = 3.5,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            IsHitTestVisible = false,
        };
        var segment = new TrailSegment(line, DateTime.UtcNow);
        _trailSegments.Enqueue(segment);
        MouseTrailCanvas.Children.Add(line);
        _lastTrailPoint = point;
        while (_trailSegments.Count > MaximumTrailSegments)
        {
            RemoveOldestTrailSegment();
        }
        UpdateTrailOpacity();
        _trailTimer.Start();
    }

    protected override void OnClosed(EventArgs e)
    {
        SourceInitialized -= OnSourceInitialized;
        _windowSource?.RemoveHook(OnWindowMessage);
        _windowSource = null;
        _clearTimer.Stop();
        _clearTimer.Tick -= OnClearTimerTick;
        _trailTimer.Stop();
        _trailTimer.Tick -= OnTrailTimerTick;
        _mousePositionTimer.Stop();
        _mousePositionTimer.Tick -= OnMousePositionTimerTick;
        _trailSegments.Clear();
        base.OnClosed(e);
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        _windowSource = HwndSource.FromHwnd(handle);
        _windowSource?.AddHook(OnWindowMessage);
        var extendedStyle = NativeMethods.GetWindowLongPtr(
            handle,
            ExtendedWindowStyleIndex).ToInt64();
        _ = NativeMethods.SetWindowLongPtr(
            handle,
            ExtendedWindowStyleIndex,
            new IntPtr(
                extendedStyle |
                ExtendedStyleTransparent |
                ExtendedStyleToolWindow |
                ExtendedStyleNoActivate));
        _ = NativeMethods.SetWindowPos(
            handle,
            new IntPtr(TopmostWindow),
            _windowBounds.X,
            _windowBounds.Y,
            _windowBounds.Width,
            _windowBounds.Height,
            DoNotActivate | DoNotChangeOwnerZOrder);
    }

    private IntPtr OnWindowMessage(
        IntPtr window,
        int message,
        IntPtr wordParameter,
        IntPtr longParameter,
        ref bool handled)
    {
        if (message != NonClientHitTestMessage)
        {
            return IntPtr.Zero;
        }

        // This window only draws input feedback inside the video. It must
        // never become the target of Windows cursor or mouse processing.
        handled = true;
        return new IntPtr(HitTestTransparent);
    }

    private void OnClearTimerTick(object? sender, EventArgs e)
    {
        _clearTimer.Stop();
        ClearInput();
    }

    private void OnTrailTimerTick(object? sender, EventArgs e)
    {
        var now = DateTime.UtcNow;
        while (_trailSegments.TryPeek(out var item) &&
               now - item.Timestamp >= TrailLifetime)
        {
            RemoveOldestTrailSegment();
        }

        UpdateTrailOpacity();

        if (_trailSegments.Count == 0)
        {
            ClearMouseTrail();
        }
    }

    private void OnMousePositionTimerTick(object? sender, EventArgs e)
    {
        var cursor = WinForms.Cursor.Position;
        ShowMousePosition(cursor.X, cursor.Y);
    }

    private void ClearInput()
    {
        _clearTimer.Stop();
        InputSurface.BeginAnimation(OpacityProperty, null);
        InputSurface.Opacity = 0;
        InputSurface.Visibility = Visibility.Collapsed;
        InputText.Text = string.Empty;
    }

    private void ClearMouseTrail()
    {
        _trailTimer.Stop();
        _trailSegments.Clear();
        MouseTrailCanvas.Children.Clear();
        _lastTrailPoint = null;
    }

    private void RemoveOldestTrailSegment()
    {
        if (_trailSegments.TryDequeue(out var segment))
        {
            MouseTrailCanvas.Children.Remove(segment.Line);
        }
    }

    private SolidColorBrush CreateTrailBrush()
    {
        var accent = ResolveThemeAccentColor();
        var trailBrush = new SolidColorBrush(accent);
        trailBrush.Freeze();
        return trailBrush;
    }

    private WpfColor ResolveThemeAccentColor()
    {
        if (TryFindResource("EditorToolbarButtonHoverBorderBrush") is
            SolidColorBrush toolbarAccent)
        {
            return toolbarAccent.Color;
        }

        return TryFindResource("AppAccentBrush") switch
        {
            SolidColorBrush accent => accent.Color,
            GradientBrush gradient when gradient.GradientStops.Count > 0 =>
                gradient.GradientStops[0].Color,
            _ => WpfColor.FromRgb(240, 68, 85),
        };
    }

    private void UpdateTrailOpacity()
    {
        var count = _trailSegments.Count;
        var index = 0;
        foreach (var segment in _trailSegments)
        {
            segment.Line.Opacity = CalculateTrailOpacity(index, count);
            index++;
        }
    }

    internal static double CalculateTrailOpacity(int index, int count)
    {
        if (count <= 0 || index < 0 || index >= count)
        {
            return 0;
        }

        var progress = (index + 1d) / count;
        return 0.05 + (0.95 * progress * progress);
    }

    private sealed record TrailSegment(Line Line, DateTime Timestamp);

    private static class NativeMethods
    {
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetWindowPos(
            IntPtr window,
            IntPtr insertAfter,
            int x,
            int y,
            int width,
            int height,
            uint flags);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
        private static extern IntPtr GetWindowLongPtr64(IntPtr window, int index);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
        private static extern int GetWindowLong32(IntPtr window, int index);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
        private static extern IntPtr SetWindowLongPtr64(
            IntPtr window,
            int index,
            IntPtr value);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
        private static extern int SetWindowLong32(
            IntPtr window,
            int index,
            int value);

        public static IntPtr GetWindowLongPtr(IntPtr window, int index) =>
            IntPtr.Size == 8
                ? GetWindowLongPtr64(window, index)
                : new IntPtr(GetWindowLong32(window, index));

        public static IntPtr SetWindowLongPtr(
            IntPtr window,
            int index,
            IntPtr value) =>
            IntPtr.Size == 8
                ? SetWindowLongPtr64(window, index, value)
                : new IntPtr(SetWindowLong32(window, index, value.ToInt32()));
    }
}
