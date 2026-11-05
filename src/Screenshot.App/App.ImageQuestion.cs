using Screenshot.App.Infrastructure;
using Screenshot.App.Text;

namespace Screenshot.App;

public partial class App
{
    private bool _openingImageQuestion;
    private ImageQuestionWindow? _emptyImageQuestionWindow;
    private void OnImageQuestionRequested(object? sender, EventArgs e) => _ = ShowEmptyImageQuestionAsync();

    private async Task ShowEmptyImageQuestionAsync()
    {
        if (ForwardFeature("image-question") || _openingImageQuestion) return;
        _openingImageQuestion = true;
        using var activity = FeatureActivity.Begin();
        _idleEfficiency?.Wake();
        try
        {
            if (!_currentSettings.EnableImageQuestion)
            { await ShowFeedbackWithoutBlockingMouseAsync("AI 问图已关闭，可在设置 → 翻译中开启。"); return; }
            if (_emptyImageQuestionWindow is { } existing)
            {
                existing.ShowForUserRequest();
                return;
            }
            var profiles = await ImageQuestionService.GetReadyProfilesAsync(_currentSettings);
            if (profiles.Length == 0)
            { await ShowFeedbackWithoutBlockingMouseAsync("没有配置在线 AI 模型，请先在设置 → 翻译中配置并启用支持图片的模型。"); return; }
            var window = new ImageQuestionWindow(null, profiles);
            _emptyImageQuestionWindow = window;
            window.Closed += OnEmptyImageQuestionClosed;
            window.ShowForUserRequest();
        }
        catch { await ShowFeedbackWithoutBlockingMouseAsync("无法打开 AI 问图，请重试。"); }
        finally { _openingImageQuestion = false; }
    }

    private void OnEmptyImageQuestionClosed(object? sender, EventArgs e)
    {
        if (sender is not ImageQuestionWindow window) return;
        window.Closed -= OnEmptyImageQuestionClosed;
        if (ReferenceEquals(_emptyImageQuestionWindow, window)) _emptyImageQuestionWindow = null;
    }
}
