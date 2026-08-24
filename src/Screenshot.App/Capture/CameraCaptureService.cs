using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.IO;
using DirectShowLib;
using Windows.Devices.Enumeration;
using Windows.Graphics.Imaging;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.MediaProperties;
using Windows.Storage.Streams;
using WinRtBitmapEncoder = Windows.Graphics.Imaging.BitmapEncoder;

namespace Screenshot.App.Capture;

internal sealed class CameraCaptureService : IAsyncDisposable
{
    private readonly MediaCapture? _capture;
    private readonly MediaFrameReader? _reader;
    private readonly DirectShowCameraCapture? _directShowCapture;
    private CancellationTokenSource? _previewLoopCts;
    private Task? _previewLoop;
    private readonly Task? _readerWatchdog;
    private int _frameBusy;
    private int _frameCount;
    private bool _disposed;

    private CameraCaptureService(MediaCapture capture, MediaFrameReader? reader)
    {
        _capture = capture;
        _reader = reader;
        if (_reader is not null)
        {
            _reader.FrameArrived += OnFrameArrived;
        }
        else
        {
            StartPreviewLoop();
        }

        if (_reader is not null)
        {
            // A few webcam drivers report a successful reader start but do
            // not raise FrameArrived. Give those drivers a preview-frame
            // fallback instead of leaving an empty camera window forever.
            _readerWatchdog = ReaderWatchdogAsync();
        }
    }

    private CameraCaptureService(DirectShowCameraCapture capture)
    {
        _directShowCapture = capture;
        _directShowCapture.FrameReady += OnDirectShowFrameReady;
    }

    internal event Action<BitmapSource>? FrameReady;

    private void OnDirectShowFrameReady(BitmapSource image) =>
        FrameReady?.Invoke(image);

