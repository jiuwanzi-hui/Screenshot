using Screenshot.App.Capture;

namespace Screenshot.App.Tests;

[Collection(GlobalInputTestGroup.Name)]
public sealed class ControlledScrollCaptureStateTests
{
    [Fact]
    public void SingleClickStartsAndTogglesTheActiveDirection()
    {
        var state = Apply(
            ControlledScrollCaptureState.WaitingToStart,
            ScrollCapturePointerAction.Click);
        Assert.Equal(ControlledScrollCaptureState.ScrollingDown, state);

        state = Apply(state, ScrollCapturePointerAction.Click);
        Assert.Equal(ControlledScrollCaptureState.PreparingPauseDown, state);

        state = ScrollCaptureService.GetControlledSettledState(state);
        Assert.Equal(ControlledScrollCaptureState.PausedDown, state);

        state = Apply(state, ScrollCapturePointerAction.Click);
        Assert.Equal(ControlledScrollCaptureState.ScrollingDown, state);

        state = Apply(state, ScrollCapturePointerAction.DoubleClick);
        Assert.Equal(
            ControlledScrollCaptureState.PreparingReturnFromDown,
            state);
    }

    [Fact]
    public void DoubleClickStartsUpwardAndCanReturnForTheDownwardSecondLeg()
    {
        var state = Apply(
            ControlledScrollCaptureState.WaitingToStart,
            ScrollCapturePointerAction.DoubleClick);
        Assert.Equal(ControlledScrollCaptureState.ScrollingUpFirst, state);

        state = Apply(state, ScrollCapturePointerAction.Click);
        Assert.Equal(
            ControlledScrollCaptureState.PreparingPauseUpFirst,
            state);

        state = ScrollCaptureService.GetControlledSettledState(state);
        Assert.Equal(ControlledScrollCaptureState.PausedUpFirst, state);

        state = Apply(state, ScrollCapturePointerAction.DoubleClick);
        Assert.Equal(ControlledScrollCaptureState.ReturningDownToStart, state);
    }

    [Fact]
    public void InputFailureRemainsInTheCaptureUiForExplicitCompletion()
    {
        var state = ControlledScrollCaptureState.InputUnavailable;

        Assert.Equal(state, Apply(state, ScrollCapturePointerAction.Click));
        Assert.Equal(
            state,
            Apply(state, ScrollCapturePointerAction.DoubleClick));
        Assert.Null(ScrollCaptureService.GetControlledCaptureDirection(state));
        Assert.Null(ScrollCaptureService.GetControlledReturnDirection(state));
    }

    [Theory]
    [InlineData(ControlledScrollCaptureState.ScrollingUp)]
    [InlineData(ControlledScrollCaptureState.ScrollingDownSecond)]
    [InlineData(ControlledScrollCaptureState.FinalTopReached)]
    [InlineData(ControlledScrollCaptureState.FinalBottomReached)]
    [InlineData(ControlledScrollCaptureState.Completing)]
    public void SecondLegCannotReverseAgain(
        ControlledScrollCaptureState state)
    {
        Assert.Equal(
            state,
            Apply(state, ScrollCapturePointerAction.DoubleClick));
    }

    [Theory]
    [InlineData(
        ControlledScrollCaptureState.PreparingPauseUp,
        ControlledScrollCaptureState.ScrollingUp)]
    [InlineData(
        ControlledScrollCaptureState.PausedUp,
        ControlledScrollCaptureState.ScrollingUp)]
    [InlineData(
        ControlledScrollCaptureState.PreparingPauseDownSecond,
        ControlledScrollCaptureState.ScrollingDownSecond)]
    [InlineData(
        ControlledScrollCaptureState.PausedDownSecond,
        ControlledScrollCaptureState.ScrollingDownSecond)]
    public void DoubleClickDuringTheFinalLegResumesTheSameDirection(
        ControlledScrollCaptureState state,
        ControlledScrollCaptureState expected)
    {
        Assert.Equal(
            expected,
            Apply(state, ScrollCapturePointerAction.DoubleClick));
    }

