using System.Net.Http;
using System.Windows.Threading;
using Screenshot.App.Capture;
using Screenshot.App.Core;
using Screenshot.App.Infrastructure;

namespace Screenshot.App.Text.Realtime;

// Owns the operation, not a control window. Only selection and click-through
// translated content are shown; the same hotkey/tray command stops the operation.
internal sealed class RealtimeTranslationController : IAsyncDisposable
{
    private readonly Func<AppSettings> _settings;
    private readonly HttpClient _http;
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly DispatcherTimer _restartTimer;
    private readonly SemaphoreSlim _restartGate = new(1, 1);
    private readonly CancellationTokenSource _selectionCancellation = new();
    private Task<ScreenRegion?>? _selectionTask;
    private ResidentSelectionWindow? _selector;
    private readonly IDisposable _activity = FeatureActivity.Begin();
    private RealtimeTranslationSession? _session;
    private RealtimeOverlayWindow? _overlay;
    private RealtimeTranslationUpdate? _output;
    private ScreenRegion? _region;
    private nint _targetWindow;
    private bool _closed;
    private bool _selecting;
    private long _generation;
    private string? _lastStatus;
    internal event EventHandler? Closed;
    internal Task Shutdown { get; private set; } = Task.CompletedTask;

    internal RealtimeTranslationController(Func<AppSettings> settings, HttpClient http)
    {
        _settings = settings; _http = http;
        _restartTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
        _restartTimer.Tick += OnRestartTick;
        TranslationTargetPreferences.Changed += OnTargetChanged;
    }

    internal async Task StartAsync(nint target, ScreenRegion? region = null)
    {
        try
        {
            _selecting = region is null;
            _region = region ?? await (_selectionTask = SelectRegionAsync());
        }
        catch (OperationCanceledException) when (_selectionCancellation.IsCancellationRequested) { Close(); return; }
        finally { _selecting = false; }
        if (_closed) return;
        if (_region is not { Width: > 10, Height: > 10 }) { Close(); return; }
        // Resolve after the native selector is hidden, before our output is
        // shown. Capture the source window behind the translated overlay;
        // global display-affinity exclusion also hid it from remote viewers.
        _targetWindow = RealtimeWindowTarget.FromRegion(_region.Value);
        if (_targetWindow == 0)
        {
            ShowStatus("请在同一个文档或游戏窗口内框选实时翻译区域");
            return;
        }
        await RestartAsync();
    }

    private async Task<ScreenRegion?> SelectRegionAsync()
    {
        var token = _selectionCancellation.Token;
        using var frame = await NativeDesktopFrame.CaptureAsync(VirtualScreen.GetBounds(), token);
        token.ThrowIfCancellationRequested();
        using var selector = new ResidentSelectionWindow(frame,
            _settings() with { CaptureAspectRatio = CaptureAspectRatio.Free }, () => { }, allowColorPicker: false);
        _selector = selector;
        try
        {
            await selector.ShowAsync(token);
            // Native input, selection and capture all use physical screen pixels.
            // Mouse release completes the selection; no screenshot editor or
            // confirmation toolbar is created in an already-running UI process.
            return (await selector.Selection.WaitAsync(token))?.Region;
        }
        finally { _selector = null; }
    }

    private void OnTargetChanged(object? sender, EventArgs args) => SettingsChanged();
    internal void SettingsChanged()
    {
        if (_closed) return;
        _generation++; _output = null; _overlay?.ClearTranslation(); _session?.SetPaused(true);
        _restartTimer.Stop(); _restartTimer.Start();
    }
    private void OnRestartTick(object? sender, EventArgs args) { _restartTimer.Stop(); _ = RestartAsync(); }

