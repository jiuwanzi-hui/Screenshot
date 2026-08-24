using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Win32;
using Screenshot.App.Infrastructure;
using DrawingPoint = System.Drawing.Point;
using DrawingRectangle = System.Drawing.Rectangle;
using DrawingSize = System.Drawing.Size;
using WinForms = System.Windows.Forms;

namespace Screenshot.App.Presentation;

internal enum FloatingDockEdge
{
    None,
    Top,
    Right,
    Bottom,
    Left,
}

internal enum FloatingMenuDirection
{
    Top,
    Right,
    Bottom,
    Left,
}

internal static class FloatingCaptureLayout
{
    private static readonly FloatingMenuDirection[] MenuPriority =
    [
        FloatingMenuDirection.Top,
        FloatingMenuDirection.Right,
        FloatingMenuDirection.Bottom,
        FloatingMenuDirection.Left,
    ];

    public static DrawingRectangle ConstrainToWorkArea(
        DrawingRectangle bounds,
        DrawingRectangle workArea)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0 ||
            workArea.Width <= 0 || workArea.Height <= 0)
        {
            return bounds;
        }

        var width = Math.Min(bounds.Width, workArea.Width);
        var height = Math.Min(bounds.Height, workArea.Height);
        var x = Math.Clamp(
            bounds.X,
            workArea.Left,
            Math.Max(workArea.Left, workArea.Right - width));
        var y = Math.Clamp(
            bounds.Y,
            workArea.Top,
            Math.Max(workArea.Top, workArea.Bottom - height));
        return new DrawingRectangle(x, y, width, height);
    }

    public static FloatingDockEdge FindNearestDockEdge(
        DrawingRectangle bounds,
        DrawingRectangle workArea,
        int threshold)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0 ||
            workArea.Width <= 0 || workArea.Height <= 0 ||
            threshold < 0)
        {
            return FloatingDockEdge.None;
        }

        var candidates = new (FloatingDockEdge Edge, int Distance)[]
        {
            (FloatingDockEdge.Top, Math.Abs(bounds.Top - workArea.Top)),
            (FloatingDockEdge.Right, Math.Abs(workArea.Right - bounds.Right)),
            (FloatingDockEdge.Bottom, Math.Abs(workArea.Bottom - bounds.Bottom)),
            (FloatingDockEdge.Left, Math.Abs(bounds.Left - workArea.Left)),
        };
        var nearest = candidates.MinBy(candidate => candidate.Distance);
        return nearest.Distance <= threshold
            ? nearest.Edge
            : FloatingDockEdge.None;
    }

    public static DrawingRectangle DockToWorkArea(
        DrawingRectangle bounds,
        DrawingRectangle workArea,
        FloatingDockEdge edge)
    {
        var constrained = ConstrainToWorkArea(bounds, workArea);
        return edge switch
        {
            FloatingDockEdge.Top => constrained with { Y = workArea.Top },
            FloatingDockEdge.Right => constrained with
            {
                X = workArea.Right - constrained.Width,
            },
            FloatingDockEdge.Bottom => constrained with
            {
                Y = workArea.Bottom - constrained.Height,
            },
            FloatingDockEdge.Left => constrained with { X = workArea.Left },
            _ => constrained,
        };
    }

    public static FloatingMenuDirection ChooseMenuDirection(
        DrawingRectangle buttonBounds,
        DrawingSize menuSize,
        DrawingRectangle workArea,
        int gap)
    {
        foreach (var direction in MenuPriority)
        {
            if (workArea.Contains(CreateMenuBounds(
                    buttonBounds,
                    menuSize,
                    direction,
                    gap)))
            {
                return direction;
            }
        }

        var bestDirection = MenuPriority[0];
        long bestVisibleArea = -1;
        foreach (var direction in MenuPriority)
        {
            var candidate = CreateMenuBounds(
                buttonBounds,
                menuSize,
                direction,
                gap);
            var visible = DrawingRectangle.Intersect(candidate, workArea);
            var visibleArea = (long)Math.Max(0, visible.Width) *
                              Math.Max(0, visible.Height);
            if (visibleArea > bestVisibleArea)
            {
                bestVisibleArea = visibleArea;
                bestDirection = direction;
            }
        }

        return bestDirection;
    }

    internal static DrawingRectangle CreateMenuBounds(
        DrawingRectangle buttonBounds,
        DrawingSize menuSize,
        FloatingMenuDirection direction,
        int gap)
    {
        var centeredX = buttonBounds.Left +
                        ((buttonBounds.Width - menuSize.Width) / 2);
        var centeredY = buttonBounds.Top +
                        ((buttonBounds.Height - menuSize.Height) / 2);
        return direction switch
        {
            FloatingMenuDirection.Top => new DrawingRectangle(
                centeredX,
                buttonBounds.Top - gap - menuSize.Height,
                menuSize.Width,
                menuSize.Height),
            FloatingMenuDirection.Right => new DrawingRectangle(
                buttonBounds.Right + gap,
                centeredY,
                menuSize.Width,
                menuSize.Height),
            FloatingMenuDirection.Bottom => new DrawingRectangle(
                centeredX,
                buttonBounds.Bottom + gap,
                menuSize.Width,
                menuSize.Height),
            _ => new DrawingRectangle(
                buttonBounds.Left - gap - menuSize.Width,
                centeredY,
                menuSize.Width,
                menuSize.Height),
        };
    }
}

