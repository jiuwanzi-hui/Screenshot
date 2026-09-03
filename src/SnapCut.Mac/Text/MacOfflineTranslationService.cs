using BergamotTranslatorSharp;
using SnapCut.Mac.App;

namespace SnapCut.Mac.Text;

internal sealed class MacOfflineTranslationService : IDisposable
{
    private readonly Func<MacSettings> _settings;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public MacOfflineTranslationService(Func<MacSettings> settings)
    {
        _settings = settings;
    }

    public async Task<MacTranslationResult> TranslateAsync(
        string text,
        CancellationToken cancellationToken = default)
    {
        var path = _settings().OfflineTranslationConfigPath?.Trim();
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return MacTranslationResult.Failure(
                "尚未配置离线翻译模型 config 文件。");
        }

        await _lock.WaitAsync(cancellationToken);
        try
        {
            var result = await Task.Run(() =>
            {
                using var service = new BlockingService(path);
                return service.Translate(text);
            }, cancellationToken);
            return string.IsNullOrWhiteSpace(result)
                ? MacTranslationResult.Failure("离线翻译未返回内容。")
                : new MacTranslationResult(true, result.Trim(), null);
        }
        catch (OperationCanceledException)
        {
            return MacTranslationResult.Failure("离线翻译已取消。");
        }
        catch (Exception exception) when (
            exception is DllNotFoundException or BadImageFormatException or
                EntryPointNotFoundException or InvalidOperationException)
        {
            return MacTranslationResult.Failure(
                $"无法加载离线翻译模型：{exception.Message}");
        }
        finally
        {
            _lock.Release();
        }
    }

    public void Dispose() => _lock.Dispose();
}
