using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Screenshot.App.Infrastructure;
using DrawingRectangle = System.Drawing.Rectangle;
using WinForms = System.Windows.Forms;

namespace Screenshot.App.Capture;

public partial class CaptureFeedbackWindow : Window
{
    private const int ExtendedWindowStyleIndex = -20;
    private const long ExtendedStyleTransparent = 0x00000020L;
    private const long ExtendedStyleToolWindow = 0x00000080L;
    private const long ExtendedStyleNoActivate = 0x08000000L;
    private const uint SetWindowPositionNoZOrder = 0x0004;
    private const uint SetWindowPositionNoActivate = 0x0010;
    private static readonly TimeSpan ExpandDuration = TimeSpan.FromMilliseconds(260);
    private static readonly TimeSpan ToastFadeDuration = TimeSpan.FromMilliseconds(140);
    private static readonly TimeSpan HoldDuration = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan CloseFadeDuration = TimeSpan.FromMilliseconds(180);

    private readonly TaskCompletionSource _completion = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly DrawingRectangle _screenBounds;
    private readonly Rect _selectionBounds;
    private bool _animationStarted;
    private bool _closed;

    internal CaptureFeedbackWindow(ScreenRegion captureRegion)
    {
        InitializeComponent();

        var physicalCaptureBounds = new DrawingRectangle(
            captureRegion.X,
            captureRegion.Y,
            captureRegion.Width,
            captureRegion.Height);
        _screenBounds = WinForms.Screen.FromRectangle(physicalCaptureBounds).Bounds;
        var visibleCaptureBounds = DrawingRectangle.Intersect(
            physicalCaptureBounds,
            _screenBounds);
        var dpi = MonitorGeometryService.GetDpiScale(_screenBounds);
        Width = _screenBounds.Width / dpi.X;
        Height = _screenBounds.Height / dpi.Y;
        Left = _screenBounds.Left / dpi.X;
        Top = _screenBounds.Top / dpi.Y;
        _selectionBounds = new Rect(
            (visibleCaptureBounds.Left - _screenBounds.Left) / dpi.X,
            (visibleCaptureBounds.Top - _screenBounds.Top) / dpi.Y,
            visibleCaptureBounds.Width / dpi.X,
            visibleCaptureBounds.Height / dpi.Y);

        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
    }

    public static Task ShowAsync(ScreenRegion captureRegion)
    {
        if (captureRegion.IsEmpty)
        {
            return Task.CompletedTask;
        }

        var window = new CaptureFeedbackWindow(captureRegion);
        window.Show();
        return window._completion.Task;
    }

    internal static System.Windows.Point CalculateToastPosition(
        Rect selectionBounds,
        System.Windows.Size toastSize,
        System.Windows.Size surfaceSize,
        double gap = 12,
        double margin = 12)
    {
        var maximumX = Math.Max(margin, surfaceSize.Width - toastSize.Width - margin);
        var x = Math.Clamp(
            selectionBounds.Left + ((selectionBounds.Width - toastSize.Width) / 2),
            margin,
            maximumX);
        var below = selectionBounds.Bottom + gap;
        if (below + toastSize.Height <= surfaceSize.Height - margin)
        {
            return new System.Windows.Point(x, below);
        }

        var above = selectionBounds.Top - gap - toastSize.Height;
        if (above >= margin)
        {
            return new System.Windows.Point(x, above);
        }

        var maximumY = Math.Max(margin, surfaceSize.Height - toastSize.Height - margin);
        return new System.Windows.Point(
            x,
            Math.Clamp(
                selectionBounds.Bottom - toastSize.Height - margin,
                margin,
                maximumY));
    }

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        SourceInitialized -= OnSourceInitialized;
        Loaded -= OnLoaded;
        _completion.TrySetResult();
        base.OnClosed(e);
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        SourceInitialized -= OnSourceInitialized;
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

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
            IntPtr.Zero,
            _screenBounds.Left,
            _screenBounds.Top,
            _screenBounds.Width,
            _screenBounds.Height,
            SetWindowPositionNoZOrder | SetWindowPositionNoActivate);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        if (_animationStarted)
        {
            return;
        }

        _animationStarted = true;
        try
        {
            await PlayFeedbackAsync();
        }
        catch (Exception exception)
        {
            _completion.TrySetException(exception);
        }
        finally
        {
            if (!_closed)
            {
                Close();
            }
        }
    }

    private async Task PlayFeedbackAsync()
    {
        await Dispatcher.Yield(DispatcherPriority.Render);
        if (_closed)
        {
            return;
        }

        Canvas.SetLeft(SelectionFrame, _selectionBounds.Left);
        Canvas.SetTop(SelectionFrame, _selectionBounds.Top);
        var toastPosition = CalculateToastPosition(
            _selectionBounds,
            new System.Windows.Size(CaptureToast.Width, CaptureToast.Height),
            new System.Windows.Size(ActualWidth, ActualHeight));
        Canvas.SetLeft(CaptureToast, toastPosition.X);
        Canvas.SetTop(CaptureToast, toastPosition.Y);

        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        SelectionFrame.BeginAnimation(
            WidthProperty,
            new DoubleAnimation(_selectionBounds.Width, ExpandDuration)
            {
                EasingFunction = easing,
            });
        SelectionFrame.BeginAnimation(
            HeightProperty,
            new DoubleAnimation(_selectionBounds.Height, ExpandDuration)
            {
                EasingFunction = easing,
            });

        await Task.Delay(ExpandDuration);
        if (_closed)
        {
            return;
        }

        CaptureToast.Visibility = Visibility.Visible;
        CaptureToast.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(1, ToastFadeDuration)
            {
                EasingFunction = easing,
            });
        await Task.Delay(ToastFadeDuration + HoldDuration);
        if (_closed)
        {
            return;
        }

        FeedbackRoot.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(0, CloseFadeDuration)
            {
                EasingFunction = new CubicEase
                {
                    EasingMode = EasingMode.EaseIn,
                },
            });
        await Task.Delay(CloseFadeDuration);
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
        private static extern IntPtr GetWindowLongPtr64(
            IntPtr window,
            int index);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
        private static extern int GetWindowLong32(
            IntPtr window,
            int index);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
        private static extern IntPtr SetWindowLongPtr64(
            IntPtr window,
            int index,
            IntPtr newLong);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
        private static extern int SetWindowLong32(
            IntPtr window,
            int index,
            int newLong);

        public static IntPtr GetWindowLongPtr(IntPtr window, int index)
        {
            return IntPtr.Size == 8
                ? GetWindowLongPtr64(window, index)
                : new IntPtr(GetWindowLong32(window, index));
        }

        public static IntPtr SetWindowLongPtr(
            IntPtr window,
            int index,
            IntPtr newLong)
        {
            return IntPtr.Size == 8
                ? SetWindowLongPtr64(window, index, newLong)
                : new IntPtr(SetWindowLong32(
                    window,
                    index,
                    unchecked((int)newLong.ToInt64())));
        }

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
    }
}