    internal static async Task<IReadOnlyList<RecordingDeviceOption>> GetDevicesAsync()
    {
        var candidates = await GetCameraCandidatesAsync();
        return candidates
            // The same physical camera can be published by both WinRT and
            // DirectShow. Virtual-camera WinRT registrations can enumerate
            // successfully while returning no frames, so prefer their
            // DirectShow filter. Physical cameras keep the WinRT fast path.
            .GroupBy(candidate => candidate.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(group => group
                .OrderByDescending(candidate =>
                    IsVirtualOrPhoneCamera(candidate.Name) &&
                    DirectShowCameraCapture.IsDeviceId(candidate.Id))
                .ThenByDescending(candidate => candidate.SourceGroup is not null)
                .ThenBy(candidate => candidate.Id.StartsWith(
                    "directshow:", StringComparison.OrdinalIgnoreCase))
                .First())
            .Select(candidate => new RecordingDeviceOption(candidate.Id, candidate.Name))
            // Keep physical cameras first for the default choice, while
            // retaining every virtual, phone, and link camera in the list.
            .OrderBy(device => IsVirtualOrPhoneCamera(device.Name) ? 1 : 0)
            .ThenBy(device => device.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    private sealed record CameraCandidate(
        string Id,
        string Name,
        MediaFrameSourceGroup? SourceGroup);

    private static async Task<IReadOnlyList<CameraCandidate>> GetCameraCandidatesAsync()
    {
        var candidates = new Dictionary<string, CameraCandidate>(
            StringComparer.OrdinalIgnoreCase);

        // Most USB cameras are exposed through DeviceClass.VideoCapture.
        // Keep this query independent from the frame-source query: a driver
        // failure in either API must not hide devices returned by the other.
        try
        {
            var devices = await DeviceInformation.FindAllAsync(
                DeviceClass.VideoCapture);
            foreach (var device in devices)
            {
                AddCameraCandidate(candidates, device.Id, device.Name, null);
            }
        }
        catch
        {
            // Some virtual-camera drivers are not available through this
            // device class, or the query can fail before frame sources load.
        }

        // Virtual cameras (including EMEET STUDIO Virtual Camera) may only be
        // published through MediaFrameSourceGroup. Include their device info
        // and remember the group so CreateAsync can initialize that source.
        try
        {
            var sourceGroups = await MediaFrameSourceGroup.FindAllAsync();
            foreach (var group in sourceGroups)
            {
                foreach (var sourceInfo in group.SourceInfos)
                {
                    var device = sourceInfo.DeviceInformation;
                    if (device is not null)
                    {
                        AddCameraCandidate(
                            candidates,
                            device.Id,
                            device.Name,
                            group);
                    }
                }
            }
        }
        catch
        {
            // Frame-source groups are optional; the ordinary device list is
            // still useful when this API is unavailable.
        }

        // Legacy virtual cameras are often registered only as DirectShow
        // video-input filters. EMEET STUDIO Virtual Camera is one example:
        // it works in applications that use DirectShow but is absent from
        // the WinRT device APIs above.
        try
        {
            foreach (var directShowDevice in DsDevice.GetDevicesOfCat(
                         FilterCategory.VideoInputDevice))
            {
                try
                {
                    AddCameraCandidate(
                        candidates,
                        DirectShowCameraCapture.ToDeviceId(
                            directShowDevice.DevicePath),
                        directShowDevice.Name,
                        null);
                }
                finally
                {
                    directShowDevice.Dispose();
                }
            }
        }
        catch
        {
            // DirectShow is optional on some Windows installations. The
            // WinRT camera list remains available when this query fails.
        }

        return candidates.Values
            .Where(candidate =>
                !string.IsNullOrWhiteSpace(candidate.Id) &&
                !string.IsNullOrWhiteSpace(candidate.Name))
            .ToArray();
    }

    private static void AddCameraCandidate(
        IDictionary<string, CameraCandidate> candidates,
        string? id,
        string? name,
        MediaFrameSourceGroup? sourceGroup)
    {
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        if (candidates.TryGetValue(id, out var existing))
        {
            if (existing.SourceGroup is null && sourceGroup is not null)
            {
                candidates[id] = existing with { SourceGroup = sourceGroup };
            }

            return;
        }

        candidates[id] = new CameraCandidate(id, name.Trim(), sourceGroup);
    }

    private static bool IsVirtualOrPhoneCamera(string name) =>
        name.Contains("virtual", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("虚拟", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("phone", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("手机", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("link", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("连接", StringComparison.OrdinalIgnoreCase);

    internal static async Task<CameraCaptureService?> CreateAsync(
        string? deviceId = null)
    {
        var candidates = await GetCameraCandidatesAsync();
        var candidate = candidates.FirstOrDefault(item =>
            string.Equals(item.Id, deviceId, StringComparison.OrdinalIgnoreCase)) ??
            (candidates.Count > 0 ? candidates[0] : null);
        if (candidate is null)
        {
            return null;
        }

        // Some virtual cameras (notably EMEET STUDIO Virtual Camera) are
        // registered only as DirectShow filters. WinRT can enumerate the
        // name in some configurations, but it cannot deliver frames from
        // that filter. Use the native DirectShow graph for those devices.
        if (DirectShowCameraCapture.IsDeviceId(candidate.Id))
        {
            var directShow = await DirectShowCameraCapture.CreateAsync(
                DirectShowCameraCapture.FromDeviceId(candidate.Id));
            return directShow is null
                ? null
                : new CameraCaptureService(directShow);
        }

        // Try the ordinary device path first. It is the path used by the
        // Windows Camera app and by most USB webcam drivers. Frame-source
        // groups are kept as a fallback because some virtual-camera drivers
        // expose a group but do not deliver preview frames through it.
        var attempts = new List<(
            MediaFrameSourceGroup? SourceGroup,
            MediaCaptureSharingMode SharingMode,
            MediaCaptureMemoryPreference MemoryPreference)>();
        attempts.Add((
            null,
            MediaCaptureSharingMode.SharedReadOnly,
            MediaCaptureMemoryPreference.Cpu));
        attempts.Add((
            null,
            MediaCaptureSharingMode.ExclusiveControl,
            MediaCaptureMemoryPreference.Cpu));
        if (candidate.SourceGroup is not null)
        {
            attempts.Add((
                candidate.SourceGroup,
                MediaCaptureSharingMode.SharedReadOnly,
                MediaCaptureMemoryPreference.Cpu));
        }

        foreach (var attempt in attempts)
        {
            var capture = new MediaCapture();
            try
            {
                var initializationSettings = new MediaCaptureInitializationSettings
                {
                    StreamingCaptureMode = StreamingCaptureMode.Video,
                    MemoryPreference = attempt.MemoryPreference,
                    SharingMode = attempt.SharingMode,
                };
                if (attempt.SourceGroup is not null)
                {
                    initializationSettings.SourceGroup = attempt.SourceGroup;
                }
                else
                {
                    initializationSettings.VideoDeviceId = candidate.Id;
                }

                await capture.InitializeAsync(initializationSettings);

                // Virtual cameras commonly expose a preview stream without a
                // usable MediaFrameSource. Start the ordinary preview path
                // first so those devices still produce frames.
                if (await TryStartPreviewAsync(capture))
                {
                    return new CameraCaptureService(capture, reader: null);
                }

                // Preview streams are much more consistently exposed by
                // webcams. Recording streams are a fallback for drivers that
                // only advertise VideoRecord.
                var source = capture.FrameSources.Values.FirstOrDefault(candidate =>
                    candidate.Info.SourceKind == MediaFrameSourceKind.Color &&
                    candidate.Info.MediaStreamType == MediaStreamType.VideoPreview);
                source ??= capture.FrameSources.Values.FirstOrDefault(candidate =>
                    candidate.Info.SourceKind == MediaFrameSourceKind.Color &&
                    candidate.Info.MediaStreamType == MediaStreamType.VideoRecord);
                source ??= capture.FrameSources.Values.FirstOrDefault(candidate =>
                    candidate.Info.SourceKind == MediaFrameSourceKind.Color);
                if (source is null)
                {
                    if (attempt.SourceGroup is null &&
                        await TryStartPreviewAsync(capture))
                    {
                        return new CameraCaptureService(capture, reader: null);
                    }

                    capture.Dispose();
                    continue;
                }

                MediaFrameReader? reader = null;
                foreach (var subtype in new[]
                {
                    MediaEncodingSubtypes.Bgra8,
                    MediaEncodingSubtypes.Nv12,
                    string.Empty,
                })
                {
                    try
                    {
                        reader = string.IsNullOrEmpty(subtype)
                            ? await capture.CreateFrameReaderAsync(source)
                            : await capture.CreateFrameReaderAsync(source, subtype);
                        break;
                    }
                    catch
                    {
                        // Continue with the next format supported by the driver.
                    }
                }

                if (reader is null)
                {
                    if (attempt.SourceGroup is null &&
                        await TryStartPreviewAsync(capture))
                    {
                        return new CameraCaptureService(capture, reader: null);
                    }

                    capture.Dispose();
                    continue;
                }

                reader.AcquisitionMode = MediaFrameReaderAcquisitionMode.Realtime;
                var status = await reader.StartAsync();
                if (status != MediaFrameReaderStartStatus.Success)
                {
                    reader.Dispose();
                    if (attempt.SourceGroup is null &&
                        await TryStartPreviewAsync(capture))
                    {
                        return new CameraCaptureService(capture, reader: null);
                    }

                    capture.Dispose();
                    continue;
                }

                return new CameraCaptureService(capture, reader);
            }
            catch
            {
                capture.Dispose();
            }
        }

        return null;
    }

    private static async Task<bool> TryStartPreviewAsync(MediaCapture capture)
    {
        try
        {
            await capture.StartPreviewAsync();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task PreviewLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && !_disposed)
        {
            try
            {
                var capture = _capture;
                if (capture is null)
                {
                    return;
                }

                using var frame = await capture.GetPreviewFrameAsync();
                var bitmap = frame?.SoftwareBitmap;
                if (bitmap is not null)
                {
                    await PublishBitmapAsync(bitmap);
                }
            }
            catch
            {
            }

            try
            {
                await Task.Delay(33, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private void StartPreviewLoop()
    {
        if (_previewLoop is not null || _disposed)
        {
            return;
        }

        _previewLoopCts = new CancellationTokenSource();
        _previewLoop = PreviewLoopAsync(_previewLoopCts.Token);
    }

    private async Task ReaderWatchdogAsync()
    {
        try
        {
            await Task.Delay(1200);
            if (_disposed || Volatile.Read(ref _frameCount) > 0)
            {
                return;
            }

            var capture = _capture;
            if (capture is not null && await TryStartPreviewAsync(capture))
            {
                StartPreviewLoop();
            }
        }
        catch
        {
            // The reader remains active; a later frame can still recover it.
        }
    }

    private async void OnFrameArrived(
        MediaFrameReader sender,
        MediaFrameArrivedEventArgs args)
    {
        if (_disposed || Interlocked.Exchange(ref _frameBusy, 1) != 0)
        {
            return;
        }

        SoftwareBitmap? copiedBitmap = null;
        try
        {
            using var frame = sender.TryAcquireLatestFrame();
            var videoFrame = frame?.VideoMediaFrame;
            var bitmap = videoFrame?.SoftwareBitmap;
            if (bitmap is null && videoFrame?.Direct3DSurface is { } surface)
            {
                try
                {
                    copiedBitmap = await SoftwareBitmap.CreateCopyFromSurfaceAsync(
                        surface);
                    bitmap = copiedBitmap;
                }
                catch
                {
                    // Some drivers expose a surface that cannot be copied by
                    // the current graphics device; keep waiting for a usable
                    // software frame instead of breaking the recording.
                }
            }

            if (bitmap is null)
            {
                return;
            }

            await PublishBitmapAsync(bitmap);
            Interlocked.Increment(ref _frameCount);

        }
        catch
        {
            // Camera availability can change while recording; the next frame
            // will retry without interrupting the screen recording session.
        }
        finally
        {
            copiedBitmap?.Dispose();
            Volatile.Write(ref _frameBusy, 0);
        }
    }

    private async Task PublishBitmapAsync(SoftwareBitmap bitmap)
    {
        using var converted = bitmap.BitmapPixelFormat == BitmapPixelFormat.Bgra8
            ? null
            : SoftwareBitmap.Convert(bitmap, BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied);
        var source = converted ?? bitmap;
        var image = await ConvertToBitmapSourceAsync(source);
        image.Freeze();
        FrameReady?.Invoke(image);
    }

    private static async Task<BitmapSource> ConvertToBitmapSourceAsync(
        SoftwareBitmap source)
    {
        using var stream = new InMemoryRandomAccessStream();
        var encoder = await WinRtBitmapEncoder.CreateAsync(
            WinRtBitmapEncoder.JpegEncoderId,
            stream);
        encoder.IsThumbnailGenerated = false;
        encoder.SetSoftwareBitmap(source);
        await encoder.FlushAsync();
        stream.Seek(0);

        using var reader = new DataReader(stream.GetInputStreamAt(0));
        await reader.LoadAsync((uint)stream.Size);
        var bytes = new byte[(int)stream.Size];
        reader.ReadBytes(bytes);
        using var memory = new MemoryStream(bytes);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = memory;
        image.EndInit();
        return image;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_directShowCapture is not null)
        {
            _directShowCapture.FrameReady -= OnDirectShowFrameReady;
            await _directShowCapture.DisposeAsync();
            return;
        }
        _previewLoopCts?.Cancel();
        if (_previewLoop is not null)
        {
            try
            {
                await _previewLoop;
            }
            catch
            {
                // Preview shutdown must not interrupt screen recording cleanup.
            }
        }

        if (_reader is not null)
        {
            _reader.FrameArrived -= OnFrameArrived;
        }

        try
        {
            if (_reader is not null)
            {
                await _reader.StopAsync();
            }
            else
            {
                if (_capture is not null)
                {
                    await _capture.StopPreviewAsync();
                }
            }
        }
        catch
        {
        }

        _reader?.Dispose();
        _previewLoopCts?.Dispose();
        _capture?.Dispose();
    }
}
