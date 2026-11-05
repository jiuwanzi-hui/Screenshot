using Screenshot.App.Core;

namespace Screenshot.App.Capture;

public sealed partial class RegionCaptureCoordinator
{
    private CaptureOverlayWindow? _preparedCaptureOverlay;
    private AppSettings? _preparedCaptureSettings;
    private CapturedImage? _preparedResidentFrame;
    private long _preparedResidentFrameId;
    private VideoRecordingControlWindow? _preparedRecordingToolbar;

    internal void PrepareResidentFrame(long id, CapturedImage image)
    {
        _preparedResidentFrame?.Dispose();
        _preparedResidentFrame = image;
        _preparedResidentFrameId = id;
    }

    internal CapturedImage? TakePreparedResidentFrame(long id)
    {
        var image = _preparedResidentFrame;
        _preparedResidentFrame = null;
        if (_preparedResidentFrameId == id) return image;
        image?.Dispose();
        return null;
    }

    internal void PrepareResidentRecordingToolbar() =>
        _preparedRecordingToolbar ??= VideoRecordingControlWindow.PrepareForResidentSelection();

    internal void DiscardPreparedRecordingToolbar()
    {
        _preparedRecordingToolbar?.Close();
        _preparedRecordingToolbar = null;
    }

    private VideoRecordingControlWindow? TakePreparedRecordingToolbar()
    {
        var toolbar = _preparedRecordingToolbar;
        _preparedRecordingToolbar = null;
        return toolbar;
    }

    internal void PrepareResidentCaptureToolbar()
    {
        var settings = _settingsProvider();
        if (_preparedCaptureOverlay is not null && ReferenceEquals(settings, _preparedCaptureSettings)) return;
        DiscardPreparedCaptureToolbar();
        var options = CreateInteractiveOptions(settings, false, false, true, null, null, false, false, null, IntPtr.Zero);
        _preparedCaptureOverlay = CaptureOverlayWindow.PrepareForResidentSelection(options);
        _preparedCaptureSettings = settings;
    }

    internal void DiscardPreparedCaptureToolbar()
    {
        _preparedResidentFrame?.Dispose();
        _preparedResidentFrame = null;
        var overlay = _preparedCaptureOverlay;
        _preparedCaptureOverlay = null;
        _preparedCaptureSettings = null;
        overlay?.DiscardPreparedSession();
    }

    internal CaptureOverlayWindow? TakePreparedCaptureToolbar(AppSettings settings, bool eligible)
    {
        if (!eligible || !ReferenceEquals(settings, _preparedCaptureSettings) ||
            _preparedCaptureOverlay?.CanCompletePreparedSession != true)
        {
            DiscardPreparedCaptureToolbar();
            return null;
        }
        var overlay = _preparedCaptureOverlay;
        _preparedCaptureOverlay = null;
        _preparedCaptureSettings = null;
        return overlay;
    }

