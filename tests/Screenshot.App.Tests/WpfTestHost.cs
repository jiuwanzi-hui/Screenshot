using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace Screenshot.App.Tests;

internal static class WpfTestHost
{
    private static readonly Dispatcher Dispatcher = CreateDispatcher();

    public static void Invoke(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        Dispatcher.Invoke(action);
    }

    private static Dispatcher CreateDispatcher()
    {
        using var ready = new ManualResetEventSlim();
        Exception? initializationException = null;
        Dispatcher? dispatcher = null;
        var thread = new Thread(() =>
        {
            try
            {
                _ = new Application
                {
                    ShutdownMode = ShutdownMode.OnExplicitShutdown,
                };
                dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
            }
            catch (Exception exception)
            {
                initializationException = exception;
            }
            finally
            {
                ready.Set();
            }

            if (dispatcher is not null)
            {
                System.Windows.Threading.Dispatcher.Run();
            }
        })
        {
            IsBackground = true,
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        if (!ready.Wait(TimeSpan.FromSeconds(5)))
        {
            throw new TimeoutException("WPF 测试宿主启动超时。");
        }

        if (initializationException is not null)
        {
            throw new InvalidOperationException("无法启动 WPF 测试宿主。", initializationException);
        }

        return dispatcher
            ?? throw new InvalidOperationException("WPF 测试宿主未创建 Dispatcher。");
    }
}
