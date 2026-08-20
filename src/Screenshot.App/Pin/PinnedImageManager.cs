using Screenshot.App.Capture;
using Screenshot.App.Core;
using Screenshot.App.Text;
using System.Windows.Threading;

namespace Screenshot.App.Pin;

public sealed class PinnedImageManager : IDisposable
{
    private readonly HashSet<PinnedImageWindow> _windows = [];
    private readonly Func<CapturedImage, Task<OcrRecognitionResult>>?
        _recognizeTextAsync;
    private readonly Func<OcrRecognitionResult, Task<TranslationSegmentsResult>>?
        _translateTextAsync;
    private readonly Action? _openSettings;
    private readonly Func<AppSettings>? _settingsProvider;
    private readonly Action<string>? _customStrokeColorChanged;
    private readonly Action<int[]>? _customColorPaletteChanged;
    private readonly Action<ArrowStyle>? _arrowStyleChanged;
    private readonly Action<ArrowToolMode>? _arrowToolModeChanged;
    private readonly Action<ShapeToolMode>? _shapeToolModeChanged;
    private readonly Action<AnnotationToolMode>? _lastAnnotationToolChanged;
    private readonly PinnedImagePersistenceStore _persistenceStore;
    private readonly Dictionary<PinnedImageWindow, (double Left, double Top)>
        _lastPositions = [];
    private readonly DispatcherTimer _saveTimer;
    private PinnedImageGroupWindow? _groupWindow;
    private bool _updatingGroup;
    private bool _hasHiddenWindows;
    private bool _disposed;

