using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using Microsoft.Win32;
using Screenshot.App.Core;
using Screenshot.App.Editor;
using Screenshot.App.Infrastructure;
using Screenshot.App.Presentation;
using DrawingPoint = System.Drawing.Point;
using DrawingRectangle = System.Drawing.Rectangle;
using WinForms = System.Windows.Forms;

namespace Screenshot.App.Capture;

internal enum RecordingInputDisplayMode
{
    None,
    Mouse,
    Keyboard,
    KeyboardAndMouse,
}

[SuppressMessage(
    "Design",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "The WPF window owns and disposes the recorder in OnClosed.")]
public partial class VideoRecordingControlWindow : Window
{
    private static WeakReference<VideoRecordingControlWindow>? _activeWindow;
    private const int TopmostWindow = -1;
    private const double ActiveControlWidth = 700;
    private const uint DoNotResize = 0x0001;
    private const uint DoNotMove = 0x0002;
    private const uint DoNotActivate = 0x0010;
    private const uint DoNotChangeOwnerZOrder = 0x0200;
    private const int ControlGap = 10;
    internal const string SuccessfulCompletionMessage = "录制完成，已保存";
    internal static readonly TimeSpan SuccessfulCompletionHoldDuration =
        TimeSpan.FromMilliseconds(1500);
    private RegionVideoRecorder _recorder;
    private readonly RecordingRegionFrameWindow _frameWindow;
    private readonly string _saveDirectory;
    private readonly Action<VideoRecordingPreferences>?
        _recordingPreferencesChanged;
    private readonly Action<VideoRecordingAnnotationPreferences>?
        _recordingAnnotationPreferencesChanged;
    private readonly Action<int[]>? _customColorPaletteChanged;
    private VideoRecordingPreferences _recorderPreferences;
    private readonly DispatcherTimer _elapsedTimer;
    private readonly Stopwatch _elapsed = new();
    private readonly TaskCompletionSource<RegionVideoRecordingResult> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private RecordingInputMonitor? _inputMonitor;
    private RecordingInputOverlayWindow? _inputOverlay;
    private RecordingAnnotationOverlayWindow? _annotationOverlay;
    private bool _allowClose;
    private bool _isCompleting;
    private bool _isInitializingOptions = true;
    private bool _hasSavedWindowPlacement;
    private Action _resetWindowPlacement = static () => { };
    private readonly ToolbarDragHintBehavior _toolbarDragHint;
    private bool _isControlSurfaceDragging;
    private DrawingPoint _controlPointerStart;
    private DrawingRectangle _controlWindowStart;
    private int _feedbackVersion;
    private System.Windows.Media.Color _annotationColor =
        System.Windows.Media.Color.FromRgb(240, 68, 85);
    private ArrowStyle _recordingArrowStyle = ArrowStyle.Filled;
    private ArrowToolMode _recordingArrowToolMode = ArrowToolMode.Straight;
    private ShapeToolMode _recordingShapeToolMode = ShapeToolMode.Rectangle;
    private int[] _customColorPalette = [];

