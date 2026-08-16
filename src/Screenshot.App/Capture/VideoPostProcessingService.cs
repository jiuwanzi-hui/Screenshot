using System.IO;
using System.Diagnostics.CodeAnalysis;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.PixelFormats;
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
    Justification = "In-flight WinRT thumbnail operations can outlive the window close event; the session and gate are reclaimed together after those tasks complete.")]
internal sealed class VideoPreviewSession
{
    private readonly MediaComposition _composition;
    private readonly SemaphoreSlim _thumbnailGate = new(1, 1);

    private VideoPreviewSession(
        MediaComposition composition,
        TimeSpan duration)
    {
        _composition = composition;
        Duration = duration;
    }

    public TimeSpan Duration { get; }

    public static async Task<VideoPreviewSession> CreateAsync(string inputPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        var file = await StorageFile.GetFileFromPathAsync(
            Path.GetFullPath(inputPath));
        var clip = await MediaClip.CreateFromFileAsync(file);
        var composition = new MediaComposition();
        composition.Clips.Add(clip);
        return new VideoPreviewSession(composition, clip.OriginalDuration);
    }

    public async Task<byte[]> GetFrameAsync(
        TimeSpan position,
        int thumbnailWidth = 960)
    {
        var finalFramePosition = Duration > TimeSpan.FromMilliseconds(1)
            ? Duration - TimeSpan.FromMilliseconds(1)
            : TimeSpan.Zero;
        var clampedPosition = position < TimeSpan.Zero
            ? TimeSpan.Zero
            : position > finalFramePosition
                ? finalFramePosition
                : position;
        await _thumbnailGate.WaitAsync();
        try
        {
            using var thumbnail = await _composition.GetThumbnailAsync(
                clampedPosition,
                Math.Clamp(thumbnailWidth, 96, 1920),
                0,
                VideoFramePrecision.NearestFrame);
            return await VideoPostProcessingService.ReadAllBytesAsync(thumbnail);
        }
        finally
        {
            _thumbnailGate.Release();
        }
    }
}

internal static class VideoPostProcessingService
{
    public static async Task<TimeSpan> GetDurationAsync(string inputPath)
    {
        var clip = await LoadClipAsync(inputPath);
        return clip.OriginalDuration;
    }

    public static async Task<string> TrimMp4Async(
        string inputPath,
        TimeSpan start,
        TimeSpan end,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var clip = await LoadClipAsync(inputPath);
        var duration = clip.OriginalDuration;
        ValidateRange(start, end, duration);
        clip.TrimTimeFromStart = start;
        clip.TrimTimeFromEnd = duration - end;

        var composition = new MediaComposition();
        composition.Clips.Add(clip);
        var outputPath = CreateOutputPath(inputPath, "-trimmed", ".mp4");
        var output = await CreateOutputFileAsync(outputPath);
        var operation = composition.RenderToFileAsync(
            output,
            MediaTrimmingPreference.Precise);
        operation.Progress = (_, value) => progress?.Report(value / 100d);
        using var registration = cancellationToken.Register(operation.Cancel);
        var result = await operation;
        if (result != TranscodeFailureReason.None)
        {
            throw new InvalidOperationException($"视频裁剪失败：{result}");
        }
        return output.Path;
    }

    public static async Task<string> ExportAnimatedImageAsync(
        string inputPath,
        TimeSpan start,
        TimeSpan end,
        AnimatedImageFormat format,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var clip = await LoadClipAsync(inputPath);
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
        Image<Rgba32>? animation = null;
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
                    VideoFramePrecision.NearestFrame);
                var bytes = await ReadAllBytesAsync(thumbnail);
                using var frame = SharpImage.Load<Rgba32>(bytes);
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
            var outputPath = CreateOutputPath(inputPath, string.Empty, extension);
            if (format == AnimatedImageFormat.Gif)
            {
                animation.Metadata.GetGifMetadata().RepeatCount = 0;
                await animation.SaveAsync(
                    outputPath,
                    new GifEncoder(),
                    cancellationToken);
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
                    cancellationToken);
            }
            progress?.Report(1);
            return outputPath;
        }
        finally
        {
            animation?.Dispose();
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

    private static async Task<MediaClip> LoadClipAsync(string inputPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        var file = await StorageFile.GetFileFromPathAsync(
            Path.GetFullPath(inputPath));
        return await MediaClip.CreateFromFileAsync(file);
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
        IRandomAccessStreamWithContentType stream)
    {
        stream.Seek(0);
        using var reader = new DataReader(stream.GetInputStreamAt(0));
        var length = checked((uint)stream.Size);
        _ = await reader.LoadAsync(length);
        var bytes = new byte[length];
        reader.ReadBytes(bytes);
        return bytes;
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