    private async Task RestartAsync()
    {
        var generation = ++_generation;
        _output = null; _overlay?.ClearTranslation();
        await _restartGate.WaitAsync();
        try
        {
            if (_session is { } previous) { _session = null; await previous.DisposeAsync(); }
            if (_closed || generation != _generation || _region is null || _targetWindow == 0) return;
            var settings = _settings();
            var provider = TranslationProviderFactory.Create(settings, new DpapiTranslationCredentialStore(), _http);
            var source = new RealtimeCaptureSource(_targetWindow, _region);
            var target = TranslationTargetPreferences.Language ?? settings.TranslationTargetLanguage;
            var glossary = new RealtimeGlossary(settings.RealtimeTranslation.GlossaryLanguage == target ? settings.RealtimeTranslation.Glossary : "");
            var session = new RealtimeTranslationSession(source,
                (image, token) => OcrProviderFactory.RecognizeAsync(image, settings, token), provider, target, glossary,
                () => RealtimeWindowTarget.IsRegionVisible(_targetWindow, _region.Value));
            _session = session;
            void Ui(Action action) => _dispatcher.BeginInvoke(() =>
            { if (!_closed && generation == _generation && ReferenceEquals(_session, session)) action(); });
            session.Updated += output => Ui(() =>
            {
                _output = output;
                RefreshOverlay();
                _lastStatus = output.Status;
            });
            session.Invalidated += () => Ui(() =>
            {
                _output = null; _overlay?.ClearTranslation();
                if (_region is { } region)
                    _overlay?.PresentStatus(region, "画面已变化，正在更新译文…", _settings().RealtimeTranslation);
            });
            session.StatusChanged += status => Ui(() => ShowStatus(status));
            ShowStatus("正在识别选区；再次按实时翻译快捷键或使用托盘菜单停止");
            session.Start();
            _ = ObserveCompletionAsync(session, generation);
        }
        catch (Exception exception)
        {
            if (!_closed)
            {
                Close();
                _ = StartupFeedbackWindow.ShowAsync("无法开始实时翻译：" + exception.Message);
            }
        }
        finally { _restartGate.Release(); }
    }

    private async Task ObserveCompletionAsync(RealtimeTranslationSession session, long generation)
    {
        try { await session.Completion; }
        catch (Exception exception) { _lastStatus = exception.Message; }
        if (_closed || generation != _generation || !ReferenceEquals(_session, session)) return;
        var message = _lastStatus ?? "实时翻译已停止";
        await _restartGate.WaitAsync();
        try
        {
            if (_closed || generation != _generation || !ReferenceEquals(_session, session)) return;
            _session = null;
            await session.DisposeAsync();
            _output = null; _overlay?.ClearTranslation();
            // Keep the cause readable in the selected region. Acquisition and
            // inference are already stopped; the hotkey closes this feedback.
            ShowStatus(message);
        }
        finally { _restartGate.Release(); }
    }

    private void ShowStatus(string status)
    {
        _lastStatus = status;
        if (_closed || _region is not { } region) return;
        _overlay ??= new RealtimeOverlayWindow();
        _overlay.PresentStatus(region, _output?.IsReadingSnapshot == true ? _output.Status : status,
            _settings().RealtimeTranslation);
    }

    internal void RefreshOverlay()
    {
        if (_closed) return;
        if (_output is null)
        {
            _overlay?.ClearTranslation();
            if (_lastStatus is { } status && !_selecting) ShowStatus(status);
            return;
        }
        _overlay ??= new RealtimeOverlayWindow();
        _overlay.Present(_output, _settings().RealtimeTranslation.Normalize(), false);
    }

    internal void Close()
    {
        if (_closed) return;
        _closed = true; _generation++;
        _selectionCancellation.Cancel();
        if (_selecting) _selector?.Cancel();
        _restartTimer.Stop(); _restartTimer.Tick -= OnRestartTick;
        TranslationTargetPreferences.Changed -= OnTargetChanged;
        _overlay?.Close(); _overlay = null; _output = null;
        Shutdown = StopAsync();
    }

    private async Task StopAsync()
    {
        if (_selectionTask is { } selection)
        {
            try { await selection; }
            catch (Exception) { /* StartAsync reports selection errors. */ }
        }
        await _restartGate.WaitAsync();
        try { if (_session is { } session) { _session = null; await session.DisposeAsync(); } }
        finally
        {
            _restartGate.Release(); _selectionCancellation.Dispose(); _activity.Dispose();
            Closed?.Invoke(this, EventArgs.Empty);
            Closed = null;
        }
    }
    public async ValueTask DisposeAsync() { Close(); await Shutdown; }
}
