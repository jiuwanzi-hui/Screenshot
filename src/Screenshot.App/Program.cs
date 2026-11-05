using System.Runtime.CompilerServices;
using Screenshot.App.Text;
using Screenshot.App.Infrastructure;

namespace Screenshot.App;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Updaters only need IO and archive code. Do not create WPF, load
        // settings from the helper's directory or start application services.
        if (Update.PortableUpdateRunner.IsUpdateRequest(args))
            return Update.PortableUpdateRunner.Run(args);
        // Enter the worker before creating Application, loading XAML, registering
        // hotkeys or enforcing the interactive application's single instance.
        if (args.Length > 0 && args[0] == OcrWorkerProcess.WorkerArgument)
            return OcrWorkerProcess.RunWorkerAsync(args).GetAwaiter().GetResult();
        Capture.CaptureTimingDiagnostics.Mark("process-entry",
            $"role={(args.Length > 0 && args[0] == FeatureProcessHost.WorkerArgument ? "worker" : "resident")} " +
            $"version={Core.AppMetadata.DisplayVersion} build={typeof(Program).Module.ModuleVersionId} " +
            $"qpcFrequency={System.Diagnostics.Stopwatch.Frequency} cores={Environment.ProcessorCount}");
        FeatureStartupCompilation.Start(args);
        return RunApplication(args);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int RunApplication(string[] args)
    {
        var workerPipe = args.Length >= 2 && args[0] == FeatureProcessHost.WorkerArgument
            ? args[1] : null;
        Capture.CaptureTimingDiagnostics.Mark("before-application-create");
        var application = new App(useResidentHost: workerPipe is null, workerPipe);
        application.InitializeComponent();
        Capture.CaptureTimingDiagnostics.Mark("application-xaml-ready");
        return application.Run();
    }
}
