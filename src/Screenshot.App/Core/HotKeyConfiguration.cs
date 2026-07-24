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
        AddBinding(bindings, HotKeyAction.RecognizeText, settings.OcrHotKey);
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

        if (!HotKeyGesture.TryParse(configuredGesture, out var gesture, out var errorMessage))
        {
            throw new ArgumentException(
                $"{GetActionName(action)}：{errorMessage}",
                nameof(configuredGesture));
        }

        bindings.Add(new HotKeyBinding(action, gesture));
    }

    private static string GetActionName(HotKeyAction action)
    {
        return action switch
        {
            HotKeyAction.RegionCapture => "区域截图",
            HotKeyAction.ScrollCapture => "长截图",
            HotKeyAction.RecognizeText => "文字识别",
            HotKeyAction.PinImage => "钉图",
            HotKeyAction.OpenSettings => "打开设置",
            _ => action.ToString(),
        };
    }
}
