using System.Windows.Threading;

namespace Screenshot.App.Capture;

internal static class CaptureStartupDispatcher
{
    // Yield out of the hotkey/native message, then join the input queue.
    // Background/ContextIdle can wait indefinitely behind a stream of input,
    // especially after an idle wake-up on a busy or slower computer.
    internal static DispatcherOperation Queue(Dispatcher dispatcher, Action action) =>
        dispatcher.BeginInvoke(DispatcherPriority.Input, action);
}
