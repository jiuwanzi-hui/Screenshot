using System.IO;
using System.Diagnostics.CodeAnalysis;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Memory;
using Windows.Media.Editing;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;
using Windows.Storage.Streams;
using SharpImage = SixLabors.ImageSharp.Image;

namespace Screenshot.App.Capture;

internal enum AnimatedImageFormat
{
    Gif,
    WebP,
}

[SuppressMessage(
    "Design",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "The managed-only semaphore can still have canceled waiters unwinding during DisposeAsync; no WaitHandle is created. Native clips are explicitly retired under its gate.")]
internal sealed class VideoPreviewSession : IAsyncDisposable
{
    private MediaComposition? _composition;
    private readonly SemaphoreSlim _thumbnailGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly CancellationToken _lifetimeToken;
    private readonly object _disposeGate = new();
    private Task? _disposeTask;

    private VideoPreviewSession(
        MediaComposition composition,
        TimeSpan duration,
        int pixelWidth,
        int pixelHeight)
    {
        _composition = composition;
        _lifetimeToken = _lifetime.Token;
        Duration = duration;
        PixelWidth = pixelWidth;
        PixelHeight = pixelHeight;
    }

    public TimeSpan Duration { get; }

    public int PixelWidth { get; }

    public int PixelHeight { get; }

    public static async Task<VideoPreviewSession> CreateAsync(string inputPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        cancellationToken.ThrowIfCancellationRequested();
        var file = await StorageFile.GetFileFromPathAsync(
            Path.GetFullPath(inputPath)).AsTask(cancellationToken).ConfigureAwait(false);
        var clip = await MediaClip.CreateFromFileAsync(file).AsTask(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var composition = new MediaComposition();
        composition.Clips.Add(clip);
        var properties = clip.GetVideoEncodingProperties();
        var pixelWidth = properties?.Width is { } width
            ? (int)Math.Min(width, int.MaxValue)
            : 0;
        var pixelHeight = properties?.Height is { } height
            ? (int)Math.Min(height, int.MaxValue)
            : 0;
        return new VideoPreviewSession(
            composition,
            clip.OriginalDuration,
            pixelWidth,
            pixelHeight);
    }

    public async Task<byte[]> GetFrameAsync(
        TimeSpan position,
        int thumbnailWidth = 960,
        CancellationToken cancellationToken = default)
    {
        using var request = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetimeToken);
        var token = request.Token;
        var finalFramePosition = Duration > TimeSpan.FromMilliseconds(1)
            ? Duration - TimeSpan.FromMilliseconds(1)
            : TimeSpan.Zero;
        var clampedPosition = position < TimeSpan.Zero
            ? TimeSpan.Zero
            : position > finalFramePosition
                ? finalFramePosition
                : position;
        await _thumbnailGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            token.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(_composition is null, this);
            using var thumbnail = await _composition.GetThumbnailAsync(
                clampedPosition,
                Math.Clamp(thumbnailWidth, 96, 1920),
                0,
                VideoFramePrecision.NearestFrame).AsTask(token).ConfigureAwait(false);
            return await VideoPostProcessingService.ReadAllBytesAsync(thumbnail, token).ConfigureAwait(false);
        }
        finally
        {
            _thumbnailGate.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposeGate) return new ValueTask(_disposeTask ??= Task.Run(ReleaseAsync));
    }

    private async Task ReleaseAsync()
    {
        await _lifetime.CancelAsync().ConfigureAwait(false);
        await _thumbnailGate.WaitAsync().ConfigureAwait(false);
        try
        {
            _composition?.Clips.Clear();
            _composition = null;
        }
        finally
        {
            _thumbnailGate.Release();
            _lifetime.Dispose();
            Core.MemoryFootprint.TrimAfterHeavyOperation();
        }
    }
}

internal static class VideoPostProcessingService
{
    public static async Task<TimeSpan> GetDurationAsync(string inputPath)
    {
        var clip = await LoadClipAsync(inputPath).ConfigureAwait(false);
        return clip.OriginalDuration;
    }