    public PinnedImageManager(
        Func<CapturedImage, Task<OcrRecognitionResult>>? recognizeTextAsync = null,
        Func<OcrRecognitionResult, Task<TranslationSegmentsResult>>?
            translateTextAsync = null,
        Action? openSettings = null,
        Func<AppSettings>? settingsProvider = null,
        Action<string>? customStrokeColorChanged = null,
        Action<int[]>? customColorPaletteChanged = null,
        Action<ArrowStyle>? arrowStyleChanged = null,
        Action<ArrowToolMode>? arrowToolModeChanged = null,
        Action<ShapeToolMode>? shapeToolModeChanged = null,
        Action<AnnotationToolMode>? lastAnnotationToolChanged = null)
    {
        _recognizeTextAsync = recognizeTextAsync;
        _translateTextAsync = translateTextAsync;
        _openSettings = openSettings;
        _settingsProvider = settingsProvider;
        _customStrokeColorChanged = customStrokeColorChanged;
        _customColorPaletteChanged = customColorPaletteChanged;
        _arrowStyleChanged = arrowStyleChanged;
        _arrowToolModeChanged = arrowToolModeChanged;
        _shapeToolModeChanged = shapeToolModeChanged;
        _lastAnnotationToolChanged = lastAnnotationToolChanged;
        _persistenceStore = new PinnedImagePersistenceStore();
        _saveTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(350),
        };
        _saveTimer.Tick += OnSaveTimerTick;
    }

    public int Count => _windows.Count;

    public bool HasHiddenWindows => _hasHiddenWindows;

    public event EventHandler? DisplayStateChanged;

    internal IReadOnlyList<PinnedImageWindow> Windows => _windows.ToArray();

    internal PinnedImageGroupWindow? GroupWindow => _groupWindow;

    public void Pin(CapturedImage capturedImage)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(capturedImage);

        PinnedImageWindow? window = null;

        try
        {
            window = new PinnedImageWindow(
                capturedImage,
                _recognizeTextAsync,
                _translateTextAsync,
                _settingsProvider,
                _customStrokeColorChanged,
                _customColorPaletteChanged,
                _arrowStyleChanged,
                _arrowToolModeChanged,
                _shapeToolModeChanged,
                _lastAnnotationToolChanged);
            AddAndShowWindow(window);
        }
        catch
        {
            if (window is not null)
            {
                DetachWindow(window);
                _windows.Remove(window);
            }

            capturedImage.Dispose();
            throw;
        }
    }

    public void RestorePersisted()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        foreach (var state in _persistenceStore.Load())
        {
            var image = _persistenceStore.LoadImage(state);
            if (image is null)
            {
                continue;
            }

            var window = new PinnedImageWindow(
                image,
                _recognizeTextAsync,
                _translateTextAsync,
                _settingsProvider,
                _customStrokeColorChanged,
                _customColorPaletteChanged,
                _arrowStyleChanged,
                _arrowToolModeChanged,
                _shapeToolModeChanged,
                _lastAnnotationToolChanged)
            {
                PersistenceId = state.Id,
            };
            window.SetPersistentState(true);
            AddAndShowWindow(window);
            window.Left = state.Left;
            window.Top = state.Top;
            window.Width = Math.Clamp(state.Width, window.MinWidth, window.MaxWidth);
            window.Height = Math.Clamp(state.Height, window.MinHeight, window.MaxHeight);
            _lastPositions[window] = (window.Left, window.Top);
        }
    }

    public void HideAll()
    {
        if (_windows.Count == 0)
        {
            _hasHiddenWindows = false;
            NotifyDisplayStateChanged();
            return;
        }

        _groupWindow?.Hide();
        foreach (var window in _windows.Where(window => !window.IsGrouped))
        {
            window.Hide();
        }
        _hasHiddenWindows = _windows.Count > 0;
        NotifyDisplayStateChanged();
    }

    public void ShowAll()
    {
        if (_groupWindow is not null)
        {
            _groupWindow.Show();
        }
        foreach (var window in _windows.Where(window => !window.IsGrouped))
        {
            window.Show();
        }
        _hasHiddenWindows = false;
        NotifyDisplayStateChanged();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _saveTimer.Stop();
        SavePersistentIndex();
        CloseGroupWindow();

        foreach (var window in _windows.ToArray())
        {
            DetachWindow(window);
            window.Close();
        }

        _windows.Clear();
        _lastPositions.Clear();
        _saveTimer.Tick -= OnSaveTimerTick;
    }

    private void OnPinnedImageWindowClosed(object? sender, EventArgs e)
    {
        if (sender is not PinnedImageWindow window)
        {
            return;
        }

        DetachWindow(window);
        _windows.Remove(window);
        _lastPositions.Remove(window);
        if (_windows.Count == 0)
        {
            _hasHiddenWindows = false;
        }
        if (window.IsPersistent && window.PersistenceId is { } id)
        {
            _persistenceStore.Delete(id);
            SavePersistentIndex();
        }
        ReconcileGroupWindow();
        NotifyDisplayStateChanged();
    }

    private void OnPinnedImageSettingsRequested(object? sender, EventArgs e)
    {
        _openSettings?.Invoke();
    }

    private void AddAndShowWindow(PinnedImageWindow window)
    {
        AttachWindow(window);
        _windows.Add(window);
        if (_hasHiddenWindows)
        {
            window.Hide();
        }
        else
        {
            window.Show();
        }
        _lastPositions[window] = (window.Left, window.Top);
        NotifyDisplayStateChanged();
    }

    private void AttachWindow(PinnedImageWindow window)
    {
        window.Closed += OnPinnedImageWindowClosed;
        window.SettingsRequested += OnPinnedImageSettingsRequested;
        window.HideAllRequested += OnHideAllRequested;
        window.GroupMembershipChanged += OnGroupMembershipChanged;
        window.PersistenceChanged += OnPersistenceChanged;
        window.LocationChanged += OnWindowLocationChanged;
        window.SizeChanged += OnWindowSizeChanged;
    }

    private void DetachWindow(PinnedImageWindow window)
    {
        window.Closed -= OnPinnedImageWindowClosed;
        window.SettingsRequested -= OnPinnedImageSettingsRequested;
        window.HideAllRequested -= OnHideAllRequested;
        window.GroupMembershipChanged -= OnGroupMembershipChanged;
        window.PersistenceChanged -= OnPersistenceChanged;
        window.LocationChanged -= OnWindowLocationChanged;
        window.SizeChanged -= OnWindowSizeChanged;
    }

    private void OnHideAllRequested(object? sender, EventArgs e) => HideAll();

    private void OnGroupMembershipChanged(object? sender, EventArgs e)
    {
        if (sender is PinnedImageWindow window)
        {
            _lastPositions[window] = (window.Left, window.Top);
            ReconcileGroupWindow();
        }
    }

    private void OnPersistenceChanged(object? sender, EventArgs e)
    {
        if (sender is not PinnedImageWindow window)
        {
            return;
        }

        if (!window.IsPersistent)
        {
            if (window.PersistenceId is { } previousId)
            {
                _persistenceStore.Delete(previousId);
            }
            window.PersistenceId = null;
            SavePersistentIndex();
            return;
        }

        window.PersistenceId ??= Guid.NewGuid().ToString("N");
        using var image = window.CloneImage();
        _persistenceStore.SaveImage(window.PersistenceId, image);
        SavePersistentIndex();
    }

    private void OnWindowLocationChanged(object? sender, EventArgs e)
    {
        if (sender is not PinnedImageWindow moved)
        {
            return;
        }

        _lastPositions[moved] = (moved.Left, moved.Top);
        SchedulePersistentSave(moved);
    }

    private void ReconcileGroupWindow()
    {
        if (_updatingGroup || _disposed)
        {
            return;
        }

        var members = _windows
            .Where(window => window.IsGrouped)
            .OrderBy(window => window.Top)
            .ThenBy(window => window.Left)
            .ToArray();
        if (members.Length < 2)
        {
            CloseGroupWindow();
            if (!_hasHiddenWindows)
            {
                foreach (var member in members)
                {
                    member.Show();
                }
            }
            return;
        }

        if (_groupWindow is null)
        {
            _groupWindow = new PinnedImageGroupWindow(members);
            _groupWindow.UngroupRequested += OnUngroupRequested;
            _groupWindow.CloseGroupRequested += OnCloseGroupRequested;
            _groupWindow.Closed += OnGroupWindowClosed;
            ApplyGroupWindowBounds(_groupWindow, members);
        }
        else
        {
            _groupWindow.SetMembers(members);
        }

        foreach (var member in members)
        {
            member.Hide();
        }
        if (!_hasHiddenWindows)
        {
            _groupWindow.Show();
        }
    }

    private static void ApplyGroupWindowBounds(
        PinnedImageGroupWindow groupWindow,
        IReadOnlyList<PinnedImageWindow> members)
    {
        var workArea = System.Windows.SystemParameters.WorkArea;
        var left = members.Min(window => window.Left);
        var top = members.Min(window => window.Top);
        var right = members.Max(window => window.Left + window.Width);
        var bottom = members.Max(window => window.Top + window.Height);
        groupWindow.Width = Math.Clamp(
            Math.Max(620, right - left),
            groupWindow.MinWidth,
            workArea.Width * 0.92);
        groupWindow.Height = Math.Clamp(
            Math.Max(360, bottom - top),
            groupWindow.MinHeight,
            workArea.Height * 0.90);
        groupWindow.Left = Math.Clamp(
            left,
            workArea.Left,
            Math.Max(workArea.Left, workArea.Right - groupWindow.Width));
        groupWindow.Top = Math.Clamp(
            top,
            workArea.Top,
            Math.Max(workArea.Top, workArea.Bottom - groupWindow.Height));
    }

    private void OnUngroupRequested(object? sender, EventArgs e)
    {
        var members = _windows.Where(window => window.IsGrouped).ToArray();
        _updatingGroup = true;
        try
        {
            CloseGroupWindow();
            foreach (var member in members)
            {
                member.SetGroupedState(false);
                if (!_hasHiddenWindows)
                {
                    member.Show();
                }
                if (_lastPositions.TryGetValue(member, out var position))
                {
                    member.Left = position.Left;
                    member.Top = position.Top;
                }
            }
        }
        finally
        {
            _updatingGroup = false;
        }
    }

    internal void UngroupAll() => OnUngroupRequested(this, EventArgs.Empty);

    private void OnCloseGroupRequested(object? sender, EventArgs e)
    {
        var members = _windows.Where(window => window.IsGrouped).ToArray();
        _updatingGroup = true;
        try
        {
            CloseGroupWindow();
            foreach (var member in members)
            {
                member.Close();
            }
        }
        finally
        {
            _updatingGroup = false;
        }
    }

    private void OnGroupWindowClosed(object? sender, EventArgs e)
    {
        if (sender is not PinnedImageGroupWindow groupWindow ||
            !ReferenceEquals(groupWindow, _groupWindow))
        {
            return;
        }

        groupWindow.ApplyCompositeToMembers();
        groupWindow.UngroupRequested -= OnUngroupRequested;
        groupWindow.CloseGroupRequested -= OnCloseGroupRequested;
        groupWindow.Closed -= OnGroupWindowClosed;
        _groupWindow = null;
        UngroupAll();
    }

    private void CloseGroupWindow()
    {
        if (_groupWindow is null)
        {
            return;
        }

        _groupWindow.ApplyCompositeToMembers();
        _groupWindow.UngroupRequested -= OnUngroupRequested;
        _groupWindow.CloseGroupRequested -= OnCloseGroupRequested;
        _groupWindow.Closed -= OnGroupWindowClosed;
        _groupWindow.Close();
        _groupWindow = null;
    }

    private void OnWindowSizeChanged(object sender, System.Windows.SizeChangedEventArgs e)
    {
        if (sender is PinnedImageWindow window)
        {
            SchedulePersistentSave(window);
        }
    }

    private void SchedulePersistentSave(PinnedImageWindow window)
    {
        if (!window.IsPersistent)
        {
            return;
        }
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void OnSaveTimerTick(object? sender, EventArgs e)
    {
        _saveTimer.Stop();
        SavePersistentIndex();
    }

    private void SavePersistentIndex()
    {
        var states = _windows
            .Where(window => window.IsPersistent &&
                             window.PersistenceId is not null)
            .Select(window => new PinnedImageState(
                window.PersistenceId!,
                PinnedImagePersistenceStore.GetImageFileName(
                    window.PersistenceId!),
                window.Left,
                window.Top,
                window.Width,
                window.Height));
        _persistenceStore.SaveIndex(states);
    }

    private void NotifyDisplayStateChanged()
    {
        if (!_disposed)
        {
            DisplayStateChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