    [Theory]
    [InlineData(
        ControlledScrollCaptureState.PreparingPauseDown,
        ControlledScrollCaptureState.ScrollingDown)]
    [InlineData(
        ControlledScrollCaptureState.PreparingPauseUp,
        ControlledScrollCaptureState.ScrollingUp)]
    [InlineData(
        ControlledScrollCaptureState.PreparingPauseUpFirst,
        ControlledScrollCaptureState.ScrollingUpFirst)]
    [InlineData(
        ControlledScrollCaptureState.PreparingPauseDownSecond,
        ControlledScrollCaptureState.ScrollingDownSecond)]
    public void ASecondClickCancelsAPendingPause(
        ControlledScrollCaptureState state,
        ControlledScrollCaptureState expected)
    {
        Assert.Equal(expected, Apply(state, ScrollCapturePointerAction.Click));
    }

    [Theory]
    [InlineData(ControlledScrollCaptureState.ScrollingDown)]
    [InlineData(ControlledScrollCaptureState.PausedDown)]
    [InlineData(ControlledScrollCaptureState.BottomReached)]
    public void DoubleClickReversesEveryDownwardState(
        ControlledScrollCaptureState state)
    {
        var expected = state == ControlledScrollCaptureState.ScrollingDown
            ? ControlledScrollCaptureState.PreparingReturnFromDown
            : ControlledScrollCaptureState.ReturningToStart;
        Assert.Equal(expected, Apply(state, ScrollCapturePointerAction.DoubleClick));
    }

    [Theory]
    [InlineData(
        ControlledScrollCaptureState.PausedDown,
        ControlledScrollCaptureState.ScrollingDown,
        true)]
    [InlineData(
        ControlledScrollCaptureState.PausedReturning,
        ControlledScrollCaptureState.ReturningToStart,
        true)]
    [InlineData(
        ControlledScrollCaptureState.PausedUp,
        ControlledScrollCaptureState.ScrollingUp,
        true)]
    [InlineData(
        ControlledScrollCaptureState.WaitingToStart,
        ControlledScrollCaptureState.ScrollingDown,
        false)]
    [InlineData(
        ControlledScrollCaptureState.ScrollingDown,
        ControlledScrollCaptureState.PreparingPauseDown,
        false)]
    [InlineData(
        ControlledScrollCaptureState.PausedUpFirst,
        ControlledScrollCaptureState.ScrollingUpFirst,
        true)]
    [InlineData(
        ControlledScrollCaptureState.PausedReturningDown,
        ControlledScrollCaptureState.ReturningDownToStart,
        true)]
    [InlineData(
        ControlledScrollCaptureState.PausedDownSecond,
        ControlledScrollCaptureState.ScrollingDownSecond,
        true)]
    public void OnlyResumingAPausedLegRequiresReanchoring(
        ControlledScrollCaptureState previousState,
        ControlledScrollCaptureState currentState,
        bool expected)
    {
        Assert.Equal(
            expected,
            ScrollCaptureService.IsControlledResumeTransition(
                previousState,
                currentState));
    }

    [Fact]
    public void AddedUpwardRowsNeverCountAsAStationaryTopSample()
    {
        Assert.False(ScrollCaptureService.IsControlledBoundarySample(
            beganUpwardExtension: false,
            added: true,
            fingerprintStationary: true,
            movementRows: 165,
            rejectReason: null));
    }

    [Theory]
    [InlineData(false, true, 0, null, true)]
    [InlineData(false, true, null, "no-candidate", false)]
    [InlineData(false, false, 0, null, false)]
    [InlineData(true, true, 0, null, false)]
    public void TopSampleRequiresConfirmedZeroMovement(
        bool beganUpwardExtension,
        bool fingerprintStationary,
        int? movementRows,
        string? rejectReason,
        bool expected)
    {
        Assert.Equal(
            expected,
            ScrollCaptureService.IsControlledBoundarySample(
                beganUpwardExtension,
                added: false,
                fingerprintStationary,
                movementRows,
                rejectReason));
    }