public partial class FloatingCaptureWindow : Window
{
    private const double CollapsedOpacity = 0.25;
    private const double CollapsedVisibleRatio = 0.75;
    private const double DockThresholdDip = 26;
    private const double MenuGapDip = 7;
    private const uint SetWindowPositionNoSize = 0x0001;
    private const uint SetWindowPositionNoZOrder = 0x0004;
    private const uint SetWindowPositionNoActivate = 0x0010;

    private readonly DispatcherTimer _menuCloseTimer;
    private readonly DispatcherTimer _dockCollapseTimer;
    private readonly bool _hasSavedPosition;
    private DrawingPoint _dragStartCursor;
    private DrawingRectangle _dragStartBounds;
    private FloatingDockEdge _dockEdge;
    private bool _isDragging;
    private bool _isCaptureHidden;
    private bool _isContextMenuOpen;
    private bool _isDisplaySettingsSubscribed;
    private int _recordingFeedbackVersion;

    public FloatingCaptureWindow()
    {
        InitializeComponent();
        _menuCloseTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(240),
        };
        _menuCloseTimer.Tick += OnMenuCloseTimerTick;
        _dockCollapseTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(360),
        };
        _dockCollapseTimer.Tick += OnDockCollapseTimerTick;
        _hasSavedPosition = WindowPlacementService.TrackPosition(
            this,
            WindowPlacementKeys.FloatingCapture);
        Loaded += OnLoaded;
        MouseLeave += OnWindowMouseLeave;
    }

    public event EventHandler? RepeatCaptureRequested;

    public event EventHandler? RegionCaptureRequested;

    public event EventHandler? ScrollCaptureRequested;

    public event EventHandler? VideoRecordingRequested;

    public event EventHandler? PinCaptureRequested;

    public event EventHandler? AllScreensCaptureRequested;

    public event EventHandler? HistoryRequested;

    public event EventHandler? SettingsRequested;

    public event EventHandler? TextTranslationRequested;

    public event EventHandler? CloseRequested;

    public void SetCaptureInProgress(bool isInProgress)
    {
        if (isInProgress)
        {
            _isCaptureHidden = IsVisible;
            FeatureMenuPopup.IsOpen = false;
            FloatingButtonContextMenu.IsOpen = false;
            Hide();
        }
        else if (_isCaptureHidden)
        {
            _isCaptureHidden = false;
            Show();
            _ = Dispatcher.BeginInvoke(
                DispatcherPriority.Loaded,
                () => ReconcileWithDisplays(snapNearby: false));
        }
    }

    internal async void ShowRecordingAlreadyActiveFeedback()
    {
        if (!IsLoaded || Dispatcher.HasShutdownStarted)
        {
            return;
        }

        var version = ++_recordingFeedbackVersion;
        var wasHidden = !IsVisible;
        if (wasHidden)
        {
            Show();
            ReconcileWithDisplays(snapNearby: false);
        }

        var previousContent = VideoRecordingMenuButton.Content;
        var previousToolTip = VideoRecordingMenuButton.ToolTip;
        VideoRecordingMenuButton.Content = "正在录制";
        VideoRecordingMenuButton.ToolTip = "当前已经有一个录屏任务";
        FeatureMenuPopup.IsOpen = true;
        try
        {
            await Task.Delay(1400);
        }
        catch (TaskCanceledException)
        {
            return;
        }

        if (version != _recordingFeedbackVersion || !IsLoaded)
        {
            return;
        }

        VideoRecordingMenuButton.Content = previousContent;
        VideoRecordingMenuButton.ToolTip = previousToolTip;
        FeatureMenuPopup.IsOpen = false;
        if (wasHidden && _isCaptureHidden)
        {
            Hide();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        Loaded -= OnLoaded;
        MouseLeave -= OnWindowMouseLeave;
        _menuCloseTimer.Stop();
        _menuCloseTimer.Tick -= OnMenuCloseTimerTick;
        _dockCollapseTimer.Stop();
        _dockCollapseTimer.Tick -= OnDockCollapseTimerTick;
        if (_isDisplaySettingsSubscribed)
        {
            SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            _isDisplaySettingsSubscribed = false;
        }

        FeatureMenuPopup.IsOpen = false;
        base.OnClosed(e);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!TryGetWindowBounds(out var bounds))
        {
            return;
        }

        if (!_hasSavedPosition)
        {
            var workArea = WinForms.Screen.PrimaryScreen?.WorkingArea ??
                WinForms.SystemInformation.VirtualScreen;
            bounds.X = workArea.Right - bounds.Width;
            bounds.Y = workArea.Top + ((workArea.Height - bounds.Height) / 2);
            MoveWindow(bounds);
        }

        ReconcileWithDisplays(snapNearby: true);
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        _isDisplaySettingsSubscribed = true;
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        _ = Dispatcher.BeginInvoke(
            DispatcherPriority.Background,
            () => ReconcileWithDisplays(snapNearby: true));
    }

    private void ReconcileWithDisplays(bool snapNearby)
    {
        if (!IsLoaded || !TryGetWindowBounds(out var bounds))
        {
            return;
        }

        var workArea = WinForms.Screen.FromRectangle(bounds).WorkingArea;
        var edge = _dockEdge;
        if (edge == FloatingDockEdge.None && snapNearby)
        {
            edge = FloatingCaptureLayout.FindNearestDockEdge(
                bounds,
                workArea,
                GetDockThresholdPhysical());
        }

        var nextBounds = edge == FloatingDockEdge.None
            ? FloatingCaptureLayout.ConstrainToWorkArea(bounds, workArea)
            : FloatingCaptureLayout.DockToWorkArea(bounds, workArea, edge);
        MoveWindow(nextBounds);
        SetDockEdge(edge, collapse: edge != FloatingDockEdge.None);
    }

    private void OnFloatingButtonMouseEnter(
        object sender,
        System.Windows.Input.MouseEventArgs e)
    {
        _menuCloseTimer.Stop();
        _dockCollapseTimer.Stop();
        SetDockVisual(collapsed: false, animated: true);
        if (_isDragging || _isContextMenuOpen)
        {
            return;
        }

        UpdateMenuPlacement();
        FeatureMenuPopup.IsOpen = true;
    }

    private void UpdateMenuPlacement()
    {
        FeatureMenuPopup.Child.Measure(new System.Windows.Size(
            double.PositiveInfinity,
            double.PositiveInfinity));
        var dpi = VisualTreeHelper.GetDpi(this);
        var desiredSize = FeatureMenuPopup.Child.DesiredSize;
        var menuSize = new DrawingSize(
            Math.Max(1, (int)Math.Ceiling(desiredSize.Width * dpi.DpiScaleX)),
            Math.Max(1, (int)Math.Ceiling(desiredSize.Height * dpi.DpiScaleY)));
        var buttonBounds = GetButtonScreenBounds();
        var workingArea = WinForms.Screen.FromRectangle(buttonBounds).WorkingArea;
        var direction = FloatingCaptureLayout.ChooseMenuDirection(
            buttonBounds,
            menuSize,
            workingArea,
            Math.Max(1, (int)Math.Round(MenuGapDip * dpi.DpiScaleX)));
        FeatureMenuPopup.Placement = direction switch
        {
            FloatingMenuDirection.Top => PlacementMode.Top,
            FloatingMenuDirection.Right => PlacementMode.Right,
            FloatingMenuDirection.Bottom => PlacementMode.Bottom,
            _ => PlacementMode.Left,
        };
    }

    private void OnFloatingButtonContextMenuOpening(
        object sender,
        RoutedEventArgs e)
    {
        _menuCloseTimer.Stop();
        _dockCollapseTimer.Stop();
        SetDockVisual(collapsed: false, animated: true);
        FeatureMenuPopup.IsOpen = false;
    }

    private void OnFloatingButtonPreviewMouseRightButtonDown(
        object sender,
        MouseButtonEventArgs e)
    {
        _menuCloseTimer.Stop();
        _dockCollapseTimer.Stop();
        FeatureMenuPopup.IsOpen = false;
    }

    private void OnFloatingContextMenuOpened(object sender, RoutedEventArgs e)
    {
        _isContextMenuOpen = true;
        FeatureMenuPopup.IsOpen = false;
        SetDockVisual(collapsed: false, animated: true);
    }

    private void OnFloatingContextMenuClosed(object sender, RoutedEventArgs e)
    {
        _isContextMenuOpen = false;
        ScheduleDockCollapse();
    }

    private void OnFeatureMenuMouseEnter(
        object sender,
        System.Windows.Input.MouseEventArgs e)
    {
        _menuCloseTimer.Stop();
        _dockCollapseTimer.Stop();
    }

    private void OnWindowMouseLeave(
        object sender,
        System.Windows.Input.MouseEventArgs e)
    {
        ScheduleMenuClose();
        ScheduleDockCollapse();
    }

    private void OnFeatureMenuMouseLeave(
        object sender,
        System.Windows.Input.MouseEventArgs e)
    {
        ScheduleMenuClose();
        ScheduleDockCollapse();
    }

    private void ScheduleMenuClose()
    {
        _menuCloseTimer.Stop();
        _menuCloseTimer.Start();
    }

    private void ScheduleDockCollapse()
    {
        if (_dockEdge == FloatingDockEdge.None)
        {
            return;
        }

        _dockCollapseTimer.Stop();
        _dockCollapseTimer.Start();
    }

    private void OnMenuCloseTimerTick(object? sender, EventArgs e)
    {
        _menuCloseTimer.Stop();
        if (!IsMouseOver && !FeatureMenuPopup.IsMouseOver)
        {
            FeatureMenuPopup.IsOpen = false;
        }
    }

    private void OnDockCollapseTimerTick(object? sender, EventArgs e)
    {
        _dockCollapseTimer.Stop();
        if (!_isDragging && !_isContextMenuOpen &&
            !IsMouseOver && !FeatureMenuPopup.IsMouseOver)
        {
            FeatureMenuPopup.IsOpen = false;
            SetDockVisual(collapsed: true, animated: true);
        }
    }

    private void OnFloatingButtonPreviewMouseLeftButtonDown(
        object sender,
        MouseButtonEventArgs e)
    {
        _menuCloseTimer.Stop();
        _dockCollapseTimer.Stop();
        FeatureMenuPopup.IsOpen = false;
        SetDockVisual(collapsed: false, animated: false);
        _dragStartCursor = WinForms.Cursor.Position;
        _ = TryGetWindowBounds(out _dragStartBounds);
        _isDragging = false;
        FloatingButton.CaptureMouse();
        e.Handled = true;
    }

    private void OnFloatingButtonMouseMove(
        object sender,
        System.Windows.Input.MouseEventArgs e)
    {
        if (!FloatingButton.IsMouseCaptured ||
            e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var cursor = WinForms.Cursor.Position;
        var deltaX = cursor.X - _dragStartCursor.X;
        var deltaY = cursor.Y - _dragStartCursor.Y;
        var dpi = VisualTreeHelper.GetDpi(this);
        if (!_isDragging &&
            Math.Abs(deltaX) < SystemParameters.MinimumHorizontalDragDistance *
                               dpi.DpiScaleX &&
            Math.Abs(deltaY) < SystemParameters.MinimumVerticalDragDistance *
                               dpi.DpiScaleY)
        {
            return;
        }

        _isDragging = true;
        _dockEdge = FloatingDockEdge.None;
        var requested = _dragStartBounds with
        {
            X = _dragStartBounds.X + deltaX,
            Y = _dragStartBounds.Y + deltaY,
        };
        var workArea = WinForms.Screen.FromPoint(cursor).WorkingArea;
        MoveWindow(FloatingCaptureLayout.ConstrainToWorkArea(
            requested,
            workArea));
    }

    private void OnFloatingButtonPreviewMouseLeftButtonUp(
        object sender,
        MouseButtonEventArgs e)
    {
        if (!FloatingButton.IsMouseCaptured)
        {
            return;
        }

        FloatingButton.ReleaseMouseCapture();
        var wasDragging = _isDragging;
        _isDragging = false;
        e.Handled = true;
        if (!wasDragging)
        {
            RepeatCaptureRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        SnapAfterDrag();
    }

    private void SnapAfterDrag()
    {
        if (!TryGetWindowBounds(out var bounds))
        {
            return;
        }

        var workArea = WinForms.Screen.FromPoint(WinForms.Cursor.Position).WorkingArea;
        var edge = FloatingCaptureLayout.FindNearestDockEdge(
            bounds,
            workArea,
            GetDockThresholdPhysical());
        var nextBounds = edge == FloatingDockEdge.None
            ? FloatingCaptureLayout.ConstrainToWorkArea(bounds, workArea)
            : FloatingCaptureLayout.DockToWorkArea(bounds, workArea, edge);
        MoveWindow(nextBounds);
        SetDockEdge(edge, collapse: false);
    }

    private void SetDockEdge(FloatingDockEdge edge, bool collapse)
    {
        _dockEdge = edge;
        SetDockVisual(collapse, animated: false);
    }

    private void SetDockVisual(bool collapsed, bool animated)
    {
        if (_dockEdge == FloatingDockEdge.None)
        {
            collapsed = false;
        }

        var hiddenRatio = 1 - CollapsedVisibleRatio;
        var offsetX = 0d;
        var offsetY = 0d;
        if (collapsed)
        {
            switch (_dockEdge)
            {
                case FloatingDockEdge.Top:
                    offsetY = -ActualHeight * hiddenRatio;
                    break;
                case FloatingDockEdge.Right:
                    offsetX = ActualWidth * hiddenRatio;
                    break;
                case FloatingDockEdge.Bottom:
                    offsetY = ActualHeight * hiddenRatio;
                    break;
                case FloatingDockEdge.Left:
                    offsetX = -ActualWidth * hiddenRatio;
                    break;
            }
        }

        var opacity = collapsed ? CollapsedOpacity : 1;
        if (!animated)
        {
            DockTranslateTransform.BeginAnimation(
                TranslateTransform.XProperty,
                null);
            DockTranslateTransform.BeginAnimation(
                TranslateTransform.YProperty,
                null);
            FloatingButton.BeginAnimation(OpacityProperty, null);
            DockTranslateTransform.X = offsetX;
            DockTranslateTransform.Y = offsetY;
            FloatingButton.Opacity = opacity;
            return;
        }

        var duration = new Duration(TimeSpan.FromMilliseconds(150));
        var easing = new CubicEase
        {
            EasingMode = EasingMode.EaseOut,
        };
        DockTranslateTransform.BeginAnimation(
            TranslateTransform.XProperty,
            new DoubleAnimation(offsetX, duration) { EasingFunction = easing });
        DockTranslateTransform.BeginAnimation(
            TranslateTransform.YProperty,
            new DoubleAnimation(offsetY, duration) { EasingFunction = easing });
        FloatingButton.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(opacity, duration) { EasingFunction = easing });
    }

    private int GetDockThresholdPhysical()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        return Math.Max(1, (int)Math.Round(DockThresholdDip *
                                           Math.Max(
                                               dpi.DpiScaleX,
                                               dpi.DpiScaleY)));
    }

    private void OnRegionCaptureClick(object sender, RoutedEventArgs e)
    {
        FeatureMenuPopup.IsOpen = false;
        RegionCaptureRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnScrollCaptureClick(object sender, RoutedEventArgs e)
    {
        FeatureMenuPopup.IsOpen = false;
        ScrollCaptureRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnVideoRecordingClick(object sender, RoutedEventArgs e)
    {
        FeatureMenuPopup.IsOpen = false;
        VideoRecordingRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnPinCaptureClick(object sender, RoutedEventArgs e)
    {
        FeatureMenuPopup.IsOpen = false;
        PinCaptureRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnAllScreensCaptureClick(object sender, RoutedEventArgs e)
    {
        FeatureMenuPopup.IsOpen = false;
        AllScreensCaptureRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnHistoryClick(object sender, RoutedEventArgs e)
    {
        FeatureMenuPopup.IsOpen = false;
        HistoryRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnCloseFloatingButtonClick(object sender, RoutedEventArgs e)
    {
        FeatureMenuPopup.IsOpen = false;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnOpenSettingsClick(object sender, RoutedEventArgs e)
    {
        FeatureMenuPopup.IsOpen = false;
        SettingsRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnOpenTextTranslationClick(object sender, RoutedEventArgs e)
    {
        FeatureMenuPopup.IsOpen = false;
        TextTranslationRequested?.Invoke(this, EventArgs.Empty);
    }

    private DrawingRectangle GetButtonScreenBounds()
    {
        var topLeft = WindowRoot.PointToScreen(new System.Windows.Point(0, 0));
        var bottomRight = WindowRoot.PointToScreen(
            new System.Windows.Point(
                WindowRoot.ActualWidth,
                WindowRoot.ActualHeight));
        return DrawingRectangle.FromLTRB(
            (int)Math.Round(topLeft.X),
            (int)Math.Round(topLeft.Y),
            (int)Math.Round(bottomRight.X),
            (int)Math.Round(bottomRight.Y));
    }

    private bool TryGetWindowBounds(out DrawingRectangle bounds)
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero &&
            NativeMethods.GetWindowRect(handle, out var nativeBounds))
        {
            bounds = DrawingRectangle.FromLTRB(
                nativeBounds.Left,
                nativeBounds.Top,
                nativeBounds.Right,
                nativeBounds.Bottom);
            return bounds.Width > 0 && bounds.Height > 0;
        }

        bounds = DrawingRectangle.Empty;
        return false;
    }

    private void MoveWindow(DrawingRectangle bounds)
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero || bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        _ = NativeMethods.SetWindowPos(
            handle,
            IntPtr.Zero,
            bounds.X,
            bounds.Y,
            0,
            0,
            SetWindowPositionNoSize |
            SetWindowPositionNoZOrder |
            SetWindowPositionNoActivate);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetWindowRect(
            IntPtr window,
            out NativeRect rectangle);

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
