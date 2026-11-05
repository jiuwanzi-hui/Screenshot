using System.Windows;
using System.Windows.Media.Imaging;
using Screenshot.App.Core;
using Screenshot.App.Text;

namespace Screenshot.App.Pin;

public partial class PinnedImageWindow
{
    internal async Task<bool> ShowImageQuestionAsync(BitmapSource image, Window source)
    {
        try
        {
            var profiles = await ImageQuestionService.GetReadyProfilesAsync(_settingsProvider?.Invoke() ?? new AppSettings());
            if (_isClosed || !source.IsVisible) return false;
            if (profiles.Length == 0)
            {
                const string message = "没有配置在线 AI 模型，请先在设置 → 翻译中配置并启用支持图片的模型。";
                StatusMenuItem.Header = message;
                _editorToolbar?.SetTranslationProgress(message);
                return false;
            }
            var editor = new WeakReference<Window>(source);
            var chat = new ImageQuestionWindow(image, profiles, () =>
            {
                if (editor.TryGetTarget(out var target) && target.IsVisible)
                { target.WindowState = WindowState.Normal; target.Activate(); }
            });
            chat.Show();
            return true;
        }
        catch
        {
            if (!_isClosed) _editorToolbar?.SetTranslationProgress("无法打开 AI 问图，请检查在线模型配置后重试。");
            return false;
        }
    }
}