    [Theory]
    [InlineData(true, null, "no-candidate", true)]
    [InlineData(false, 0, null, true)]
    [InlineData(false, null, "below-minimum", true)]
    [InlineData(false, null, "no-candidate", false)]
    public void UnlocatedFrameStopsTheNextWheelStep(
        bool added,
        int? movementRows,
        string? rejectReason,
        bool expected)
    {
        Assert.Equal(
            expected,
            ScrollCaptureService.IsControlledFrameLocated(
                added,
                movementRows,
                rejectReason));
    }

    [Theory]
    [InlineData(0, 2)]
    [InlineData(20, 2)]
    [InlineData(100, 52)]
    [InlineData(800, 752)]
    public void ContinuousReturnUsesPixelSizedEvidenceNearTheInitialViewport(
        long outboundPixels,
        long expectedMinimum)
    {
        Assert.Equal(
            expectedMinimum,
            ScrollCaptureService.GetControlledMinimumReturnMagnitude(
                outboundPixels));
    }

    [Theory]
    [InlineData(
        ControlledScrollCaptureState.ScrollingDown,
        ControlledScrollCaptureState.PreparingReturnFromDown,
        true)]
    [InlineData(
        ControlledScrollCaptureState.PausedDown,
        ControlledScrollCaptureState.ReturningToStart,
        true)]
    [InlineData(
        ControlledScrollCaptureState.BottomReached,
        ControlledScrollCaptureState.ReturningToStart,
        true)]
    [InlineData(
        ControlledScrollCaptureState.PausedReturning,
        ControlledScrollCaptureState.ReturningToStart,
        false)]
    [InlineData(
        ControlledScrollCaptureState.ScrollingUpFirst,
        ControlledScrollCaptureState.PreparingReturnFromUp,
        true)]
    [InlineData(
        ControlledScrollCaptureState.PausedUpFirst,
        ControlledScrollCaptureState.ReturningDownToStart,
        true)]
    [InlineData(
        ControlledScrollCaptureState.TopReached,
        ControlledScrollCaptureState.ReturningDownToStart,
        true)]
    [InlineData(
        ControlledScrollCaptureState.PausedReturningDown,
        ControlledScrollCaptureState.ReturningDownToStart,
        false)]
    public void ReturnDistanceIsResetOnlyForANewReturnJourney(
        ControlledScrollCaptureState previousState,
        ControlledScrollCaptureState currentState,
        bool expected)
    {
        Assert.Equal(
            expected,
            ScrollCaptureService.BeginsControlledReturnJourney(
                previousState,
                currentState));
    }

    [Theory]
    [InlineData(400, 0, null)]
    [InlineData(400, 3, 3)]
    [InlineData(400, 4, 4)]
    [InlineData(400, 7, 7)]
    [InlineData(400, 500, 380)]
    public void ControlledInputUsesActualTravelAsTheStitchExpectation(
        int frameHeight,
        long inputTravelUnits,
        int? expected)
    {
        Assert.Equal(
            expected,
            ScrollCaptureService.GetControlledExpectedInputRows(
                frameHeight,
                inputTravelUnits));
    }

    [Theory]
    [InlineData(
        ControlledScrollCaptureState.PreparingPauseDown,
        ScrollCaptureDirection.Down,
        ControlledScrollCaptureState.PausedDown)]
    [InlineData(
        ControlledScrollCaptureState.PreparingReturnFromDown,
        ScrollCaptureDirection.Down,
        ControlledScrollCaptureState.ReturningToStart)]
    [InlineData(
        ControlledScrollCaptureState.PreparingPauseUp,
        ScrollCaptureDirection.Up,
        ControlledScrollCaptureState.PausedUp)]
    [InlineData(
        ControlledScrollCaptureState.PreparingPauseUpFirst,
        ScrollCaptureDirection.Up,
        ControlledScrollCaptureState.PausedUpFirst)]
    [InlineData(
        ControlledScrollCaptureState.PreparingReturnFromUp,
        ScrollCaptureDirection.Up,
        ControlledScrollCaptureState.ReturningDownToStart)]
    [InlineData(
        ControlledScrollCaptureState.PreparingPauseDownSecond,
        ScrollCaptureDirection.Down,
        ControlledScrollCaptureState.PausedDownSecond)]
    [InlineData(
        ControlledScrollCaptureState.AligningUpwardStart,
        ScrollCaptureDirection.Up,
        ControlledScrollCaptureState.ScrollingUp)]
    [InlineData(
        ControlledScrollCaptureState.AligningDownwardStart,
        ScrollCaptureDirection.Down,
        ControlledScrollCaptureState.ScrollingDownSecond)]
    public void PauseAndReturnBothSettleTheTailBeforeChangingState(
        ControlledScrollCaptureState preparingState,
        ScrollCaptureDirection expectedDirection,
        ControlledScrollCaptureState expectedSettledState)
    {
        Assert.True(ScrollCaptureService.IsControlledSettleState(
            preparingState));
        Assert.Equal(
            expectedDirection,
            ScrollCaptureService.GetControlledSettleDirection(
                preparingState));
        Assert.Equal(
            expectedSettledState,
            ScrollCaptureService.GetControlledSettledState(
                preparingState));
    }

