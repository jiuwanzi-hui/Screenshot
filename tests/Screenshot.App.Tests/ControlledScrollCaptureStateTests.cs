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

    [Theory]
    [InlineData(
        ControlledScrollCaptureState.PreparingPauseDown,
        ControlledScrollCaptureState.PreparingReturnFromDown)]
    [InlineData(
        ControlledScrollCaptureState.PreparingPauseUpFirst,
        ControlledScrollCaptureState.PreparingReturnFromUp)]
    public void ImmediateFirstClickDoesNotPreventDoubleClickReversal(
        ControlledScrollCaptureState state,
        ControlledScrollCaptureState expected)
    {
        Assert.Equal(
            expected,
            Apply(state, ScrollCapturePointerAction.DoubleClick));
    }

    [Theory]
    [InlineData(ControlledScrollCaptureState.WaitingToStart, true)]
    [InlineData(ControlledScrollCaptureState.PausedDown, true)]
    [InlineData(ControlledScrollCaptureState.PausedUp, true)]
    [InlineData(ControlledScrollCaptureState.BottomReached, true)]
    [InlineData(ControlledScrollCaptureState.TopReached, true)]
    [InlineData(ControlledScrollCaptureState.ScrollingDown, false)]
    [InlineData(ControlledScrollCaptureState.ScrollingUp, false)]
    [InlineData(ControlledScrollCaptureState.ScrollingUpFirst, false)]
    [InlineData(ControlledScrollCaptureState.ScrollingDownSecond, false)]
    [InlineData(ControlledScrollCaptureState.ReturningToStart, false)]
    [InlineData(ControlledScrollCaptureState.ReturningDownToStart, false)]
    [InlineData(ControlledScrollCaptureState.PreparingPauseDown, false)]
    [InlineData(ControlledScrollCaptureState.PreparingPauseUp, false)]
    public void ClickDeferralOnlyAppliesToIdleStates(
        ControlledScrollCaptureState state,
        bool expectedDeferral)
    {
        // Motion states deliver the click immediately so pausing feels
        // instant; idle states keep the double-click disambiguation window
        // because there a click and a double-click start different motions.
        Assert.Equal(
            expectedDeferral,
            ScrollCaptureService.ShouldDeferControlledPointerClicks(state));
    }

    [Theory]
    [InlineData(62, 62, 1.0, true)]
    [InlineData(62, 86, 1.0, true)]
    [InlineData(62, 90, 0.98, false)]
    [InlineData(62, 100, 1.0, false)]
    public void StableInitialCrossingCanConfirmWithoutFurtherWheelTravel(
        int previousRows,
        int currentRows,
        double confidence,
        bool expected)
    {
        Assert.Equal(
            expected,
            ScrollCaptureService.IsControlledInitialCrossingStable(
                previousRows,
                1170,
                currentRows,
                1170,
                confidence));
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
    [InlineData(true, 82, true)]
    [InlineData(true, 0, false)]
    [InlineData(true, null, false)]
    [InlineData(false, 165, false)]
    public void InputDistanceIsCommittedOnlyAfterVisibleMovement(
        bool frameLocated,
        int? movementRows,
        bool expected)
    {
        Assert.Equal(
            expected,
            ScrollCaptureService.ShouldAdvanceControlledInputAnchor(
                frameLocated,
                movementRows));
    }

    [Theory]
    [InlineData("movement-cap-veto", 311, 15, 24, 155)]
    [InlineData("movement-cap-veto", 426, 55, 24, 213)]
    [InlineData("no-candidate", 311, 15, 24, null)]
    [InlineData(null, 311, 15, 24, null)]
    public void OnlyMovementCapRetriesReceiveABoundedReanchorAllowance(
        string? rejectReason,
        int frameHeight,
        int? expectedRows,
        int minimumOverlapRows,
        int? expected)
    {
        Assert.Equal(
            expected,
            ScrollCaptureService.GetControlledRetryMaximumMovementRows(
                rejectReason,
                frameHeight,
                expectedRows,
                minimumOverlapRows));
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
    [InlineData(440, 350, 837, 215)]
    [InlineData(350, 350, 837, 0)]
    [InlineData(440, 350, 0, 90)]
    public void InitialCrossingUsesImageCalibratedInputScale(
        long returnInput,
        long outboundInput,
        int outboundVisualRows,
        long expectedRows)
    {
        Assert.Equal(
            expectedRows,
            ScrollCaptureService.GetControlledExpectedCrossingRows(
                returnInput,
                outboundInput,
                outboundVisualRows));
    }

    [Theory]
    [InlineData(65, 125, 90, 150, true)]
    [InlineData(65, 125, 65, 150, false)]
    [InlineData(65, 125, 160, 150, false)]
    [InlineData(90, 150, 65, 175, false)]
    public void InitialCrossingNeedsASecondConsistentFrame(
        int previousMovementRows,
        long previousInputMagnitude,
        int currentMovementRows,
        long currentInputMagnitude,
        bool expected)
    {
        Assert.Equal(
            expected,
            ScrollCaptureService.IsControlledInitialCrossingConsistent(
                previousMovementRows,
                previousInputMagnitude,
                currentMovementRows,
                currentInputMagnitude));
    }

    [Theory]
    [InlineData(423, 30, 20, 211)]
    [InlineData(423, null, 20, 211)]
    [InlineData(423, 200, 20, 403)]
    public void ResumeAnchorCannotCommitAnUnmeasuredNearViewportJump(
        int frameHeight,
        int? expectedRows,
        int minimumOverlapRows,
        int expectedMaximum)
    {
        Assert.Equal(
            expectedMaximum,
            ScrollCaptureService.GetControlledResumeMaximumMovementRows(
                frameHeight,
                expectedRows,
                minimumOverlapRows));
    }

    [Theory]
    [InlineData(314, 20, 294)]
    [InlineData(423, 20, 403)]
    public void PauseSettleAllowsFullMatchableInertia(
        int frameHeight,
        int minimumOverlapRows,
        int expectedMaximum)
    {
        Assert.Equal(
            expectedMaximum,
            ScrollCaptureService.GetControlledSettleMaximumMovementRows(
                frameHeight,
                minimumOverlapRows));
        Assert.True(
            expectedMaximum >
            ScrollCaptureService.GetControlledResumeMaximumMovementRows(
                frameHeight,
                expectedRows: null,
                minimumOverlapRows));
    }

    [Theory]
    [InlineData(175, 1.000, 220, 1.000, true)]
    [InlineData(82, 0.983, 175, 1.000, true)]
    [InlineData(220, 1.000, 175, 1.000, false)]
    [InlineData(175, 0.900, 220, 1.000, false)]
    [InlineData(175, 1.000, 220, 0.950, false)]
    public void ConsecutiveDecisiveOverlapsConfirmAnInertiaGlideCrossing(
        int previousRows,
        double previousConfidence,
        int currentRows,
        double currentConfidence,
        bool expected)
    {
        Assert.Equal(
            expected,
            ScrollCaptureService.IsControlledInitialCrossingGlide(
                previousRows,
                previousConfidence,
                currentRows,
                currentConfidence));
    }

    [Fact]
    public void AligningSettledStateStartsTheCaptureLeg()
    {
        Assert.Equal(
            ControlledScrollCaptureState.ScrollingUp,
            ScrollCaptureService.GetControlledSettledState(
                ControlledScrollCaptureState.AligningUpwardStart));
        Assert.Equal(
            ControlledScrollCaptureState.ScrollingDownSecond,
            ScrollCaptureService.GetControlledSettledState(
                ControlledScrollCaptureState.AligningDownwardStart));
    }

    [Theory]
    [InlineData(307, null, 20, 102)]
    [InlineData(307, 80, 20, 104)]
    [InlineData(307, 165, 20, 189)]
    [InlineData(307, 400, 20, 287)]
    public void InitialAlignmentHonorsTheConfirmedCrossingWithoutLosingOverlap(
        int frameHeight,
        int? confirmedCrossingRows,
        int minimumOverlapRows,
        int expectedMaximum)
    {
        Assert.Equal(
            expectedMaximum,
            ScrollCaptureService.GetControlledInitialAlignmentMaximumMovementRows(
                frameHeight,
                confirmedCrossingRows,
                minimumOverlapRows));
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
    [InlineData(true, false, 0, true)]
    [InlineData(true, true, 0, true)]
    [InlineData(false, true, 0, false)]
    [InlineData(false, true, 1, false)]
    [InlineData(false, true, 2, true)]
    [InlineData(false, false, 4, false)]
    public void LooseInitialFingerprintNeedsAStablePhysicalBoundary(
        bool isStrictInitialViewport,
        bool isLooseInitialViewport,
        int stationarySamples,
        bool expected)
    {
        Assert.Equal(
            expected,
            ScrollCaptureService.IsControlledInitialViewportReached(
                isStrictInitialViewport,
                isLooseInitialViewport,
                stationarySamples));
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
        Assert.Equal(12, ControlledScrollDriver.MaximumCaptureStepsPerFrame);
        Assert.Equal(
            250,
            (1000 * ControlledScrollDriver.CapturePixelsPerTick) /
            ControlledScrollDriver.TickIntervalMilliseconds);
        Assert.Equal(0, ControlledScrollDriver.PresentationSettleMilliseconds);
    }

    [Fact]
    public void AutomaticAndManualModesUseIndependentAlgorithmTypes()
    {
        Assert.NotEqual(
            typeof(ControlledScrollDriver),
            typeof(ManualScrollDriver));
        Assert.NotEqual(
            typeof(AutomaticScrollCaptureComposerCore),
            typeof(ScrollCaptureComposer));
        Assert.NotEqual(
            typeof(AutomaticImageOverlapMatcher),
            typeof(ImageOverlapMatcher));
        Assert.NotEqual(
            typeof(AutomaticViewportFingerprint),
            typeof(ViewportFingerprint));

        var automaticComposerFieldTypes =
            typeof(ControlledScrollCaptureComposer)
                .GetFields(System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.NonPublic)
                .Select(field => field.FieldType)
                .ToArray();
        Assert.Contains(
            typeof(AutomaticScrollCaptureComposerCore),
            automaticComposerFieldTypes);
        Assert.DoesNotContain(
            typeof(ScrollCaptureComposer),
            automaticComposerFieldTypes);
    }

    [Fact]
    public async Task ManualDriverQueuesRapidWheelInputWithoutDroppingNotches()
    {
        await using var driver = CreateManualInputDriver();

        for (var index = 0; index < 10; index++)
        {
            driver.QueueCaptureInput(-120);
        }

        Assert.True(driver.HasPendingCaptureInput);
        Assert.Equal(ScrollCaptureDirection.Down, driver.PendingCaptureDirection);
        Assert.Equal(
            10 * ManualScrollDriver.MaximumCaptureStepsPerFrame,
            driver.PendingCaptureStepCount);
    }

    [Fact]
    public async Task ManualDriverReversalDiscardsOnlyUnexecutedOldDirection()
    {
        await using var driver = CreateManualInputDriver();
        driver.QueueCaptureInput(-360);

        driver.QueueCaptureInput(120);

        Assert.Equal(ScrollCaptureDirection.Up, driver.PendingCaptureDirection);
        Assert.Equal(
            ManualScrollDriver.MaximumCaptureStepsPerFrame,
            driver.PendingCaptureStepCount);
    }

    private static ManualScrollDriver CreateManualInputDriver()
    {
        return new ManualScrollDriver(
            new ScrollCaptureTarget(
                new IntPtr(1),
                new IntPtr(1),
                new ScreenRegion(0, 0, 100, 100),
                SupportsVerticalScroll: true));
    }

    private static ControlledScrollCaptureState Apply(
        ControlledScrollCaptureState state,
        ScrollCapturePointerAction action)
    {
        return ScrollCaptureService.ApplyControlledPointerAction(state, action);
    }
}
