using Screenshot.App.Capture;

namespace Screenshot.App.Tests;

public sealed class CaptureInteractionScopeTests
{
    [Fact]
    public async Task RecordingHandOffAllowsScreenshotsBeforeRecordingFinishes()
    {
        var captureActive = false;
        var changes = new List<bool>();
        using var startup = new CaptureInteractionScope(active =>
        {
            captureActive = active;
            changes.Add(active);
        });
        var recordingFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(captureActive); // Selecting and initializing the recording.

        startup.Dispose(); // Recording controls are ready; the session continues.
        Assert.False(captureActive);
        Assert.False(recordingFinished.Task.IsCompleted);

        captureActive = true; // A new screenshot now owns the interaction.
        recordingFinished.SetResult();
        await recordingFinished.Task;
        startup.Dispose(); // Recording cleanup must not clear that screenshot.
        Assert.True(captureActive);
        Assert.Collection(changes, active => Assert.True(active), active => Assert.False(active));
    }

    [Fact]
    public void FailedRecordingInitializationReleasesCaptureInteraction()
    {
        var captureActive = false;
        Assert.Throws<InvalidOperationException>((Action)(() =>
        {
            using var startup = new CaptureInteractionScope(active => captureActive = active);
            Assert.True(captureActive);
            throw new InvalidOperationException("Recorder initialization failed.");
        }));
        Assert.False(captureActive);
    }
}