    private VideoRecordingControlWindow(
        RegionVideoRecorder recorder,
        string saveDirectory,
        bool recordSystemAudio,
        bool recordMicrophone,
        VideoRecordingCodec codec,
        int frameRate,
        bool showKeyboardInput,
        bool showMouseInput,
        bool showMouseTrail,
        VideoRecordingOutputFormat outputFormat,
        RecordingInputOverlayWindow inputOverlay,
        RecordingAnnotationOverlayWindow annotationOverlay,
        Action<VideoRecordingPreferences>? recordingPreferencesChanged,
        VideoRecordingAnnotationPreferences annotationPreferences,
        Action<VideoRecordingAnnotationPreferences>?
            recordingAnnotationPreferencesChanged,
        int[]? customColorPalette,
        Action<int[]>? customColorPaletteChanged)
    {
        ArgumentNullException.ThrowIfNull(recorder);
        _saveDirectory = saveDirectory;
        _recordingPreferencesChanged = recordingPreferencesChanged;
        _recordingAnnotationPreferencesChanged =
            recordingAnnotationPreferencesChanged;
        _customColorPalette = NormalizeCustomColorPalette(customColorPalette);
        _customColorPaletteChanged = customColorPaletteChanged;
        _recordingShapeToolMode = Enum.IsDefined(annotationPreferences.ShapeToolMode)
            ? annotationPreferences.ShapeToolMode
            : ShapeToolMode.Rectangle;
        _recordingArrowToolMode = Enum.IsDefined(annotationPreferences.ArrowToolMode)
            ? annotationPreferences.ArrowToolMode
            : ArrowToolMode.Straight;
        _recordingArrowStyle = Enum.IsDefined(annotationPreferences.ArrowStyle)
            ? annotationPreferences.ArrowStyle
            : ArrowStyle.Filled;
        _annotationColor = ParseAnnotationColor(annotationPreferences.StrokeColor);
        _recorderPreferences = new VideoRecordingPreferences(
            codec,
            RegionVideoRecorder.NormalizeFrameRate(frameRate),
            recordSystemAudio,
            recordMicrophone,
            showKeyboardInput,
            showMouseInput,
            showMouseTrail,
            outputFormat);
        _recorder = recorder;
        _inputOverlay = inputOverlay;
        _annotationOverlay = annotationOverlay;
        _frameWindow = new RecordingRegionFrameWindow(_recorder.Region);
        _elapsedTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(200),
        };
        _elapsedTimer.Tick += OnElapsedTimerTick;
        _recorder.Failed += OnRecorderFailed;
        InitializeComponent();
        ApplySavedAnnotationPreferences(annotationPreferences.StrokeWidth);
        PopulateRecordingEmojiMenu();
        _toolbarDragHint = new ToolbarDragHintBehavior(
            ControlSurface,
            ControlSurface);
        _hasSavedWindowPlacement = WindowPlacementService.TrackPosition(
            this,
            WindowPlacementKeys.VideoRecordingControls,
            out _resetWindowPlacement);
        CodecComboBox.SelectedValue = outputFormat == VideoRecordingOutputFormat.Gif
            ? "Gif"
            : codec.ToString();
        FrameRateComboBox.SelectedValue =
            RegionVideoRecorder.NormalizeFrameRate(frameRate).ToString(
                CultureInfo.InvariantCulture);
        SystemAudioCheckBox.IsChecked = recordSystemAudio;
        MicrophoneCheckBox.IsChecked = recordMicrophone;
        InputDisplayModeComboBox.SelectedValue = ResolveInputDisplayMode(
            showKeyboardInput,
            showMouseInput).ToString();
        MouseTrailCheckBox.IsChecked = showMouseTrail;
        UpdateFormatDependentControls();
        _isInitializingOptions = false;
        SourceInitialized += OnSourceInitialized;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
    }

    internal static async Task<RegionVideoRecordingResult> ShowSessionAsync(
        ScreenRegion recordingRegion,
        string saveDirectory,
        bool recordSystemAudio,
        bool recordMicrophone,
        VideoRecordingCodec codec,
        int frameRate,
        bool showKeyboardInput,
        bool showMouseInput,
        bool showMouseTrail,
        VideoRecordingOutputFormat outputFormat = VideoRecordingOutputFormat.Mp4,
        Action<VideoRecordingPreferences>? recordingPreferencesChanged = null,
        VideoRecordingAnnotationPreferences? annotationPreferences = null,
        Action<VideoRecordingAnnotationPreferences>?
            recordingAnnotationPreferencesChanged = null,
        int[]? customColorPalette = null,
        Action<int[]>? customColorPaletteChanged = null)
    {
        // ScreenRecorderLib may initialize Desktop Duplication, hardware
        // encoders, and audio devices synchronously. Keep that native work
        // off WPF's dispatcher so a slow driver cannot freeze the shell.
        var inputOverlay = new RecordingInputOverlayWindow(recordingRegion);
        var annotationOverlay = new RecordingAnnotationOverlayWindow(recordingRegion);
        RegionVideoRecorder recorder;
        try
        {
            recorder = await Task.Run(() => new RegionVideoRecorder(
                recordingRegion,
                saveDirectory,
                recordSystemAudio,
                recordMicrophone,
                codec,
                frameRate));
        }
        catch
        {
            inputOverlay.Close();
            annotationOverlay.Close();
            throw;
        }
        VideoRecordingControlWindow? window = null;
        try
        {
            window = new VideoRecordingControlWindow(
                recorder,
                saveDirectory,
                recordSystemAudio,
                recordMicrophone,
                codec,
                frameRate,
                showKeyboardInput,
                showMouseInput,
                showMouseTrail,
                outputFormat,
                inputOverlay,
                annotationOverlay,
                recordingPreferencesChanged,
                annotationPreferences ?? new VideoRecordingAnnotationPreferences(
                    ShapeToolMode.Rectangle,
                    ArrowToolMode.Straight,
                    ArrowStyle.Filled,
                    "#F04455",
                    3),
                recordingAnnotationPreferencesChanged,
                customColorPalette,
                customColorPaletteChanged);
        }
        catch
        {
            recorder.Dispose();
            inputOverlay.Close();
            annotationOverlay.Close();
            throw;
        }
        window._frameWindow.Show();
        window.Show();
        window.UpdateLayout();
        window.EnsureControlVisibleAndTopmost();
        _activeWindow = new WeakReference<VideoRecordingControlWindow>(window);
        try
        {
            return await window._completion.Task;
        }
        finally
        {
            if (_activeWindow?.TryGetTarget(out var activeWindow) == true &&
                ReferenceEquals(activeWindow, window))
            {
                _activeWindow = null;
            }
        }
    }

    internal static bool TryShowAlreadyRecordingFeedback()
    {
        if (_activeWindow is null ||
            !_activeWindow.TryGetTarget(out var window) ||
            !window.IsLoaded)
        {
            return false;
        }

        _ = window.Dispatcher.BeginInvoke(window.ShowAlreadyRecordingFeedback);
        return true;
    }

    internal static void SetCaptureInteractionActive(bool isActive)
    {
        if (_activeWindow is null ||
            !_activeWindow.TryGetTarget(out var window) ||
            !window.IsLoaded)
        {
            return;
        }

        _ = window.Dispatcher.BeginInvoke(
            () => window.SetCaptureInteractionVisibility(isActive));
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (!_hasSavedWindowPlacement)
        {
            _resetWindowPlacement();
            PositionOutsideRecordingRegion(handle);
        }

        _ = Dispatcher.BeginInvoke(
            DispatcherPriority.Loaded,
            EnsureControlVisibleAndTopmost);
    }

    private void PositionOutsideRecordingRegion(IntPtr handle)
    {
        if (!NativeMethods.GetWindowRect(handle, out var currentBounds))
        {
            return;
        }

        var width = currentBounds.Right - currentBounds.Left;
        var height = currentBounds.Bottom - currentBounds.Top;
        var region = _recorder.Region;
        var reference = new DrawingRectangle(
            region.X,
            region.Y,
            region.Width,
            region.Height);
        var workArea = WinForms.Screen.FromRectangle(reference).WorkingArea;
        var destination = CalculateAutomaticControlBounds(
            region,
            workArea,
            width,
            height);
        _ = NativeMethods.SetWindowPos(
            handle,
            new IntPtr(TopmostWindow),
            destination.X,
            destination.Y,
            0,
            0,
            DoNotResize | DoNotActivate | DoNotChangeOwnerZOrder);
    }

    internal static DrawingRectangle CalculateAutomaticControlBounds(
        ScreenRegion region,
        DrawingRectangle workArea,
        int width,
        int height)
    {
        var centeredX = region.X + ((region.Width - width) / 2);
        var centeredY = region.Y + ((region.Height - height) / 2);
        var candidates = new[]
        {
            new DrawingRectangle(
                centeredX,
                region.Y + region.Height + ControlGap,
                width,
                height),
            new DrawingRectangle(
                centeredX,
                region.Y - height - ControlGap,
                width,
                height),
            new DrawingRectangle(
                region.X + region.Width + ControlGap,
                centeredY,
                width,
                height),
            new DrawingRectangle(
                region.X - width - ControlGap,
                centeredY,
                width,
                height),
        };
        var destination = candidates.FirstOrDefault(workArea.Contains);
        if (destination.IsEmpty)
        {
            destination = new DrawingRectangle(
                Math.Clamp(
                    centeredX,
                    workArea.Left,
                    Math.Max(workArea.Left, workArea.Right - width)),
                Math.Clamp(
                    region.Y + region.Height + ControlGap,
                    workArea.Top,
                    Math.Max(workArea.Top, workArea.Bottom - height)),
                width,
                height);
        }

        return destination;
    }

    private void OnStartClick(object sender, RoutedEventArgs e)
    {
        if (_isCompleting ||
            _recorder.State != RegionVideoRecorderState.Ready)
        {
            return;
        }

        try
        {
            var preferences = GetRecordingPreferences();
            ReplaceRecorder(preferences);
            _recordingPreferencesChanged?.Invoke(preferences);
            StartInputFeedback(preferences);
            StartAnnotationOverlay();
            _recorder.Start();
            _elapsed.Restart();
            _elapsedTimer.Start();
            RecordingStatusText.Text = "正在录屏";
            RecordingStatusText.Foreground = new SolidColorBrush(
                System.Windows.Media.Color.FromRgb(240, 68, 85));
            RecordingOptionsPanel.Visibility = Visibility.Collapsed;
            AnnotationToolsPanel.Visibility = Visibility.Visible;
            StartButton.Visibility = Visibility.Collapsed;
            PauseButton.Visibility = Visibility.Visible;
            PauseButton.IsEnabled = true;
            PauseButton.ToolTip = "暂停录制";
            StopButton.ToolTip = "结束并保存";
            StopIcon.Data = (Geometry)FindResource("StopIconGeometry");
            CancelRecordingButton.Visibility = Visibility.Visible;
            CancelRecordingButton.IsEnabled = true;
            FinishAndEditButton.Visibility = Visibility.Visible;
            FinishAndEditButton.IsEnabled = true;
            Width = ActiveControlWidth;
            _ = Dispatcher.BeginInvoke(
                DispatcherPriority.Loaded,
                () =>
                {
                    RepositionAutomaticControl();
                    EnsureControlVisibleAndTopmost();
                });
            UpdateLowProfileOpacity();
        }
        catch (Exception exception)
        {
            _ = CompleteAfterFailureAsync(exception.Message);
        }
    }

    private void OnPauseClick(object sender, RoutedEventArgs e)
    {
        if (_isCompleting)
        {
            return;
        }

        try
        {
            if (_recorder.State == RegionVideoRecorderState.Recording)
            {
                _recorder.Pause();
                _elapsed.Stop();
                RecordingStatusText.Text = "已暂停";
                PauseButton.ToolTip = "继续录制";
                PauseIcon.Data = (Geometry)FindResource("ResumeIconGeometry");
                _inputMonitor?.SetPaused(true);
                _inputOverlay?.SetPaused(true);
                _annotationOverlay?.SetPaused(true);
                UpdateLowProfileOpacity();
            }
            else if (_recorder.State == RegionVideoRecorderState.Paused)
            {
                _recorder.Resume();
                _elapsed.Start();
                RecordingStatusText.Text = "正在录屏";
                PauseButton.ToolTip = "暂停录制";
                PauseIcon.Data = (Geometry)FindResource("PauseIconGeometry");
                _inputOverlay?.SetPaused(false);
                _inputMonitor?.SetPaused(false);
                _annotationOverlay?.SetPaused(false);
                UpdateLowProfileOpacity();
            }
        }
        catch (Exception exception)
        {
            _ = CompleteAfterFailureAsync(exception.Message);
        }
    }

    private void OnRecordingOptionChanged(object sender, RoutedEventArgs e)
    {
        if (_isInitializingOptions || _recorder.State != RegionVideoRecorderState.Ready)
        {
            return;
        }

        UpdateFormatDependentControls();
        _recordingPreferencesChanged?.Invoke(GetRecordingPreferences());
    }

    private VideoRecordingPreferences GetRecordingPreferences()
    {
        var formatValue = CodecComboBox.SelectedValue as string;
        var outputFormat = string.Equals(
            formatValue,
            "Gif",
            StringComparison.OrdinalIgnoreCase)
            ? VideoRecordingOutputFormat.Gif
            : VideoRecordingOutputFormat.Mp4;
        var codec = string.Equals(
            formatValue,
            nameof(VideoRecordingCodec.H265),
            StringComparison.OrdinalIgnoreCase)
            ? VideoRecordingCodec.H265
            : VideoRecordingCodec.H264;
        var frameRate = int.TryParse(
            FrameRateComboBox.SelectedValue as string,
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var parsedFrameRate)
            ? RegionVideoRecorder.NormalizeFrameRate(parsedFrameRate)
            : 30;
        var inputDisplayMode = Enum.TryParse<RecordingInputDisplayMode>(
            InputDisplayModeComboBox.SelectedValue as string,
            ignoreCase: true,
            out var parsedInputDisplayMode)
            ? parsedInputDisplayMode
            : RecordingInputDisplayMode.None;
        return new VideoRecordingPreferences(
            codec,
            frameRate,
            SystemAudioCheckBox.IsChecked == true,
            MicrophoneCheckBox.IsChecked == true,
            ShowsKeyboardInput(inputDisplayMode),
            ShowsMouseInput(inputDisplayMode),
            MouseTrailCheckBox.IsChecked == true,
            outputFormat);
    }

    private void UpdateFormatDependentControls()
    {
        var isGif = string.Equals(
            CodecComboBox.SelectedValue as string,
            "Gif",
            StringComparison.OrdinalIgnoreCase);
        SystemAudioCheckBox.IsEnabled = !isGif;
        MicrophoneCheckBox.IsEnabled = !isGif;
        SystemAudioCheckBox.ToolTip = isGif
            ? "GIF 不包含声音；切换回 MP4 后保留原选择"
            : "录制电脑正在播放的声音";
        MicrophoneCheckBox.ToolTip = isGif
            ? "GIF 不包含声音；切换回 MP4 后保留原选择"
            : "录制系统默认麦克风";
    }

    internal static RecordingInputDisplayMode ResolveInputDisplayMode(
        bool showKeyboardInput,
        bool showMouseInput)
    {
        return (showKeyboardInput, showMouseInput) switch
        {
            (true, true) => RecordingInputDisplayMode.KeyboardAndMouse,
            (true, false) => RecordingInputDisplayMode.Keyboard,
            (false, true) => RecordingInputDisplayMode.Mouse,
            _ => RecordingInputDisplayMode.None,
        };
    }

    internal static bool ShowsKeyboardInput(RecordingInputDisplayMode mode) =>
        mode is RecordingInputDisplayMode.Keyboard or
            RecordingInputDisplayMode.KeyboardAndMouse;

    internal static bool ShowsMouseInput(RecordingInputDisplayMode mode) =>
        mode is RecordingInputDisplayMode.Mouse or
            RecordingInputDisplayMode.KeyboardAndMouse;

    private void ReplaceRecorder(VideoRecordingPreferences preferences)
    {
        if (!RequiresRecorderReplacement(_recorderPreferences, preferences))
        {
            _recorderPreferences = preferences;
            return;
        }

        var replacement = new RegionVideoRecorder(
            _recorder.Region,
            _saveDirectory,
            preferences.RecordSystemAudio,
            preferences.RecordMicrophone,
            preferences.Codec,
            preferences.FrameRate);
        replacement.Failed += OnRecorderFailed;
        _recorder.Failed -= OnRecorderFailed;
        _recorder.Dispose();
        _recorder = replacement;
        _recorderPreferences = preferences;
    }

    internal static bool RequiresRecorderReplacement(
        VideoRecordingPreferences current,
        VideoRecordingPreferences replacement) =>
        current.Codec != replacement.Codec ||
        current.FrameRate != replacement.FrameRate ||
        current.RecordSystemAudio != replacement.RecordSystemAudio ||
        current.RecordMicrophone != replacement.RecordMicrophone;

    private async void OnStopClick(object sender, RoutedEventArgs e)
    {
        if (_recorder.State == RegionVideoRecorderState.Ready)
        {
            CloseWithResult(new RegionVideoRecordingResult(null, null));
            return;
        }

        await StopAndCloseAsync(openEditor: false);
    }

    private async void OnFinishAndEditClick(object sender, RoutedEventArgs e)
    {
        await StopAndCloseAsync(openEditor: true);
    }

    private async void OnCancelRecordingClick(object sender, RoutedEventArgs e)
    {
        if (_isCompleting)
        {
            return;
        }

        _isCompleting = true;
        StartButton.IsEnabled = false;
        PauseButton.IsEnabled = false;
        StopButton.IsEnabled = false;
        CancelRecordingButton.IsEnabled = false;
        FinishAndEditButton.IsEnabled = false;
        _elapsed.Stop();
        _elapsedTimer.Stop();
        StopInputMonitor();
        RecordingStatusText.Text = "正在取消";
        EnterCompletionUi();

        await _recorder.CancelAsync();
        DisposeInputFeedback();
        DisposeAnnotationOverlay();
        CloseWithResult(new RegionVideoRecordingResult(null, null));
    }

    private async Task StopAndCloseAsync(bool openEditor = false)
    {
        if (_isCompleting)
        {
            return;
        }

        _isCompleting = true;
        StartButton.IsEnabled = false;
        PauseButton.IsEnabled = false;
        StopButton.IsEnabled = false;
        CancelRecordingButton.IsEnabled = false;
        FinishAndEditButton.IsEnabled = false;
        _elapsed.Stop();
        _elapsedTimer.Stop();
        StopInputMonitor();
        RecordingStatusText.Text = "正在保存";
        EnterCompletionUi();

        var result = await _recorder.StopAsync();
        DisposeInputFeedback();
        DisposeAnnotationOverlay();
        if (!result.IsSuccess)
        {
            await CompleteAfterFailureAsync(
                result.ErrorMessage ?? "视频录制未完成。",
                alreadyCompleting: true);
            return;
        }

        RecordingStatusText.Text = SuccessfulCompletionMessage;
        RecordingStatusText.ToolTip = result.FilePath;
        await Task.Delay(SuccessfulCompletionHoldDuration);
        CloseWithResult(result with { OpenEditor = openEditor });
    }

    private void OnRecorderFailed(string errorMessage)
    {
        _ = Dispatcher.InvokeAsync(
            () => _ = CompleteAfterFailureAsync(errorMessage));
    }

    private async Task CompleteAfterFailureAsync(
        string errorMessage,
        bool alreadyCompleting = false)
    {
        if (_isCompleting && !alreadyCompleting)
        {
            return;
        }

        _isCompleting = true;
        _elapsed.Stop();
        _elapsedTimer.Stop();
        DisposeInputFeedback();
        DisposeAnnotationOverlay();
        StartButton.IsEnabled = false;
        PauseButton.IsEnabled = false;
        StopButton.IsEnabled = false;
        CancelRecordingButton.IsEnabled = false;
        FinishAndEditButton.IsEnabled = false;
        RecordingStatusText.Text = "录制失败";
        EnterCompletionUi();
        RecordingStatusText.ToolTip = errorMessage;
        await Task.Delay(1400);
        CloseWithResult(new RegionVideoRecordingResult(null, errorMessage));
    }

    private void OnElapsedTimerTick(object? sender, EventArgs e)
    {
        var elapsed = _elapsed.Elapsed;
        ElapsedTimeText.Text = elapsed.TotalHours >= 1
            ? elapsed.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture)
            : elapsed.ToString(@"mm\:ss", CultureInfo.InvariantCulture);
    }

    private void OnControlSurfaceMouseLeftButtonDown(
        object sender,
        MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed ||
            !ToolbarDragInteraction.IsBlankSurface(
                e.OriginalSource as DependencyObject,
                ControlSurface))
        {
            return;
        }

        if (e.ClickCount >= 2)
        {
            FinishControlSurfaceDrag(ensureVisible: false);
            ResetControlPosition();
            e.Handled = true;
            return;
        }

        if (!MonitorGeometryService.TryGetWindowBounds(this, out var bounds))
        {
            return;
        }

        EnsureManualPositionTracking();
        _isControlSurfaceDragging = true;
        _controlPointerStart = WinForms.Cursor.Position;
        _controlWindowStart = bounds;
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            _isControlSurfaceDragging = false;
            return;
        }

        _ = NativeMethods.SetCapture(handle);
        if (NativeMethods.GetCapture() != handle)
        {
            _isControlSurfaceDragging = false;
            return;
        }

        e.Handled = true;
    }

    private void OnControlSurfaceMouseMove(
        object sender,
        System.Windows.Input.MouseEventArgs e)
    {
        if (!_isControlSurfaceDragging)
        {
            return;
        }

        if (e.LeftButton != MouseButtonState.Pressed)
        {
            FinishControlSurfaceDrag();
            return;
        }

        var current = WinForms.Cursor.Position;
        var targetX = _controlWindowStart.Left +
            current.X - _controlPointerStart.X;
        var targetY = _controlWindowStart.Top +
            current.Y - _controlPointerStart.Y;
        _ = MonitorGeometryService.TryMoveWindow(this, targetX, targetY);
        e.Handled = true;
    }

    private void OnControlSurfaceMouseLeftButtonUp(
        object sender,
        MouseButtonEventArgs e)
    {
        if (!_isControlSurfaceDragging)
        {
            return;
        }

        FinishControlSurfaceDrag();
        e.Handled = true;
    }

    private void FinishControlSurfaceDrag(bool ensureVisible = true)
    {
        _isControlSurfaceDragging = false;
        var handle = new WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero && NativeMethods.GetCapture() == handle)
        {
            _ = NativeMethods.ReleaseCapture();
        }

        if (ensureVisible)
        {
            EnsureControlVisibleAndTopmost();
        }
    }

    private void EnsureManualPositionTracking()
    {
        if (_hasSavedWindowPlacement)
        {
            return;
        }

        _hasSavedWindowPlacement = WindowPlacementService.TrackPosition(
            this,
            WindowPlacementKeys.VideoRecordingControls,
            out _resetWindowPlacement);
        // A missing stored position is expected here. Tracking is active now,
        // and the drag immediately below will save the user's coordinates.
        _hasSavedWindowPlacement = true;
    }

    private void ResetControlPosition()
    {
        _resetWindowPlacement();
        _hasSavedWindowPlacement = false;
        var handle = new WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero)
        {
            PositionOutsideRecordingRegion(handle);
        }
    }

    private void RepositionAutomaticControl()
    {
        if (_hasSavedWindowPlacement)
        {
            return;
        }

        var handle = new WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero)
        {
            PositionOutsideRecordingRegion(handle);
        }
    }

    private void EnsureControlVisibleAndTopmost()
    {
        if (!IsLoaded)
        {
            return;
        }

        _ = WindowPlacementService.EnsureVisible(this);
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
            DoNotMove |
            DoNotResize |
            DoNotActivate |
            DoNotChangeOwnerZOrder);
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
        {
            return;
        }

        _ = Dispatcher.BeginInvoke(
            DispatcherPriority.Loaded,
            EnsureControlVisibleAndTopmost);
    }

    private void UpdateLowProfileOpacity()
    {
        Opacity = 1;
    }

    private void EnterCompletionUi()
    {
        RecordingOptionsPanel.Visibility = Visibility.Collapsed;
        AnnotationToolsPanel.Visibility = Visibility.Collapsed;
        StartButton.Visibility = Visibility.Collapsed;
        PauseButton.Visibility = Visibility.Collapsed;
        StopButton.Visibility = Visibility.Collapsed;
        CancelRecordingButton.Visibility = Visibility.Collapsed;
        FinishAndEditButton.Visibility = Visibility.Collapsed;
        System.Windows.Controls.Grid.SetColumnSpan(RecordingStatusPanel, 3);
        Opacity = 1;
    }

    private void StartInputFeedback(VideoRecordingPreferences preferences)
    {
        StopInputMonitor();
        if (!preferences.ShowKeyboardInput &&
            !preferences.ShowMouseInput &&
            !preferences.ShowMouseTrail)
        {
            _inputOverlay?.SetMouseTrailEnabled(false);
            _inputOverlay?.Hide();
            return;
        }

        _inputOverlay?.SetMouseTrailEnabled(preferences.ShowMouseTrail);
        _inputOverlay?.Show();
        _inputMonitor = new RecordingInputMonitor(
            preferences.ShowKeyboardInput,
            preferences.ShowMouseInput,
            preferences.ShowMouseTrail);
        _inputMonitor.InputChanged += OnRecordingInputChanged;
        _inputMonitor.MouseMoved += OnRecordingMouseMoved;
        _inputMonitor.Start();
    }

    private void OnRecordingMouseMoved(
        object? sender,
        RecordingMouseMovedEventArgs e)
    {
        var overlay = _inputOverlay;
        if (overlay is null)
        {
            return;
        }

        _ = overlay.Dispatcher.BeginInvoke(
            DispatcherPriority.Render,
            () => overlay.ShowMousePosition(e.X, e.Y));
    }

    private void OnRecordingInputChanged(
        object? sender,
        RecordingInputChangedEventArgs e)
    {
        var overlay = _inputOverlay;
        if (overlay is null)
        {
            return;
        }

        _ = overlay.Dispatcher.BeginInvoke(
            () => overlay.ShowInput(e.DisplayText, e.IsTransient));
    }

    private void DisposeInputFeedback()
    {
        StopInputMonitor();
        _inputOverlay?.Close();
        _inputOverlay = null;
    }

    private void StopInputMonitor()
    {
        if (_inputMonitor is not null)
        {
            _inputMonitor.InputChanged -= OnRecordingInputChanged;
            _inputMonitor.MouseMoved -= OnRecordingMouseMoved;
            _inputMonitor.Dispose();
            _inputMonitor = null;
        }
    }

    private void StartAnnotationOverlay()
    {
        _annotationOverlay?.Clear();
        _annotationOverlay?.SetSelectedColor(_annotationColor);
        _annotationOverlay?.SetStrokeWidth(AnnotationStrokeWidthSlider.Value);
        _annotationOverlay?.SetArrowStyle(_recordingArrowStyle);
        _annotationOverlay?.Show();
        SelectAnnotationTool(RecordingAnnotationTool.Pointer);
    }

    private void ApplySavedAnnotationPreferences(int strokeWidth)
    {
        var shapeTool = _recordingShapeToolMode == ShapeToolMode.Ellipse
            ? RecordingAnnotationTool.Ellipse
            : RecordingAnnotationTool.Rectangle;
        ShapeToolButton.Tag = shapeTool.ToString();
        ShapeToolIcon.Data = (Geometry)FindResource(
            shapeTool == RecordingAnnotationTool.Ellipse
                ? "EllipseIconGeometry"
                : "RectangleIconGeometry");

        var arrowTool = _recordingArrowToolMode == ArrowToolMode.Curved
            ? RecordingAnnotationTool.CurvedArrow
            : RecordingAnnotationTool.Arrow;
        ArrowToolButton.Tag = arrowTool.ToString();
        UpdateRecordingArrowButton(arrowTool, _recordingArrowStyle);
        AnnotationColorSwatch.Fill = new SolidColorBrush(_annotationColor);
        AnnotationStrokeWidthSlider.Value = Math.Clamp(strokeWidth, 1, 12);
    }

    private static System.Windows.Media.Color ParseAnnotationColor(string? value)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(value) &&
                System.Windows.Media.ColorConverter.ConvertFromString(value) is
                    System.Windows.Media.Color color)
            {
                return color;
            }
        }
        catch (FormatException)
        {
            // Invalid legacy values fall back to the recording accent color.
        }

        return System.Windows.Media.Color.FromRgb(240, 68, 85);
    }

    private void SaveRecordingAnnotationPreferences()
    {
        if (_isInitializingOptions)
        {
            return;
        }

        _recordingAnnotationPreferencesChanged?.Invoke(
            new VideoRecordingAnnotationPreferences(
                _recordingShapeToolMode,
                _recordingArrowToolMode,
                _recordingArrowStyle,
                FormatAnnotationColor(_annotationColor),
                (int)Math.Round(AnnotationStrokeWidthSlider.Value)));
    }

    private static string FormatAnnotationColor(System.Windows.Media.Color color) =>
        color.A == byte.MaxValue
            ? $"#{color.R:X2}{color.G:X2}{color.B:X2}"
            : $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";

    internal static int[] NormalizeCustomColorPalette(IEnumerable<int>? colors) =>
        (colors ?? [])
            .Where(color => color is >= 0 and <= 0xFFFFFF)
            .Take(16)
            .ToArray();

    private void DisposeAnnotationOverlay()
    {
        _annotationOverlay?.Close();
        _annotationOverlay = null;
    }

    private void OnAnnotationToolClick(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton button ||
            button.Tag is not string tag ||
            !Enum.TryParse<RecordingAnnotationTool>(tag, out var tool))
        {
            return;
        }

        SelectAnnotationTool(ResolveAnnotationToolSelection(
            tool,
            button.IsChecked == true));
        e.Handled = true;
    }

    internal static RecordingAnnotationTool ResolveAnnotationToolSelection(
        RecordingAnnotationTool tool,
        bool isChecked) =>
        isChecked ? tool : RecordingAnnotationTool.Pointer;

    private void SelectAnnotationTool(RecordingAnnotationTool tool)
    {
        ShapeToolButton.IsChecked = tool is RecordingAnnotationTool.Rectangle or
            RecordingAnnotationTool.Ellipse;
        BrushToolButton.IsChecked = tool == RecordingAnnotationTool.Brush;
        ArrowToolButton.IsChecked = tool is RecordingAnnotationTool.Arrow or
            RecordingAnnotationTool.CurvedArrow;
        EmojiToolButton.IsChecked = tool == RecordingAnnotationTool.Emoji;
        NumberToolButton.IsChecked = tool == RecordingAnnotationTool.Number;
        TextToolButton.IsChecked = tool == RecordingAnnotationTool.Text;
        MosaicToolButton.IsChecked = tool == RecordingAnnotationTool.Mosaic;
        _annotationOverlay?.SelectTool(tool);
        _frameWindow.EnsureTopmost();
        _annotationOverlay?.EnsureTopmost();
        EnsureControlVisibleAndTopmost();
    }

    private void OnRecordingShapeMenuItemClick(
        object sender,
        RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.MenuItem item ||
            item.Tag is not string tag ||
            !Enum.TryParse<RecordingAnnotationTool>(tag, out var tool) ||
            tool is not (RecordingAnnotationTool.Rectangle or
                RecordingAnnotationTool.Ellipse))
        {
            return;
        }

        ShapeToolButton.Tag = tag;
        _recordingShapeToolMode = tool == RecordingAnnotationTool.Ellipse
            ? ShapeToolMode.Ellipse
            : ShapeToolMode.Rectangle;
        ShapeToolIcon.Data = (Geometry)FindResource(
            tool == RecordingAnnotationTool.Rectangle
                ? "RectangleIconGeometry"
                : "EllipseIconGeometry");
        SelectAnnotationTool(tool);
        SaveRecordingAnnotationPreferences();
        e.Handled = true;
    }

    private void OnRecordingShapeMenuArrowMouseDown(
        object sender,
        MouseButtonEventArgs e)
    {
        OpenRecordingToolMenu(ShapeToolButton);
        e.Handled = true;
    }

    private void OnRecordingEmojiMenuArrowMouseDown(
        object sender,
        MouseButtonEventArgs e)
    {
        OpenRecordingToolMenu(EmojiToolButton);
        e.Handled = true;
    }

    private void OnRecordingArrowMenuArrowMouseDown(
        object sender,
        MouseButtonEventArgs e)
    {
        OpenRecordingToolMenu(ArrowToolButton);
        e.Handled = true;
    }

    private static void OpenRecordingToolMenu(ToggleButton button)
    {
        if (button.ContextMenu is not { } menu)
        {
            return;
        }

        menu.PlacementTarget = button;
        menu.Placement = PlacementMode.Top;
        menu.IsOpen = true;
    }

    private void OnRecordingEmojiMenuItemClick(
        object sender,
        RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string emoji } ||
            string.IsNullOrWhiteSpace(emoji))
        {
            return;
        }

        EmojiToolIcon.Sticker = emoji;
        _annotationOverlay?.SetSelectedEmoji(emoji);
        SelectAnnotationTool(RecordingAnnotationTool.Emoji);
        RecordingEmojiMenu.IsOpen = false;
        e.Handled = true;
    }

    private void PopulateRecordingEmojiMenu()
    {
        var panel = new WrapPanel { Width = 288 };
        foreach (var emoji in EmojiStickerCatalog.All)
        {
            var button = new System.Windows.Controls.Button
            {
                Width = 32,
                Height = 32,
                Margin = new Thickness(2),
                Padding = new Thickness(2),
                Tag = emoji,
                ToolTip = emoji,
                Cursor = System.Windows.Input.Cursors.Hand,
                Style = (Style)FindResource("RecordingEmojiButton"),
                Content = new EmojiStickerImage
                {
                    Width = 23,
                    Height = 23,
                    Sticker = emoji,
                },
            };
            button.Click += OnRecordingEmojiMenuItemClick;
            panel.Children.Add(button);
        }

        RecordingEmojiMenu.Items.Clear();
        RecordingEmojiMenu.Items.Add(new ScrollViewer
        {
            Width = 310,
            MaxHeight = 250,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = panel,
        });
    }

    private void OnRecordingArrowMenuItemClick(
        object sender,
        RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.MenuItem { Tag: string tag })
        {
            return;
        }

        var parts = tag.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length != 2 ||
            !Enum.TryParse(parts[0], out RecordingAnnotationTool tool) ||
            tool is not (RecordingAnnotationTool.Arrow or
                RecordingAnnotationTool.CurvedArrow) ||
            !Enum.TryParse(parts[1], out ArrowStyle style))
        {
            return;
        }

        _recordingArrowStyle = style;
        _recordingArrowToolMode = tool == RecordingAnnotationTool.CurvedArrow
            ? ArrowToolMode.Curved
            : ArrowToolMode.Straight;
        ArrowToolButton.Tag = tool.ToString();
        UpdateRecordingArrowButton(tool, style);
        _annotationOverlay?.SetArrowStyle(style);
        SelectAnnotationTool(tool);
        SaveRecordingAnnotationPreferences();
        e.Handled = true;
    }

    private void UpdateRecordingArrowButton(
        RecordingAnnotationTool tool,
        ArrowStyle style)
    {
        ArrowToolIcon.Data = (Geometry)FindResource((tool, style) switch
        {
            (RecordingAnnotationTool.Arrow, ArrowStyle.Hollow) =>
                "StraightHollowArrowIconGeometry",
            (RecordingAnnotationTool.CurvedArrow, ArrowStyle.Filled) =>
                "CurvedFilledArrowIconGeometry",
            (RecordingAnnotationTool.CurvedArrow, ArrowStyle.Hollow) =>
                "CurvedHollowArrowIconGeometry",
            _ => "StraightFilledArrowIconGeometry",
        });
        ArrowToolIcon.Fill = style == ArrowStyle.Filled
            ? TryFindResource("EditorToolbarIconBrush") as System.Windows.Media.Brush
            : System.Windows.Media.Brushes.Transparent;
        ArrowToolIcon.Stroke = style == ArrowStyle.Hollow
            ? TryFindResource("EditorToolbarIconBrush") as System.Windows.Media.Brush
            : System.Windows.Media.Brushes.Transparent;
        ArrowToolIcon.StrokeThickness = style == ArrowStyle.Hollow ? 1.7 : 0;
    }

    private void OnAnnotationColorClick(object sender, RoutedEventArgs e)
    {
        var picker = new ThemeColorPickerWindow(
            _annotationColor,
            _customColorPalette)
        {
            Owner = this,
        };
        picker.ColorSelected += (_, color) =>
        {
            _annotationColor = color;
            AnnotationColorSwatch.Fill = new SolidColorBrush(color);
            _annotationOverlay?.SetSelectedColor(color);
            SaveRecordingAnnotationPreferences();
        };
        picker.PaletteChanged += (_, colors) =>
        {
            _customColorPalette = NormalizeCustomColorPalette(colors);
            _customColorPaletteChanged?.Invoke(_customColorPalette.ToArray());
        };
        picker.Show();
        e.Handled = true;
    }

    private void OnAnnotationStrokeWidthChanged(
        object sender,
        RoutedPropertyChangedEventArgs<double> e)
    {
        _annotationOverlay?.SetStrokeWidth(e.NewValue);
    }

    private void OnAnnotationStrokeWidthCommitted(
        object sender,
        RoutedEventArgs e)
    {
        SaveRecordingAnnotationPreferences();
    }

    private void OnAnnotationUndoClick(object sender, RoutedEventArgs e)
    {
        _annotationOverlay?.Undo();
        e.Handled = true;
    }

    private void OnAnnotationRedoClick(object sender, RoutedEventArgs e)
    {
        _annotationOverlay?.Redo();
        e.Handled = true;
    }

    private void OnAnnotationClearClick(object sender, RoutedEventArgs e)
    {
        _annotationOverlay?.Clear();
        e.Handled = true;
    }

    private async void ShowAlreadyRecordingFeedback()
    {
        var version = ++_feedbackVersion;
        RecordingStatusText.Text = "正在录屏";
        Opacity = 1;
        ControlSurface.BorderBrush = new SolidColorBrush(
            System.Windows.Media.Color.FromRgb(240, 68, 85));
        try
        {
            await Task.Delay(1200);
        }
        finally
        {
            if (version == _feedbackVersion && IsLoaded)
            {
                ControlSurface.SetResourceReference(
                    System.Windows.Controls.Border.BorderBrushProperty,
                    "AppBorderBrush");
                UpdateLowProfileOpacity();
            }
        }
    }

    private void SetCaptureInteractionVisibility(bool isActive)
    {
        if (isActive)
        {
            _inputMonitor?.SetPaused(true);
            _inputOverlay?.SetPaused(true);
            _inputOverlay?.Hide();
            _annotationOverlay?.Hide();
            _frameWindow.Hide();
            return;
        }

        if (_inputOverlay is not null)
        {
            _inputOverlay.Show();
            if (_recorder.State != RegionVideoRecorderState.Paused)
            {
                _inputOverlay.SetPaused(false);
            }
        }

        if (_recorder.State != RegionVideoRecorderState.Paused)
        {
            _inputMonitor?.SetPaused(false);
        }
        _frameWindow.Show();
        _frameWindow.EnsureTopmost();
        if (_annotationOverlay is not null)
        {
            _annotationOverlay.Show();
            _annotationOverlay.SetPaused(
                _recorder.State == RegionVideoRecorderState.Paused);
            _annotationOverlay.EnsureTopmost();
        }
        EnsureControlVisibleAndTopmost();
    }

    private void OnWindowKeyDown(
        object sender,
        System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
        {
            return;
        }

        e.Handled = true;
        if (_recorder.State == RegionVideoRecorderState.Ready)
        {
            CloseWithResult(new RegionVideoRecordingResult(null, null));
        }
        else
        {
            _ = StopAndCloseAsync();
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            if (_recorder.State == RegionVideoRecorderState.Ready)
            {
                CloseWithResult(new RegionVideoRecordingResult(null, null));
            }
            else
            {
                _ = StopAndCloseAsync();
            }
        }

        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        FinishControlSurfaceDrag(ensureVisible: false);
        _toolbarDragHint.Detach();
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        _elapsedTimer.Stop();
        _elapsedTimer.Tick -= OnElapsedTimerTick;
        _recorder.Failed -= OnRecorderFailed;
        _recorder.Dispose();
        DisposeInputFeedback();
        DisposeAnnotationOverlay();
        _frameWindow.Close();
        _completion.TrySetResult(new RegionVideoRecordingResult(null, null));
        base.OnClosed(e);
    }

    private void CloseWithResult(RegionVideoRecordingResult result)
    {
        if (_allowClose)
        {
            return;
        }

        _completion.TrySetResult(result);
        _allowClose = true;
        Close();
    }

    private static class NativeMethods
    {
        [StructLayout(LayoutKind.Sequential)]
        public readonly record struct NativeRect(
            int Left,
            int Top,
            int Right,
            int Bottom);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetCapture(IntPtr windowHandle);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ReleaseCapture();

        [DllImport("user32.dll")]
        public static extern IntPtr GetCapture();

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
