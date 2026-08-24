namespace Screenshot.App.Capture;

public enum ControlledScrollCaptureState
{
    WaitingToStart,
    ScrollingDown,
    PreparingPauseDown,
    PausedDown,
    BottomReached,
    PreparingReturnFromDown,
    ReturningToStart,
    PausedReturning,
    AligningUpwardStart,
    ScrollingUp,
    PreparingPauseUp,
    PausedUp,
    ScrollingUpFirst,
    PreparingPauseUpFirst,
    PausedUpFirst,
    TopReached,
    PreparingReturnFromUp,
    ReturningDownToStart,
    PausedReturningDown,
    AligningDownwardStart,
    ScrollingDownSecond,
    PreparingPauseDownSecond,
    PausedDownSecond,
    FinalTopReached,
    FinalBottomReached,
    InputUnavailable,
    Completing,
}

public enum ScrollCapturePointerAction
{
    Click,
    DoubleClick,
}