    [Theory]
    [InlineData(false, false, 0, false)]
    [InlineData(false, true, 64, false)]
    [InlineData(true, false, 64, false)]
    [InlineData(true, true, 63, false)]
    [InlineData(true, true, 64, true)]
    public void BoundaryNeedsVisibleMovement(
        bool legHasVisibleMovement,
        bool inputAdvancedSincePreviousSample,
        long inputTravelSinceVisibleMovement,
        bool expected)
    {
        Assert.Equal(
            expected,
            ScrollCaptureService.CanConfirmControlledBoundary(
                legHasVisibleMovement,
                inputAdvancedSincePreviousSample,
                inputTravelSinceVisibleMovement));
    }

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(false, false, false)]
    [InlineData(true, true, false)]
    public void ReturnIsSkippedOnlyWhenTheFirstLegNeverLeftTheInitialViewport(
        bool outboundHadVisibleMovement,
        bool isInitialViewport,
        bool expected)
    {
        Assert.Equal(
            expected,
            ScrollCaptureService.ShouldSkipControlledReturn(
                outboundHadVisibleMovement,
                isInitialViewport));
    }

    [Theory]
    [InlineData(ControlledScrollCaptureState.ScrollingDown, ScrollCaptureDirection.Down)]
    [InlineData(ControlledScrollCaptureState.ScrollingDownSecond, ScrollCaptureDirection.Down)]
    [InlineData(ControlledScrollCaptureState.ScrollingUp, ScrollCaptureDirection.Up)]
    [InlineData(ControlledScrollCaptureState.ScrollingUpFirst, ScrollCaptureDirection.Up)]
    public void ActiveCaptureStatesHaveSymmetricDirections(
        ControlledScrollCaptureState state,
        ScrollCaptureDirection expected)
    {
        Assert.Equal(
            expected,
            ScrollCaptureService.GetControlledCaptureDirection(state));
    }

    [Theory]
    [InlineData(ControlledScrollCaptureState.ReturningToStart, ScrollCaptureDirection.Up)]
    [InlineData(ControlledScrollCaptureState.ReturningDownToStart, ScrollCaptureDirection.Down)]
    public void ReturnStatesMoveTowardTheInitialViewport(
        ControlledScrollCaptureState state,
        ScrollCaptureDirection expected)
    {
        Assert.Equal(
            expected,
            ScrollCaptureService.GetControlledReturnDirection(state));
    }

    [Fact]
    public void ContinuousDriverUsesFineGrainedFixedWheelCadence()
    {
        Assert.Equal(20, ControlledScrollDriver.TickIntervalMilliseconds);
        Assert.Equal(5, ControlledScrollDriver.CapturePixelsPerTick);
        Assert.Equal(
            250,
            (1000 * ControlledScrollDriver.CapturePixelsPerTick) /
            ControlledScrollDriver.TickIntervalMilliseconds);
        Assert.Equal(0, ControlledScrollDriver.PresentationSettleMilliseconds);
    }

    private static ControlledScrollCaptureState Apply(
        ControlledScrollCaptureState state,
        ScrollCapturePointerAction action)
    {
        return ScrollCaptureService.ApplyControlledPointerAction(state, action);
    }
}
