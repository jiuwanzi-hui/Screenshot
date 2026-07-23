using Screenshot.App.Capture;

namespace Screenshot.App.Pin;

public sealed class PinnedImageManager : IDisposable
{
    private readonly HashSet<PinnedImageWindow> _windows = [];
    private readonly Func<CapturedImage, Task>? _recognizeTextAsync;
    private bool _disposed;

    public PinnedImageManager(Func<CapturedImage, Task>? recognizeTextAsync = null)
    {
        _recognizeTextAsync = recognizeTextAsync;
    }

    public int Count => _windows.Count;

    public void Pin(CapturedImage capturedImage)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(capturedImage);

        PinnedImageWindow? window = null;

        try
        {
            window = new PinnedImageWindow(capturedImage, _recognizeTextAsync);
            window.Closed += OnPinnedImageWindowClosed;
            _windows.Add(window);
            window.Show();
        }
        catch
        {
            if (window is not null)
            {
                window.Closed -= OnPinnedImageWindowClosed;
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
        _windows.Remove(window);
    }
}