    public static async Task<string> TrimMp4Async(
        string inputPath,
        TimeSpan start,
        TimeSpan end,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var clip = await LoadClipAsync(inputPath, cancellationToken).ConfigureAwait(false);
        var duration = clip.OriginalDuration;
        ValidateRange(start, end, duration);
        clip.TrimTimeFromStart = start;
        clip.TrimTimeFromEnd = duration - end;

        var composition = new MediaComposition();
        composition.Clips.Add(clip);
        var outputPath = CreateOutputPath(inputPath, "-trimmed", ".mp4");
        StorageFile? output = null;
        var succeeded = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            output = await CreateOutputFileAsync(outputPath).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var operation = composition.RenderToFileAsync(output, MediaTrimmingPreference.Precise);
            var result = await operation.AsTask(cancellationToken,
                new Progress<double>(value => progress?.Report(value / 100d))).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (result != TranscodeFailureReason.None)
                throw new InvalidOperationException($"视频裁剪失败：{result}");
            succeeded = true;
            return output.Path;
        }
        finally
        {
            composition.Clips.Clear();
            if (!succeeded && output is not null) DeleteIncompleteOutput(outputPath);
            Core.MemoryFootprint.TrimAfterHeavyOperation();
        }
    }

    public static async Task<string> ExportAnimatedImageAsync(
        string inputPath,
        TimeSpan start,
        TimeSpan end,
        AnimatedImageFormat format,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var clip = await LoadClipAsync(inputPath, cancellationToken).ConfigureAwait(false);
        var duration = clip.OriginalDuration;
        ValidateRange(start, end, duration);
        clip.TrimTimeFromStart = start;
        clip.TrimTimeFromEnd = duration - end;
        var composition = new MediaComposition();
        composition.Clips.Add(clip);

        var selectedDuration = end - start;
        const int maximumFrames = 240;
        var framesPerSecond = Math.Clamp(
            maximumFrames / Math.Max(1, selectedDuration.TotalSeconds),
            2,
            12);
        var frameCount = Math.Max(1, Math.Min(
            maximumFrames,
            (int)Math.Ceiling(selectedDuration.TotalSeconds * framesPerSecond)));
        var delayMilliseconds = Math.Max(
            20,
            (int)Math.Round(selectedDuration.TotalMilliseconds / frameCount));
        // Keep pooling during this operation, then return all its retained native
        // buffers. The global ImageSharp allocator otherwise outlives every export.
        var configuration = Configuration.Default.Clone();
        configuration.MemoryAllocator = MemoryAllocator.Create(new MemoryAllocatorOptions());
        var decoderOptions = new DecoderOptions { Configuration = configuration };
        Image<Rgba32>? animation = null;
        string? outputPath = null;
        var succeeded = false;
        try
        {
            for (var index = 0; index < frameCount; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var position = TimeSpan.FromTicks(
                    selectedDuration.Ticks * index / frameCount);
                using var thumbnail = await composition.GetThumbnailAsync(
                    position,
                    720,
                    0,
                    VideoFramePrecision.NearestFrame).AsTask(cancellationToken).ConfigureAwait(false);
                var bytes = await ReadAllBytesAsync(thumbnail, cancellationToken).ConfigureAwait(false);
                using var frame = SharpImage.Load<Rgba32>(decoderOptions, bytes);
                SetFrameDelay(frame.Frames.RootFrame, format, delayMilliseconds);
                if (animation is null)
                {
                    animation = frame.Clone();
                }
                else
                {
                    animation.Frames.AddFrame(frame.Frames.RootFrame);
                }
                progress?.Report((index + 1d) / frameCount * 0.92);
            }

            if (animation is null)
            {
                throw new InvalidOperationException("视频中没有可导出的画面。");
            }

            var extension = format == AnimatedImageFormat.Gif ? ".gif" : ".webp";
            outputPath = CreateOutputPath(inputPath, string.Empty, extension);
            if (format == AnimatedImageFormat.Gif)
            {
                animation.Metadata.GetGifMetadata().RepeatCount = 0;
                await animation.SaveAsync(
                    outputPath,
                    new GifEncoder(),
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                animation.Metadata.GetWebpMetadata().RepeatCount = 0;
                await animation.SaveAsync(
                    outputPath,
                    new WebpEncoder
                    {
                        Quality = 82,
                        FileFormat = WebpFileFormatType.Lossy,
                    },
                    cancellationToken).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(1);
            succeeded = true;
            return outputPath;
        }
        finally
        {
            animation?.Dispose();
            configuration.MemoryAllocator.ReleaseRetainedResources();
            composition.Clips.Clear();
            if (!succeeded && outputPath is not null) DeleteIncompleteOutput(outputPath);
            Core.MemoryFootprint.TrimAfterHeavyOperation();
        }
    }

    private static void SetFrameDelay(
        ImageFrame<Rgba32> frame,
        AnimatedImageFormat format,
        int delayMilliseconds)
    {
        if (format == AnimatedImageFormat.Gif)
        {
            frame.Metadata.GetGifMetadata().FrameDelay =
                Math.Max(2, (int)Math.Round(delayMilliseconds / 10d));
        }
        else
        {
            frame.Metadata.GetWebpMetadata().FrameDelay =
                (uint)delayMilliseconds;
        }
    }

    private static async Task<MediaClip> LoadClipAsync(string inputPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        cancellationToken.ThrowIfCancellationRequested();
        var file = await StorageFile.GetFileFromPathAsync(
            Path.GetFullPath(inputPath)).AsTask(cancellationToken).ConfigureAwait(false);
        return await MediaClip.CreateFromFileAsync(file).AsTask(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<StorageFile> CreateOutputFileAsync(string path)
    {
        var directory = Path.GetDirectoryName(path) ?? AppContext.BaseDirectory;
        var folder = await StorageFolder.GetFolderFromPathAsync(directory);
        return await folder.CreateFileAsync(
            Path.GetFileName(path),
            CreationCollisionOption.ReplaceExisting);
    }

    internal static async Task<byte[]> ReadAllBytesAsync(
        IRandomAccessStreamWithContentType stream,
        CancellationToken cancellationToken = default)
    {
        stream.Seek(0);
        using var reader = new DataReader(stream.GetInputStreamAt(0));
        var length = checked((uint)stream.Size);
        _ = await reader.LoadAsync(length).AsTask(cancellationToken).ConfigureAwait(false);
        var bytes = new byte[length];
        reader.ReadBytes(bytes);
        return bytes;
    }

    private static void DeleteIncompleteOutput(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void ValidateRange(
        TimeSpan start,
        TimeSpan end,
        TimeSpan duration)
    {
        if (start < TimeSpan.Zero || end > duration ||
            end - start < TimeSpan.FromMilliseconds(100))
        {
            throw new ArgumentOutOfRangeException(
                nameof(end),
                "至少保留 0.1 秒且裁剪范围不能超出视频长度。");
        }
    }

    private static string CreateOutputPath(
        string inputPath,
        string suffix,
        string extension)
    {
        var directory = Path.GetDirectoryName(inputPath) ?? AppContext.BaseDirectory;
        var name = Path.GetFileNameWithoutExtension(inputPath) + suffix;
        for (var index = 0; ; index++)
        {
            var numbered = index == 0 ? name : $"{name}-{index}";
            var candidate = Path.Combine(directory, numbered + extension);
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }
    }
}
