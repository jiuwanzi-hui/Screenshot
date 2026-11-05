using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using Screenshot.App.Core;

namespace Screenshot.App.Text;

internal static class OcrFailureDetails
{
    internal const string CleanupErrorKey = "SnapCut.OcrInitializationCleanupError";

    internal static string Describe(Exception exception)
    {
        var cause = exception.GetBaseException();
        var advice = cause switch
        {
            DllNotFoundException or EntryPointNotFoundException or BadImageFormatException =>
                "本机推理运行库或其依赖无法加载，请检查安装文件及系统兼容性。",
            OutOfMemoryException => "可用内存不足，请关闭占用内存较多的程序后重试。",
            FileNotFoundException or DirectoryNotFoundException => "模型或依赖文件不存在，请检查模型安装。",
            UnauthorizedAccessException => "模型或运行库文件无法访问，请检查文件权限。",
            _ => "请根据下方原始错误检查模型或运行环境。",
        };
        // Keep the full exception and stack in the diagnostic file, with a
        // bounded, single-line original reason in the operation's UI.
        var detail = cause.Message.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (detail.Length > 300) detail = detail[..300] + "…";
        return $"{advice}（{cause.GetType().Name}: {detail}）";
    }

    internal static string FormatException(Exception exception) =>
        exception + (exception.Data[CleanupErrorKey] is string cleanup
            ? $"{Environment.NewLine}Secondary cleanup failure (original error above):{Environment.NewLine}{cleanup}"
            : string.Empty);

    internal static string EnvironmentSummary() =>
        $"SnapCut={AppMetadata.DisplayVersion}; OS={RuntimeInformation.OSDescription}; " +
        $"process={RuntimeInformation.ProcessArchitecture}; runtime={RuntimeInformation.FrameworkDescription}; " +
        $"logical CPUs={Environment.ProcessorCount}; OCR threads={HeavyWorkloadBudget.OcrThreadCount}; " +
        $"SSE2={Sse2.IsSupported}; AVX={Avx.IsSupported}; AVX2={Avx2.IsSupported}; " +
        $"memory available to GC={GC.GetGCMemoryInfo().TotalAvailableMemoryBytes}";
}
