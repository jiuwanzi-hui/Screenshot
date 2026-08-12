using Screenshot.App.Capture;
using Screenshot.App.Text;

namespace Screenshot.App.Pin;

public sealed class PinnedImageManager : IDisposable
{
    private readonly HashSet<PinnedImageWindow> _windows = [];
    private readonly Func<CapturedImage, Task<OcrRecognitionResult>>?
        _recognizeTextAsync;
    private readonly Func<OcrRecognitionResult, Task<TranslationSegmentsResult>>?
        _translateTextAsync;
    private readonly Action? _openSettings;
    private bool _disposed;

    public PinnedImageManager(
        Func<CapturedImage, Task<OcrRecognitionResult>>? recognizeTextAsync = null,
        Func<OcrRecognitionResult, Task<TranslationSegmentsResult>>?
            translateTextAsync = null,
        Action? openSettings = null)
    {
        _recognizeTextAsync = recognizeTextAsync;
        _translateTextAsync = translateTextAsync;
        _openSettings = openSettings;
    }

    public int Count => _windows.Count;

    public void Pin(CapturedImage capturedImage)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(capturedImage);

        PinnedImageWindow? window = null;

        try
        {
            window = new PinnedImageWindow(
                capturedImage,
                _recognizeTextAsync,
                _translateTextAsync);
            window.Closed += OnPinnedImageWindowClosed;
            window.SettingsRequested += OnPinnedImageSettingsRequested;
            _windows.Add(window);
            window.Show();
        }
        catch
        {
            if (window is not null)
            {
                window.Closed -= OnPinnedImageWindowClosed;
                window.SettingsRequested -= OnPinnedImageSettingsRequested;
                _windows.Remove(window);
            }

            capturedImage.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        foreach (var window in _windows.ToArray())
        {
            window.Closed -= OnPinnedImageWindowClosed;
            window.SettingsRequested -= OnPinnedImageSettingsRequested;
            window.Close();
        }

        _windows.Clear();
    }

    private void OnPinnedImageWindowClosed(object? sender, EventArgs e)
    {
        if (sender is not PinnedImageWindow window)
        {
            return;
        }

        window.Closed -= OnPinnedImageWindowClosed;
        window.SettingsRequested -= OnPinnedImageSettingsRequested;
        _windows.Remove(window);
    }

    private void OnPinnedImageSettingsRequested(object? sender, EventArgs e)
    {
        _openSettings?.Invoke();
    }
}
