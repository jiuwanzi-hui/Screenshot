using Panlingo.LanguageIdentification.CLD3;
using System.IO;
using System.Runtime.InteropServices;

namespace Screenshot.App.Text;

internal sealed record OfflineLanguageDetectionResult(
    string? LanguageCode,
    double Confidence,
    bool IsReliable,
    string? ErrorMessage = null)
{
    public bool IsSuccess => !string.IsNullOrWhiteSpace(LanguageCode);

    public static OfflineLanguageDetectionResult Failure(string message) =>
        new(null, 0, false, message);
}

internal interface IOfflineLanguageDetector
{
    OfflineLanguageDetectionResult Detect(string text);
}

internal sealed class Cld3OfflineLanguageDetector : IOfflineLanguageDetector
{
    private const int MaximumDetectionBytes = 4096;
    private static readonly Lazy<CLD3Detector> Detector = new(
        () => new CLD3Detector(0, MaximumDetectionBytes),
        LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly object DetectorLock = new();

    static Cld3OfflineLanguageDetector()
    {
        NativeLibrary.SetDllImportResolver(
            typeof(CLD3Detector).Assembly,
            ResolveNativeLibrary);
    }

    public static Cld3OfflineLanguageDetector Shared { get; } = new();

    public OfflineLanguageDetectionResult Detect(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return OfflineLanguageDetectionResult.Failure("没有可检测语言的文字。");
        }

        try
        {
            lock (DetectorLock)
            {
                var prediction = Detector.Value.PredictLanguage(text.Trim());
                if (prediction is null ||
                    string.IsNullOrWhiteSpace(prediction.Language))
                {
                    return DetectByScript(text) ??
                        OfflineLanguageDetectionResult.Failure(
                            "文字过短或特征不足，无法可靠识别源语言。");
                }

                var code = NormalizeDetectedCode(prediction.Language, text);
                var confidence = Math.Clamp(prediction.Probability, 0d, 1d);
                if (TranslationLanguageCatalog.IsSupportedSource(code) &&
                    (prediction.IsReliable || confidence >= 0.70d))
                {
                    return new OfflineLanguageDetectionResult(
                        code,
                        confidence,
                        prediction.IsReliable);
                }

                return DetectByScript(text) ??
                    new OfflineLanguageDetectionResult(
                        code,
                        confidence,
                        prediction.IsReliable,
                        TranslationLanguageCatalog.IsSupportedSource(code)
                            ? "文字特征不足，无法可靠识别源语言。"
                            : $"检测到的语言“{prediction.Language}”暂无离线翻译模型。");
            }
        }
        catch (Exception exception) when (
            exception is DllNotFoundException or BadImageFormatException or
                EntryPointNotFoundException or InvalidOperationException)
        {
            return OfflineLanguageDetectionResult.Failure(
                "无法加载本地语言检测器，请重新安装或使用完整的免安装包。");
        }
    }

    private static IntPtr ResolveNativeLibrary(
        string libraryName,
        System.Reflection.Assembly assembly,
        DllImportSearchPath? searchPath)
    {
        if (!string.Equals(libraryName, "libcld3", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(libraryName, "libcld3.dll", StringComparison.OrdinalIgnoreCase))
        {
            return IntPtr.Zero;
        }

        var architecture = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "win-x64",
            Architecture.Arm64 => "win-arm64",
            _ => string.Empty,
        };
        if (string.IsNullOrEmpty(architecture))
        {
            return IntPtr.Zero;
        }

        var path = Path.Combine(
            AppContext.BaseDirectory,
            "runtimes",
            architecture,
            "native",
            "libcld3.dll");
        return File.Exists(path) ? NativeLibrary.Load(path) : IntPtr.Zero;
    }

    private static string NormalizeDetectedCode(string detectedCode, string text)
    {
        var normalized = detectedCode.Trim().Replace('_', '-');
        if (string.Equals(normalized, "iw", StringComparison.OrdinalIgnoreCase))
        {
            return "he";
        }

        if (normalized.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
        {
            return LooksTraditionalChinese(text) ? "zh_hant" : "zh";
        }

        // Mozilla uses nb/no separately but CLD3 reports generic Norwegian.
        // The no-en model is the compatible generic route.
        if (string.Equals(normalized, "no", StringComparison.OrdinalIgnoreCase))
        {
            return "no";
        }

        return TranslationLanguageCatalog.NormalizeOfflineCode(normalized) ??
            normalized.ToLowerInvariant();
    }

    private static OfflineLanguageDetectionResult? DetectByScript(string text)
    {
        var code = text.Any(character => character is 'ə' or 'Ə')
            ? "az"
            : text.Any(character => character is >= '\u3040' and <= '\u30ff')
            ? "ja"
            : text.Any(character => character is >= '\uac00' and <= '\ud7af')
                ? "ko"
                : text.Any(character => character is >= '\u0e00' and <= '\u0e7f')
                    ? "th"
                    : text.Any(character => character is >= '\u0370' and <= '\u03ff')
                        ? "el"
                        : text.Any(character => character is >= '\u0590' and <= '\u05ff')
                            ? "he"
                            : DetectIndicScript(text);
        if (code is null && text.Any(character =>
                character is >= '\u3400' and <= '\u9fff'))
        {
            code = LooksTraditionalChinese(text) ? "zh_hant" : "zh";
        }

        return code is null
            ? null
            : new OfflineLanguageDetectionResult(code, 1d, true);
    }

    private static string? DetectIndicScript(string text)
    {
        if (text.Any(character => character is >= '\u0980' and <= '\u09ff'))
        {
            return "bn";
        }

        if (text.Any(character => character is >= '\u0a80' and <= '\u0aff'))
        {
            return "gu";
        }

        if (text.Any(character => character is >= '\u0c80' and <= '\u0cff'))
        {
            return "kn";
        }

        if (text.Any(character => character is >= '\u0d00' and <= '\u0d7f'))
        {
            return "ml";
        }

        if (text.Any(character => character is >= '\u0b80' and <= '\u0bff'))
        {
            return "ta";
        }

        return text.Any(character => character is >= '\u0c00' and <= '\u0c7f')
            ? "te"
            : null;
    }

    private static bool LooksTraditionalChinese(string text)
    {
        const string traditionalMarkers =
            "體臺灣為這個們來時會裡後發與國學說對開關長間點無萬東車門風電書見頭實從還過現機樣應當經總區別種線數據網頁軟體設置下載顯示譯檔儲處錄圖標選擇確認錯誤";
        return text.Any(traditionalMarkers.Contains);
    }
}