    private CaptureOverlayOptions CreateInteractiveOptions(AppSettings settings,
        bool recognizeTextAfterSelection, bool translateTextAfterSelection, bool trackOrdinaryCaptureRegion,
        CapturePointerContinuation? pointerContinuation, ScreenRegion? initialSelection,
        bool deferInitialColorPickerActivation, bool deferInitialScreenCapture,
        Action? capturePresented, IntPtr residentSelectionWindow) =>
                new CaptureOverlayOptions
                {
                    SaveDirectory = settings.SaveDirectory,
                    PngSaveLocationMode = settings.PngSaveLocationMode,
                    AutoSaveOnComplete = settings.AutoSaveOnComplete,
                    ScreenshotScalePercent = settings.ScreenshotScalePercent,
                    CaptureAspectRatio = settings.CaptureAspectRatio,
                    CaptureAspectRatioChanged = _captureAspectRatioChanged,
                    CompletionHotKey = settings.CompleteCaptureHotKey,
                    KeepHistory = true,
                    HistoryLimit = settings.HistoryLimit,
                    HistoryService = _historyService,
                    PinnedImageManager = _pinnedImageManager,
                    TranslationTargetLanguage = settings.TranslationTargetLanguage,
                    RecognitionResultPresentation = settings.RecognitionResultPresentation,
                    StartOcrAsync = ShowOcrResultAndCompleteCaptureAsync,
                    RecognizeTextAsync = RecognizeTextAsync,
                    RecognizeTextWithCancellationAsync = RecognizeTextAsync,
                    AutoOcrAfterCapture = settings.AutoOcrAfterCapture,
                    SettingsProvider = _settingsProvider,
                    Watermark = trackOrdinaryCaptureRegion ? settings.Watermark : null,
                    TranslateTextAsync = TranslateTextAsync,
                    TranslateTextWithCancellationAsync = TranslateTextAsync,
                    PrepareTranslation = token => Text.TranslationPreparation.Begin(_settingsProvider(), token),
                    ShowRecognitionPopupAsync = ShowRecognitionPopupAsync,
                    RecognizeFormulaAsync = RecognizeFormulaAsync,
                    RecognizeTextAfterSelection = recognizeTextAfterSelection,
                    TranslateTextAfterSelection = translateTextAfterSelection,
                    InitialPointerContinuation = pointerContinuation,
                    StartScrollCaptureAsync = RequestScrollCaptureFromSelectionAsync,
                    StartScrollCaptureWithSnapshotAsync =
                        RequestScrollCaptureFromSelectionWithSnapshotAsync,
                    StartVideoRecordingAsync = RequestVideoRecordingFromSelectionAsync,
                    InitialSelection = initialSelection,
                    CaptureCompleted = trackOrdinaryCaptureRegion
                        ? region => _lastOrdinaryCaptureRegion = region
                        : null,
                    CaptureClosed = OnInteractiveCaptureClosed,
                    CapturePresented = capturePresented,
                    ResidentSelectionWindow = residentSelectionWindow,
                    CaptureStartupFailed = message =>
                    {
                        _statusReporter(message);
                        _ = StartupFeedbackWindow.ShowAsync(message, visibleMilliseconds: 4000);
                    },
                    ArrowStyle = settings.ArrowStyle,
                    ArrowToolMode = settings.ArrowToolMode,
                    ShapeToolMode = settings.ShapeToolMode,
                    LastAnnotationTool = settings.LastAnnotationTool,
                    VisibleToolbarFeatures =
                        settings.VisibleCaptureToolbarFeatures.ToArray(),
                    ToolbarFeatureOrder =
                        settings.CaptureToolbarFeatureOrder.ToArray(),
                    ToolbarFeatureShortcuts = new Dictionary<CaptureToolbarFeature, string>(
                        settings.CaptureToolbarFeatureShortcuts),
                    ToolbarRows = settings.CaptureToolbarRows,
                    ToolbarScalePercent = settings.ToolbarScalePercent,
                    DeferInitialColorPickerActivation =
                        deferInitialColorPickerActivation,
                    DeferInitialScreenCapture = deferInitialScreenCapture,
                    ArrowStyleChanged = _arrowStyleChanged,
                    ArrowToolModeChanged = _arrowToolModeChanged,
                    ShapeToolModeChanged = _shapeToolModeChanged,
                    LastAnnotationToolChanged = _lastAnnotationToolChanged,
                    CustomStrokeColor = settings.CustomStrokeColor,
                    CustomStrokeColorChanged = _customStrokeColorChanged,
                    CustomColorPalette = settings.CustomColorPalette,
                    CustomColorPaletteChanged = _customColorPaletteChanged,
                    ToolbarPositionXRatio =
                        settings.CaptureToolbarPositionXRatio,
                    ToolbarPositionYRatio =
                        settings.CaptureToolbarPositionYRatio,
                    ToolbarPositionChanged = _captureToolbarPositionChanged,
                };
}
