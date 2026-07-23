using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;

namespace Screenshot.App.Capture;

/// <summary>
/// Optional diagnostic that writes each raw viewport frame handed to the
/// composer to disk, together with its sequence number and scroll direction.
/// It is disabled unless the SCREENSHOT_SCROLL_FRAME_DUMP environment variable
/// is set, so it has no effect on normal runs. The captured frames let a
/// stitching defect be reproduced from the exact pixels that triggered it
/// instead of from synthetic data.
/// </summary>
public sealed class ScrollCaptureFrameDump
{
    private const string EnvironmentVariableName = "SCREENSHOT_SCROLL_FRAME_DUMP";
    private readonly string? _directory;
    private int _sequence;

    private ScrollCaptureFrameDump(string? directory)
    {
        _directory = directory;
    }

    public bool IsEnabled => _directory is not null;

    /// <summary>
    /// Creates a dump session for one scroll capture. When the environment
    /// variable holds a path that directory is used; when it holds any other
    /// non-empty value a timestamped folder is created under the user's
    /// Pictures\Screenshot\scroll-frames location. Returns a disabled instance
    /// when the variable is unset or the directory cannot be created.
    /// </summary>
    public static ScrollCaptureFrameDump Create()
    {
        var configured = Environment.GetEnvironmentVariable(EnvironmentVariableName);

        if (string.IsNullOrWhiteSpace(configured))
        {
            return new ScrollCaptureFrameDump(directory: null);
        }

        try
        {
            var root = LooksLikePath(configured)
                ? configured
                : Path.Combine(
                    Environment.GetFolderPath(
                        Environment.SpecialFolder.MyPictures),
                    "Screenshot",
                    "scroll-frames");
            var sessionDirectory = Path.Combine(
                root,
                DateTime.Now.ToString(
                    "yyyyMMdd-HHmmss",
                    CultureInfo.InvariantCulture));
            Directory.CreateDirectory(sessionDirectory);
            return new ScrollCaptureFrameDump(sessionDirectory);
        }
        catch (Exception)
        {
            return new ScrollCaptureFrameDump(directory: null);
        }
    }

    public void Save(Bitmap frame, ScrollCaptureDirection direction)
    {
        Save(frame, direction.ToString().ToLowerInvariant());
    }

    public void SaveInitial(Bitmap frame)
    {
        Save(frame, "initial");
    }

    private void Save(Bitmap frame, string label)
    {
        if (_directory is null)
        {
            return;
        }

        ArgumentNullException.ThrowIfNull(frame);

        try
        {
            var index = Interlocked.Increment(ref _sequence);
            var fileName = $"{index:D4}-{label}.png";
            frame.Save(Path.Combine(_directory, fileName), ImageFormat.Png);
        }
        catch (Exception)
        {
            // Diagnostics must never interrupt a capture. A failed dump is
            // silently ignored so the scroll capture proceeds unchanged.
        }
    }

    private static bool LooksLikePath(string value)
    {
        return value.IndexOfAny(['\\', '/', ':']) >= 0;
    }
}
