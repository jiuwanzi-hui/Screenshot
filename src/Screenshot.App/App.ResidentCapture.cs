using Screenshot.App.Capture;
using Screenshot.App.Core;
using Screenshot.App.Infrastructure;

namespace Screenshot.App;

public partial class App
{
    private ResidentSelectionWindow? _residentSelectionWindow;
    private CancellationTokenSource? _residentSelectionCancellation;
    private TaskCompletionSource? _residentSelectionPresented;
    private long _residentSelectionId;
    private bool _waitingForResidentSelection;
    private long _activeResidentSelectionId;

    internal static bool CanUseResidentSelection(FeatureMessage command) =>
        command.HotKey is HotKeyAction.RegionCapture or HotKeyAction.PinImage or HotKeyAction.VideoRecording or HotKeyAction.RealtimeTranslation &&
        command.Image is null && command.Button is null;

    // Invoked on the resident dispatcher before the worker receives capture-hidden.
    // A stale/cancelled selection must never publish a pin or leave a recording
    // toolbar behind. Hide synchronously so a recorder cannot capture this DIB.
    private bool ReleaseResidentSelection(long id)
    {
        if (id != _residentSelectionId || _residentSelectionWindow is not { IsCancelled: false } window ||
            _residentSelectionCancellation?.IsCancellationRequested != false) return false;
        window.Hide();
        _residentSelectionPresented?.TrySetResult();
        return true;
    }

    private async Task SendResidentSelectionAsync(FeatureMessage command, int escapeVersion)
    {
        using var cancellation = new CancellationTokenSource();
        _residentSelectionCancellation = cancellation;
        var token = cancellation.Token;
        var id = ++_residentSelectionId;
        CaptureTimingDiagnostics.Mark("resident-selection-start", $"selection={id} action={command.HotKey}");
        _residentSelectionPresented = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var transferred = false;
        var framePrepared = false;
        NativeDesktopFrame? frame = null;
        ResidentSelectionWindow? window = null;
        var selectionReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? send = null;
        _isCaptureInProgress = true;
        _idleEfficiency?.SetCaptureActive(true);
        MemoryFootprint.SetInteractionActive(true);
        _floatingCaptureWindow?.SetCaptureInProgress(true);
        try
        {
            // Start the hidden runtime while obtaining the first desktop frame.
            // Input takeover is gated until those pixels are safe and the native
            // selection exists; startup must not dismiss a captured hover card.
            send = _featureHost!.SendAsync(command,
                () => token.IsCancellationRequested || Volatile.Read(ref _featureEscapeVersion) != escapeVersion,
                async () =>
                {
                    var selected = await window!.Selection.WaitAsync(token);
                    CaptureTimingDiagnostics.Mark("resident-selection-result", $"selection={id} cancelled={selected is null}");
                    if (selected is null) return new("command") { Command = "cancel" };
                    if (selected.Color is { } color)
                    {
                        await ClipboardTextService.SetTextAsync(color, token);
                        window.Hide();
                        // Render feedback in the disposable UI process. Only
                        // the color travels; the desktop DIB is released below.
                        return new("command") { Command = "color-copied", Text = color };
                    }
                    token.ThrowIfCancellationRequested();
                    transferred = true;
                    // Recording needs coordinates, not a second full-screen image.
                    return command with { NativeFrame = framePrepared || command.HotKey is HotKeyAction.VideoRecording or HotKeyAction.RealtimeTranslation ? null : frame,
                        InitialSelection = selected.Region, NativeSelectionId = id,
                        NativeSelectionWindow = window.Handle.ToInt64() };
                }, prepareCaptureToolbar: () => (command.HotKey is HotKeyAction.RegionCapture or HotKeyAction.VideoRecording) && !window!.Selection.IsCompleted,
                activationReady: selectionReady.Task,
                prepareCaptureFrame: () =>
                {
                    if (window!.Selection.IsCompleted) return null;
                    if (command.HotKey != HotKeyAction.RegionCapture) return null;
                    framePrepared = true;
                    return new("prepare-capture-frame") { NativeFrame = frame, NativeSelectionId = id };
                });
            frame = await NativeDesktopFrame.CaptureAsync(VirtualScreen.GetBounds(), token);
            CaptureTimingDiagnostics.Mark("resident-frame-ready", $"selection={id} width={frame.Region.Width} height={frame.Region.Height}");
            token.ThrowIfCancellationRequested();
            var selectionSettings = command.HotKey == HotKeyAction.RegionCapture ? _currentSettings
                : _currentSettings with { CaptureAspectRatio = CaptureAspectRatio.Free };
            window = new ResidentSelectionWindow(frame, selectionSettings, cancellation.Cancel,
                allowColorPicker: command.HotKey != HotKeyAction.RealtimeTranslation);
            _residentSelectionWindow = window;
            await window.ShowAsync(token);
            CaptureTimingDiagnostics.Mark("resident-selection-visible", $"selection={id} window={window.Handle.ToInt64()}");
            // Bootstrap transfers the current settings snapshot, so a disk flush
            // is not a dependency of displaying this capture's toolbar.
            selectionReady.TrySetResult();
            await send;
            if (transferred)
                await _residentSelectionPresented.Task.WaitAsync(TimeSpan.FromSeconds(8), token);
            CaptureTimingDiagnostics.Mark("resident-handoff-finished", $"selection={id} transferred={transferred}");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally
        {
            selectionReady.TrySetCanceled();
            // Observe failed startup too, and keep its DIB alive until sending
            // has ended. A cancelled frame never leaves a startup waiter behind.
            if (send is not null)
            {
                try { await send; } catch { }
            }
            var failedHandoff = transferred && _residentSelectionPresented?.Task.IsCompletedSuccessfully != true;
            window?.Dispose();
            frame?.Dispose();
            _residentSelectionWindow = null;
            _residentSelectionCancellation = null;
            _residentSelectionPresented = null;
            if (!transferred || failedHandoff || token.IsCancellationRequested)
            {
                _isCaptureInProgress = false;
                _idleEfficiency?.SetCaptureActive(false);
                _floatingCaptureWindow?.SetCaptureInProgress(false);
            }
            MemoryFootprint.SetInteractionActive(false);
            MemoryFootprint.TrimAfterHeavyOperation();
            if (failedHandoff && !_isShuttingDown)
            {
                try
                {
                    await _featureHost!.SendAsync(new("command") { Command = "cancel-native", NativeSelectionId = id },
                        startIfNeeded: false);
                }
                catch (Exception exception) when (exception is System.IO.IOException or OperationCanceledException or ObjectDisposedException) { }
            }
        }
    }
}
