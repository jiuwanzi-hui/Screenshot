using Screenshot.App.Capture;

namespace Screenshot.App.Tests;

public sealed class CaptureInputActivationTests
{
    [Fact]
    public async Task ClassicMenuReleasesInputBeforeActivationIsRetried()
    {
        var activation = new Probe { MenuActive = true };
        Assert.Equal(CaptureInputActivation.Result.Focused, await CaptureInputActivation.AcquireAsync(activation, CancellationToken.None));
        Assert.Equal(1, activation.Cancellations);
        Assert.Equal(1, activation.Activations);
        Assert.Equal(new IntPtr(10), activation.CancelledMenu?.Owner);
    }

    [Fact]
    public async Task OrdinaryWindowAndAlreadyOwnedInputNeverReceiveCancelMode()
    {
        foreach (var owned in new[] { false, true })
        {
            var activation = new Probe { OwnsForeground = owned };
            Assert.Equal(CaptureInputActivation.Result.Focused, await CaptureInputActivation.AcquireAsync(activation, CancellationToken.None));
            Assert.Equal(0, activation.Cancellations);
            Assert.Equal(owned ? 0 : 1, activation.Activations);
            Assert.Equal(1, activation.Raises);
        }
    }

    [Fact]
    public async Task UserChangingForegroundStopsTheHandoff()
    {
        var activation = new Probe { MenuActive = true, SameForeground = false };
        Assert.Equal(CaptureInputActivation.Result.Interrupted, await CaptureInputActivation.AcquireAsync(activation, CancellationToken.None));
        Assert.Equal(0, activation.Activations);
    }

    [Fact]
    public async Task CancellationDoesNotActivateOrDismissAMenu()
    {
        var activation = new Probe { MenuActive = true };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CaptureInputActivation.AcquireAsync(activation, new CancellationToken(true)));
        Assert.Equal(0, activation.Cancellations);
        Assert.Equal(0, activation.Activations);
    }

    [Fact]
    public async Task UnresponsiveMenuHasABoundedWait()
    {
        var activation = new Probe { MenuActive = true, IgnoreCancel = true };
        Assert.Equal(CaptureInputActivation.Result.Failed, await CaptureInputActivation.AcquireAsync(activation, CancellationToken.None));
        Assert.Equal(1, activation.Cancellations);
        Assert.Equal(0, activation.Activations);
    }

    [Fact]
    public async Task ActivationDeniedButVisibleSurfaceCanSelectImmediately()
    {
        var activation = new Probe { DenyAttempts = 10, PointerReady = true };
        Assert.Equal(CaptureInputActivation.Result.PointerReady,
            await CaptureInputActivation.AcquireAsync(activation, CancellationToken.None));
        Assert.Equal(1, activation.Activations);
        Assert.Equal(0, activation.Cancellations);
    }

    [Fact]
    public async Task ATemporaryFocusDenialRetriesWithoutCancellingTheScreenshot()
    {
        var activation = new Probe { DenyAttempts = 1 };
        Assert.Equal(CaptureInputActivation.Result.Focused,
            await CaptureInputActivation.AcquireAsync(activation, CancellationToken.None));
        Assert.Equal(2, activation.Activations);
        Assert.Equal(2, activation.Raises);
    }

    [Fact]
    public async Task CoveredWindowWithoutFocusDoesNotPretendToBeInteractive()
    {
        var activation = new Probe { DenyAttempts = 10 };
        Assert.Equal(CaptureInputActivation.Result.Failed,
            await CaptureInputActivation.AcquireAsync(activation, CancellationToken.None));
        Assert.Equal(2, activation.Activations);
    }

    private sealed class Probe : CaptureInputActivation.IActivation
    {
        public bool OwnsForeground { get; set; }
        public IntPtr Foreground => new(1);
        public bool PointerReady { get; set; }
        public int DenyAttempts { get; set; }
        public int Raises { get; private set; }
        public void RaiseSurface() => Raises++;
        public bool MenuActive { get; set; }
        public bool IgnoreCancel { get; set; }
        public bool SameForeground { get; set; } = true;
        public int Cancellations { get; private set; }
        public int Activations { get; private set; }
        public CaptureInputActivation.Menu? CancelledMenu { get; private set; }
        public CaptureInputActivation.Menu? GetForegroundMenu() => MenuActive ? new(new(1), new(10), new(11)) : null;
        public bool IsSameForeground(IntPtr foreground) => SameForeground;
        public void CancelMenu(CaptureInputActivation.Menu menu)
        {
            Cancellations++;
            CancelledMenu = menu;
            if (!IgnoreCancel) MenuActive = false;
        }
        public bool TryActivate()
        {
            Activations++;
            return OwnsForeground = Activations > DenyAttempts;
        }
    }
}
