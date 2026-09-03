namespace Screenshot.App.Core;

public sealed record HotKeyBinding(HotKeyAction Action, HotKeyGesture Gesture);

public sealed record HotKeyValidationResult(bool IsValid, string? ErrorMessage)
{
    public static HotKeyValidationResult Valid { get; } = new(true, ErrorMessage: null);
}

public static class HotKeyConfiguration
{
    public static IReadOnlyList<HotKeyBinding> CreateBindings(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var bindings = new List<HotKeyBinding>();
        AddBinding(bindings, HotKeyAction.RegionCapture, settings.RegionCaptureHotKey);
        AddBinding(bindings, HotKeyAction.CompleteCapture, settings.CompleteCaptureHotKey);
        AddBinding(bindings, HotKeyAction.VideoRecording, settings.VideoRecordingHotKey);
        AddBinding(
            bindings,
            HotKeyAction.EndVideoRecording,
            settings.EndVideoRecordingHotKey);
        AddBinding(bindings, HotKeyAction.RecognizeText, settings.OcrHotKey);
        AddBinding(
            bindings,
            HotKeyAction.TranslateSelectedText,
            settings.TextTranslationHotKey);
        AddBinding(bindings, HotKeyAction.PinImage, settings.PinHotKey);
        AddBinding(bindings, HotKeyAction.OpenSettings, settings.OpenSettingsHotKey);
        return bindings;
    }

    public static HotKeyValidationResult Validate(IReadOnlyList<HotKeyBinding> bindings)
    {
        var actions = new HashSet<HotKeyAction>();
        var gestures = new HashSet<HotKeyGesture>();

        foreach (var binding in bindings)
        {
            if (!actions.Add(binding.Action))
            {
                return new HotKeyValidationResult(false, "同一功能存在重复快捷键配置。");
            }

            if (binding.Gesture.IsSystemReserved(out var reservedError))
            {
                return new HotKeyValidationResult(false, reservedError);
            }

            if (binding.Action == HotKeyAction.CompleteCapture &&
                !HotKeyGesture.IsCompletionShortcutAllowed(binding.Gesture))
            {
                return new HotKeyValidationResult(
                    false,
                    "完成截图快捷键只支持键盘组合或单独一个英文字母，不支持其他单键和鼠标键。");
            }

            if (!gestures.Add(binding.Gesture))
            {
                return new HotKeyValidationResult(
                    false,
                    $"快捷键 {binding.Gesture} 被多个功能重复使用。");
            }
        }

        return HotKeyValidationResult.Valid;
    }

    private static void AddBinding(
        List<HotKeyBinding> bindings,
        HotKeyAction action,
        string? configuredGesture)
    {
        if (string.IsNullOrWhiteSpace(configuredGesture))
        {
            return;
        }

        var parsed = action == HotKeyAction.CompleteCapture
            ? HotKeyGesture.TryParseCompletionShortcut(
                configuredGesture,
                out var gesture,
                out var errorMessage)
            : HotKeyGesture.TryParse(
                configuredGesture,
                out gesture,
                out errorMessage);
        if (!parsed)
        {
            throw new ArgumentException(
                $"{GetActionName(action)}：{errorMessage}",
                nameof(configuredGesture));
        }

        if (action == HotKeyAction.CompleteCapture &&
            !HotKeyGesture.IsCompletionShortcutAllowed(gesture))
        {
            throw new ArgumentException(
                "完成截图：只支持键盘组合或单独一个英文字母，不支持其他单键和鼠标键。",
                nameof(configuredGesture));
        }

        bindings.Add(new HotKeyBinding(action, gesture));
    }

    private static string GetActionName(HotKeyAction action)
    {
        return action switch
        {
            HotKeyAction.RegionCapture => "区域截图",
            HotKeyAction.CompleteCapture => "完成截图",
            HotKeyAction.VideoRecording => "视频录制",
            HotKeyAction.EndVideoRecording => "结束录制",
            HotKeyAction.ScrollCapture => "长截图",
            HotKeyAction.RecognizeText => "翻译",
            HotKeyAction.TranslateSelectedText => "选中文字翻译",
            HotKeyAction.PinImage => "钉图",
            HotKeyAction.OpenSettings => "打开设置",
            _ => action.ToString(),
        };
    }
}
