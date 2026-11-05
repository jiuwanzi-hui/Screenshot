using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using Screenshot.App.Core;
using Screenshot.App.Capture;

namespace Screenshot.App.Infrastructure;

/// <summary>
/// The resident shell never constructs settings/capture/model windows. Their
/// process owns those runtimes. Capture UI may stay ready after use; explicit
/// low-memory mode and system memory pressure use the orderly retirement handshake.
/// </summary>
internal sealed class FeatureProcessHost(
    Dispatcher dispatcher,
    Func<FeatureMessage> suspendInput,
    Action resumeInput,
    Action<FeatureMessage> receive,
    Func<Core.AppSettings>? startupSettings = null,
    Func<long, bool>? releaseResidentSelection = null) : IDisposable
{
    internal const string WorkerArgument = "--feature-worker";
    private readonly SemaphoreSlim _commands = new(1, 1);
    private Process? _process;
    private FeatureProcessConnection? _connection;
    private TaskCompletionSource? _ready;
    private Task _exited = Task.CompletedTask;
    private bool _retiring;
    private TaskCompletionSource? _retirement;
    private bool _disposed;
    private long _sequence;
    private Core.AppSettings? _startupSettings;

    // Remains non-null until input has been restored on the dispatcher.
    internal int? ProcessId => _process?.Id;

    public async Task SendAsync(FeatureMessage message, Func<bool>? isCancelled = null,
        Func<Task<FeatureMessage>>? prepareMessage = null, bool startIfNeeded = true,
        Func<bool>? prepareCaptureToolbar = null, Task? activationReady = null,
        Func<FeatureMessage?>? prepareCaptureFrame = null)
    {
        CaptureTimingDiagnostics.Mark("feature-command-request", $"action={message.HotKey} worker={_process?.Id} connected={_connection is not null}");
        try
        {
            await _commands.WaitAsync();
            try
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_retiring && _retirement is { } retirement) await retirement.Task;
                if (!startIfNeeded && _connection is null) return;
                if (_connection is null) await StartAsync(message, activationReady);
                // A warm child follows the same first-frame barrier as a cold
                // child. No feature window/input takeover precedes the pixels.
                if (activationReady is not null) await activationReady;
                if (isCancelled?.Invoke() != true && prepareCaptureToolbar?.Invoke() == true)
                {
                    CaptureTimingDiagnostics.Mark("feature-toolbar-prepare-send");
                    await _connection!.SendAsync(new("prepare-capture-toolbar") { HotKey = message.HotKey });
                }
                if (isCancelled?.Invoke() != true && prepareCaptureFrame?.Invoke() is { } frame)
                    await _connection!.SendAsync(frame);
                FeatureMessage outgoing;
                try
                {
                    outgoing = isCancelled?.Invoke() == true ? new("command") { Command = "cancel" }
                        : prepareMessage is not null ? await prepareMessage() : message;
                }
                catch
                {
                    await _connection!.SendAsync(new("command") { Command = "cancel", Sequence = ++_sequence });
                    throw;
                }
                if (_process is { } process) _ = AllowSetForegroundWindow((uint)process.Id);
                CaptureTimingDiagnostics.Mark("feature-command-send-start", $"selection={outgoing.NativeSelectionId} sequence={_sequence + 1}");
                await _connection!.SendAsync(outgoing with { Sequence = ++_sequence });
                CaptureTimingDiagnostics.Mark("feature-command-send-end", $"selection={outgoing.NativeSelectionId} sequence={_sequence}");
            }
            finally { _commands.Release(); }
        }
        finally
        {
            message.Image?.Dispose();
            message.Image = null;
        }
    }

    private async Task StartAsync(FeatureMessage command, Task? activationReady)
    {
        var pipeName = $"SnapCut.Features.{Environment.ProcessId}.{Guid.NewGuid():N}";
        var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _ready = ready;
        _retiring = false;
        _sequence = 0;
        var start = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = AppContext.BaseDirectory,
        };
        start.ArgumentList.Add(WorkerArgument);
        start.ArgumentList.Add(pipeName);
        start.ArgumentList.Add("--background");
        if (WpfRenderingCompatibility.GetExplicitRenderingArgument(Environment.GetCommandLineArgs()) is { } renderingArgument)
            start.ArgumentList.Add(renderingArgument);
        if (command.Command == "settings" || command.HotKey == HotKeyAction.OpenSettings)
            start.ArgumentList.Add(FeatureStartupCompilation.SettingsArgument);
        if (command.HotKey is HotKeyAction.RegionCapture or HotKeyAction.RecognizeText or
            HotKeyAction.PinImage or HotKeyAction.ScrollCapture or HotKeyAction.VideoRecording ||
            command.Command is "repeat" or "all-screens" or "preset")
            start.ArgumentList.Add(FeatureStartupCompilation.Argument);
        try
        {
            CaptureTimingDiagnostics.Mark("worker-process-start");
            // Process creation (including the first security/image-loader
            // checks) must not stall the dispatcher serving low-level input.
            _process = await Task.Run(() => Process.Start(start)) ?? throw new IOException("无法启动功能进程。");
            CaptureTimingDiagnostics.Mark("worker-process-created", $"worker={_process.Id}");
            _exited = ObserveExitAsync(_process);
            ObjectDisposedException.ThrowIf(_disposed, this);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await server.WaitForConnectionAsync(timeout.Token);
            CaptureTimingDiagnostics.Mark("worker-pipe-connected", $"worker={_process.Id}");
            if (!GetNamedPipeClientProcessId(server.SafePipeHandle, out var clientId) || clientId != _process.Id)
                throw new IOException("功能进程管道身份不匹配。");
            _connection = new FeatureProcessConnection(server);
            _startupSettings = startupSettings?.Invoke();
            await _connection.SendAsync(new("bootstrap") { Settings = _startupSettings,
                HotKey = command.HotKey, Flag = activationReady is not null });
            CaptureTimingDiagnostics.Mark("worker-bootstrap-sent", $"worker={_process.Id}");
            _ = ReadAsync(_connection, ready, activationReady, timeout.Token);
            await ready.Task.WaitAsync(timeout.Token);
            CaptureTimingDiagnostics.Mark("worker-activated", $"worker={_process.Id}");
        }
        catch
        {
            server.Dispose();
            // Only our never-ready child is stopped. Never terminate a worker
            // with a recording/export in progress to meet an idle target.
            if (_process is { HasExited: false } process) process.Kill();
            await _exited;
            throw;
        }
    }

    internal async Task ReadAsync(FeatureProcessConnection connection, TaskCompletionSource ready,
        Task? activationReady, CancellationToken startupToken)
    {
        try
        {
            while (!_disposed)
            {
                var message = await connection.ReadAsync().ConfigureAwait(false);
                CaptureTimingDiagnostics.Mark("resident-message-received", $"kind={message.Kind} selection={message.NativeSelectionId} sequence={message.Sequence}");
                if (message.Kind == "ready")
                {
                    if (activationReady is not null)
                        await activationReady.WaitAsync(startupToken).ConfigureAwait(false);
                    var activation = await dispatcher.InvokeAsync(suspendInput);
                    if (ReferenceEquals(activation.Settings, _startupSettings)) activation = activation with { Settings = null };
                    _startupSettings = null;
                    await connection.SendAsync(activation).ConfigureAwait(false);
                    CaptureTimingDiagnostics.Mark("worker-activation-sent");
                }
                else if (message.Kind == "started") ready.TrySetResult();
                else if (message.Kind == "prepare-capture")
                {
                    var accepted = message.NativeSelectionId == 0 || await dispatcher.InvokeAsync(
                        () => releaseResidentSelection?.Invoke(message.NativeSelectionId) == true);
                    if (accepted) await dispatcher.InvokeAsync(() => receive(message));
                    await connection.SendAsync(new("capture-hidden") { Sequence = message.Sequence,
                        NativeSelectionId = message.NativeSelectionId, Flag = accepted }).ConfigureAwait(false);
                }
                else if (message.Kind == "idle")
                {
                    await _commands.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                    try
                    {
                        // A command racing with idle always wins. The child may
                        // exit only after the shell stops sending to that child.
                        if (ReferenceEquals(_connection, connection) && message.Sequence == _sequence)
                        {
                            _retiring = true;
                            _retirement = new(TaskCreationOptions.RunContinuationsAsynchronously);
                            await connection.SendAsync(new("retire") { Sequence = _sequence }).ConfigureAwait(false);
                        }
                    }
                    finally { _commands.Release(); }
                }
                else if (message.Kind == "busy")
                {
                    _retiring = false;
                    _retirement?.TrySetResult();
                }
                else await dispatcher.InvokeAsync(() => receive(message));
            }
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException or ObjectDisposedException)
        {
            ready.TrySetException(exception);
        }
    }

    private async Task ObserveExitAsync(Process process)
    {
        await process.WaitForExitAsync().ConfigureAwait(false);
        CaptureTimingDiagnostics.Mark("worker-process-exited", $"worker={process.Id} code={process.ExitCode}");
        await dispatcher.InvokeAsync(() =>
        {
            if (!ReferenceEquals(_process, process)) return;
            var unexpected = !_disposed && !_retiring && _ready?.Task.IsCompletedSuccessfully == true;
            _connection?.Dispose();
            _connection = null;
            _process = null;
            _retiring = false;
            _retirement?.TrySetResult();
            _ready?.TrySetException(new IOException("功能进程已退出，请重试。"));
            if (!_disposed) resumeInput();
            if (unexpected) receive(new("error") { Text = "功能进程意外结束，快捷键已恢复，请重新打开所需功能。" });
            process.Dispose();
        });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // EOF asks the worker to finish native recording and pending writes,
        // then exit. It also prevents orphaned workers when the shell crashes.
        _connection?.Dispose();
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint processId);
}
