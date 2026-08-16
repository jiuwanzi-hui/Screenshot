using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Screenshot.App.Core;
using Screenshot.App.Infrastructure;
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
    private const double ActiveControlWidth = 216;
    private const double RecordingIdleOpacity = 0.62;
    private const double PausedIdleOpacity = 0.86;
    private const uint DoNotResize = 0x0001;
    private const uint DoNotActivate = 0x0010;
    private const uint DoNotChangeOwnerZOrder = 0x0200;
    private const uint ExcludeFromCapture = 0x00000011;
    private const int ControlGap = 10;
    internal const string SuccessfulCompletionMessage = "录制完成，已保存";
    internal static readonly TimeSpan SuccessfulCompletionHoldDuration =
        TimeSpan.FromMilliseconds(1500);
    private RegionVideoRecorder _recorder;
    private readonly RecordingRegionFrameWindow _frameWindow;
    private readonly string _saveDirectory;
    private readonly Action<VideoRecordingPreferences>?
        _recordingPreferencesChanged;
    private VideoRecordingPreferences _recorderPreferences;
    private readonly DispatcherTimer _elapsedTimer;
    private readonly Stopwatch _elapsed = new();
    private readonly TaskCompletionSource<RegionVideoRecordingResult> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private RecordingInputMonitor? _inputMonitor;
    private RecordingInputOverlayWindow? _inputOverlay;
    private bool _allowClose;
    private bool _isCompleting;
    private bool _isInitializingOptions = true;
    private bool _hasSavedWindowPlacement;
    private Action _resetWindowPlacement = static () => { };
    private int _feedbackVersion;

    private VideoRecordingControlWindow(
        ScreenRegion recordingRegion,
        string saveDirectory,
        bool recordSystemAudio,
        bool recordMicrophone,
        VideoRecordingCodec codec,
        int frameRate,
        bool showKeyboardInput,
        bool showMouseInput,
        VideoRecordingOutputFormat outputFormat,
        Action<VideoRecordingPreferences>? recordingPreferencesChanged)
    {
        _saveDirectory = saveDirectory;
        _recordingPreferencesChanged = recordingPreferencesChanged;
        _recorderPreferences = new VideoRecordingPreferences(
            codec,
            RegionVideoRecorder.NormalizeFrameRate(frameRate),
            recordSystemAudio,
            recordMicrophone,
            showKeyboardInput,
            showMouseInput,
            outputFormat);
        _recorder = new RegionVideoRecorder(
            recordingRegion,
            saveDirectory,
            recordSystemAudio,
            recordMicrophone,
            codec,
            frameRate);
        _frameWindow = new RecordingRegionFrameWindow(_recorder.Region);
        _elapsedTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(200),
        };
        _elapsedTimer.Tick += OnElapsedTimerTick;
        _recorder.Failed += OnRecorderFailed;
        InitializeComponent();
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
        UpdateFormatDependentControls();
        _isInitializingOptions = false;
        SourceInitialized += OnSourceInitialized;
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
        VideoRecordingOutputFormat outputFormat = VideoRecordingOutputFormat.Mp4,
        Action<VideoRecordingPreferences>? recordingPreferencesChanged = null)
    {
        var window = new VideoRecordingControlWindow(
            recordingRegion,
            saveDirectory,
            recordSystemAudio,
            recordMicrophone,
            codec,
            frameRate,
            showKeyboardInput,
            showMouseInput,
            outputFormat,
            recordingPreferencesChanged);
        window._frameWindow.Show();
        window.Show();
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
        _ = NativeMethods.SetWindowDisplayAffinity(handle, ExcludeFromCapture);
        if (!_hasSavedWindowPlacement)
        {
            _resetWindowPlacement();
            PositionOutsideRecordingRegion(handle);
        }
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
            _recorder.Start();
            _elapsed.Restart();
            _elapsedTimer.Start();
            RecordingStatusText.Text = "正在录屏";
            RecordingStatusText.Foreground = new SolidColorBrush(
                System.Windows.Media.Color.FromRgb(240, 68, 85));
            RecordingOptionsPanel.Visibility = Visibility.Collapsed;
            StartButton.Visibility = Visibility.Collapsed;
            PauseButton.Visibility = Visibility.Visible;
            PauseButton.IsEnabled = true;
            PauseButton.ToolTip = "暂停录制";
            StopButton.ToolTip = "结束并保存";
            StopIcon.Data = (Geometry)FindResource("StopIconGeometry");
            FinishAndEditButton.Visibility = Visibility.Visible;
            FinishAndEditButton.IsEnabled = true;
            Width = ActiveControlWidth;
            if (!_hasSavedWindowPlacement)
            {
                _ = Dispatcher.BeginInvoke(
                    DispatcherPriority.Loaded,
                    RepositionAutomaticControl);
            }
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
        if (preferences == _recorderPreferences)
        {
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
        FinishAndEditButton.IsEnabled = false;
        _elapsed.Stop();
        _elapsedTimer.Stop();
        DisposeInputFeedback();
        RecordingStatusText.Text = "正在保存";
        EnterCompletionUi();

        var result = await _recorder.StopAsync();
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
        StartButton.IsEnabled = false;
        PauseButton.IsEnabled = false;
        StopButton.IsEnabled = false;
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
        if (e.LeftButton == MouseButtonState.Pressed &&
            e.Source is not System.Windows.Controls.Primitives.ButtonBase &&
            e.Source is not System.Windows.Controls.ComboBox)
        {
            if (e.ClickCount >= 2)
            {
                ResetControlPosition();
                e.Handled = true;
                return;
            }

            EnsureManualPositionTracking();
            DragMove();
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

    private void OnWindowMouseEnter(
        object sender,
        System.Windows.Input.MouseEventArgs e)
    {
        Opacity = 1;
    }

    private void OnWindowMouseLeave(
        object sender,
        System.Windows.Input.MouseEventArgs e)
    {
        UpdateLowProfileOpacity();
    }

    private void UpdateLowProfileOpacity()
    {
        if (IsMouseOver)
        {
            Opacity = 1;
            return;
        }

        Opacity = _recorder.State switch
        {
            RegionVideoRecorderState.Recording => RecordingIdleOpacity,
            RegionVideoRecorderState.Paused => PausedIdleOpacity,
            _ => 1,
        };
    }

    private void EnterCompletionUi()
    {
        RecordingOptionsPanel.Visibility = Visibility.Collapsed;
        StartButton.Visibility = Visibility.Collapsed;
        PauseButton.Visibility = Visibility.Collapsed;
        StopButton.Visibility = Visibility.Collapsed;
        FinishAndEditButton.Visibility = Visibility.Collapsed;
        System.Windows.Controls.Grid.SetColumnSpan(RecordingStatusPanel, 3);
        Opacity = 1;
    }

    private void StartInputFeedback(VideoRecordingPreferences preferences)
    {
        DisposeInputFeedback();
        if (!preferences.ShowKeyboardInput && !preferences.ShowMouseInput)
        {
            return;
        }

        _inputOverlay = new RecordingInputOverlayWindow(_recorder.Region);
        _inputOverlay.Show();
        _inputMonitor = new RecordingInputMonitor(
            preferences.ShowKeyboardInput,
            preferences.ShowMouseInput);
        _inputMonitor.InputChanged += OnRecordingInputChanged;
        _inputMonitor.Start();
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
        if (_inputMonitor is not null)
        {
            _inputMonitor.InputChanged -= OnRecordingInputChanged;
            _inputMonitor.Dispose();
            _inputMonitor = null;
        }

        _inputOverlay?.Close();
        _inputOverlay = null;
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
        _elapsedTimer.Stop();
        _elapsedTimer.Tick -= OnElapsedTimerTick;
        _recorder.Failed -= OnRecorderFailed;
        _recorder.Dispose();
        DisposeInputFeedback();
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
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetWindowDisplayAffinity(
            IntPtr window,
            uint affinity);

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
