using System.IO;
using System.IO.Pipes;
using System.Windows;
using System.Windows.Threading;

namespace Screenshot.App.Infrastructure;

internal sealed class FeatureProcessWorker(
    Dispatcher dispatcher,
    Func<bool> isBusy,
    Func<FeatureMessage, Task> receive,
    Action retire,
    Action disconnected) : IDisposable
{
    internal sealed record Startup(FeatureProcessConnection Connection, FeatureMessage Bootstrap);

    // Connect and decode settings while Application loads its XAML, before it
    // owns any input. Input handover still waits for the ready acknowledgement.
    internal static async Task<Startup> PrepareAsync(string pipeName)
    {
        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        var connection = new FeatureProcessConnection(pipe);
        try
        {
            await pipe.ConnectAsync(15_000).ConfigureAwait(false);
            var bootstrap = await connection.ReadAsync().WaitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
            if (bootstrap.Kind != "bootstrap") throw new InvalidDataException("Missing feature bootstrap.");
            return new(connection, bootstrap);
        }
        catch { connection.Dispose(); throw; }
    }

    private FeatureProcessConnection? _connection;
    private readonly DispatcherTimer _idleTimer = new(DispatcherPriority.ApplicationIdle, dispatcher)
    {
        Interval = TimeSpan.FromSeconds(2),
    };
    private long _sequence;
    private bool _hasCommand;
    private bool _disposed;
    private long _captureSequence;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<long, TaskCompletionSource<bool>> _captureReady = new();

    internal async Task PrepareResidentCaptureAsync(long nativeSelectionId = 0)
    {
        var sequence = Interlocked.Increment(ref _captureSequence);
        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _captureReady[sequence] = ready;
        try
        {
            await SendAsync(new("prepare-capture") { Sequence = sequence, NativeSelectionId = nativeSelectionId }).ConfigureAwait(false);
            var accepted = await ready.Task.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            if (nativeSelectionId != 0 && !accepted) throw new OperationCanceledException("框选已取消。");
            // The parent's Hide acknowledgement is not a compositor fence.
            // Flush on the one-shot capture worker before its early pixel copy.
            Capture.CaptureStartupFrameSource.Shared.RequestCompositionRefresh();
        }
        finally { _captureReady.TryRemove(sequence, out _); }
    }

    public Task ConnectAsync(string pipeName) => ConnectAsync(PrepareAsync(pipeName));

    internal async Task ConnectAsync(Task<Startup> preparation)
    {
        _idleTimer.Tick += OnIdle;
        FeatureActivity.Changed += ScheduleIdle;
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
            new RoutedEventHandler(OnWindowLoaded));
        try
        {
            var startup = await preparation;
            _connection = startup.Connection;
            // Preserve the operation and preparation flag as well as settings.
            // Rebuilding a settings-only message silently skipped cold toolbar
            // preparation while still acknowledging "ready" to the resident.
            await receive(startup.Bootstrap);
            await _connection.SendAsync(new("ready"));
            _ = ReadAsync();
        }
        catch { _connection?.Dispose(); disconnected(); }
    }

    public async Task SendAsync(FeatureMessage message)
    {
        try { if (_connection is { } connection) await connection.SendAsync(message).ConfigureAwait(false); }
        catch (Exception exception) when (exception is IOException or OperationCanceledException or ObjectDisposedException)
        { /* The reader owns disconnect/shutdown. */ }
    }

    private async Task ReadAsync()
    {
        try
        {
            while (!_disposed)
            {
                var message = await _connection!.ReadAsync().ConfigureAwait(false);
                Capture.CaptureTimingDiagnostics.Mark("worker-message-received", $"kind={message.Kind} selection={message.NativeSelectionId} sequence={message.Sequence} width={message.Width} height={message.Height}");
                if (message.Kind == "capture-hidden")
                {
                    if (_captureReady.TryRemove(message.Sequence, out var ready)) ready.TrySetResult(message.Flag);
                    continue;
                }
                await dispatcher.InvokeAsync(() =>
                {
                    Capture.CaptureTimingDiagnostics.Mark("worker-message-dispatched", $"kind={message.Kind} selection={message.NativeSelectionId} sequence={message.Sequence}");
                    if (message.Kind == "retire")
                    {
                        if (message.Sequence == _sequence && !isBusy() && FeatureActivity.Count == 0)
                            retire();
                        else
                            _ = SendAsync(new("busy"));
                        return;
                    }
                    if (message.Kind == "command")
                    {
                        _hasCommand = true;
                        _sequence = message.Sequence;
                    }
                    // Start in dispatcher order; long captures must not block
                    // subsequent end-recording or settings commands on the pipe.
                    _ = DispatchAsync(message);
                });
            }
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException or ObjectDisposedException)
        {
            if (!_disposed) await dispatcher.InvokeAsync(disconnected);
        }
    }

    private async Task DispatchAsync(FeatureMessage message)
    {
        using var activity = FeatureActivity.Begin();
        try { await receive(message); }
        catch (OperationCanceledException) { /* User cancelled before the native selection handoff. */ }
        catch (Exception exception)
        {
            await SendAsync(new("error") { Text = "本次操作未能完成：" + exception.Message });
            await Task.Run(() =>
            {
                try
                {
                    Directory.CreateDirectory(Core.AppMetadata.DiagnosticsDirectoryPath);
                    App.AppendCrashLog(Path.Combine(Core.AppMetadata.DiagnosticsDirectoryPath, "feature-error.log"),
                        $"[{DateTime.Now:O}] {exception}{Environment.NewLine}");
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            });
        }
        finally
        {
            message.Image?.Dispose();
            message.Image = null;
            ScheduleIdle();
        }
    }

    private void OnWindowLoaded(object sender, RoutedEventArgs args)
    {
        if (sender is not Window window || !ReferenceEquals(sender, args.OriginalSource)) return;
        window.Closed -= OnWindowClosed;
        window.Closed += OnWindowClosed;
        window.IsVisibleChanged -= OnVisibilityChanged;
        window.IsVisibleChanged += OnVisibilityChanged;
        ScheduleIdle();
    }

    private void OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs args) => ScheduleIdle();
    private void OnWindowClosed(object? sender, EventArgs args)
    {
        if (sender is Window window)
        {
            window.Closed -= OnWindowClosed;
            window.IsVisibleChanged -= OnVisibilityChanged;
        }
        ScheduleIdle();
    }

    internal void ScheduleIdle()
    {
        if (_disposed || dispatcher.HasShutdownStarted) return;
        if (!dispatcher.CheckAccess())
        {
            _ = dispatcher.BeginInvoke(ScheduleIdle);
            return;
        }
        _idleTimer.Stop();
        if (_hasCommand && FeatureActivity.Count == 0 && !isBusy()) _idleTimer.Start();
    }

    private void OnIdle(object? sender, EventArgs args)
    {
        _idleTimer.Stop();
        // Retire the whole UI process so WPF and native rendering caches are
        // released as well. Active windows/tasks and newer commands still veto
        // retirement through the existing two-way acknowledgement.
        if (_hasCommand && FeatureActivity.Count == 0 && !isBusy())
            _ = SendAsync(new("idle") { Sequence = _sequence });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _idleTimer.Stop();
        _idleTimer.Tick -= OnIdle;
        FeatureActivity.Changed -= ScheduleIdle;
        _connection?.Dispose();
    }
}
