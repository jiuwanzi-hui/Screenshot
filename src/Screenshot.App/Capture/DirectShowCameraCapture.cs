using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DirectShowLib;

namespace Screenshot.App.Capture;

/// <summary>
/// Captures frames from legacy video-input filters that do not expose a
/// usable WinRT MediaFrameSource. The callback only copies the newest frame;
/// bitmap creation happens outside DirectShow's graph thread.
/// </summary>
internal sealed class DirectShowCameraCapture : IAsyncDisposable
{
    private const string DevicePrefix = "directshow:";

    private readonly IFilterGraph2 _graph;
    private readonly ICaptureGraphBuilder2 _graphBuilder;
    private readonly ISampleGrabber _sampleGrabber;
    private readonly IMediaControl _mediaControl;
    private readonly IBaseFilter _sourceFilter;
    private readonly IBaseFilter _sampleGrabberFilter;
    private readonly IBaseFilter _nullRenderer;
    private readonly SampleGrabberCallback _callback;
    private readonly object _frameSync = new();
    private byte[]? _pendingBuffer;
    private int _pendingLength;
    private int _width;
    private int _height;
    private int _stride;
    private bool _bottomUp;
    private bool _disposed;
    private int _publishScheduled;

    private DirectShowCameraCapture(
        IFilterGraph2 graph,
        ICaptureGraphBuilder2 graphBuilder,
        ISampleGrabber sampleGrabber,
        IMediaControl mediaControl,
        IBaseFilter sourceFilter,
        IBaseFilter sampleGrabberFilter,
        IBaseFilter nullRenderer,
        SampleGrabberCallback callback,
        int width,
        int height,
        int stride,
        bool bottomUp)
    {
        _graph = graph;
        _graphBuilder = graphBuilder;
        _sampleGrabber = sampleGrabber;
        _mediaControl = mediaControl;
        _sourceFilter = sourceFilter;
        _sampleGrabberFilter = sampleGrabberFilter;
        _nullRenderer = nullRenderer;
        _callback = callback;
        _width = width;
        _height = height;
        _stride = stride;
        _bottomUp = bottomUp;
        _callback.BufferReceived += OnBufferReceived;
    }

    internal event Action<BitmapSource>? FrameReady;

    internal static string ToDeviceId(string devicePath) =>
        DevicePrefix + devicePath;

    internal static bool IsDeviceId(string? deviceId) =>
        !string.IsNullOrWhiteSpace(deviceId) &&
        deviceId.StartsWith(DevicePrefix, StringComparison.OrdinalIgnoreCase);

    internal static string FromDeviceId(string deviceId) =>
        deviceId[DevicePrefix.Length..];

    internal static Task<DirectShowCameraCapture?> CreateAsync(string devicePath) =>
        Task.Run(() => TryCreate(devicePath));

    private static DirectShowCameraCapture? TryCreate(string devicePath)
    {
        DsDevice? device = null;
        IFilterGraph2? graph = null;
        ICaptureGraphBuilder2? graphBuilder = null;
        IBaseFilter? sourceFilter = null;
        IBaseFilter? sampleGrabberFilter = null;
        IBaseFilter? nullRenderer = null;
        ISampleGrabber? sampleGrabber = null;
        SampleGrabberCallback? callback = null;

        try
        {
            device = DsDevice.GetDevicesOfCat(FilterCategory.VideoInputDevice)
                .FirstOrDefault(item => string.Equals(
                    item.DevicePath,
                    devicePath,
                    StringComparison.OrdinalIgnoreCase));
            if (device is null)
            {
                return null;
            }

            graph = (IFilterGraph2)new FilterGraph();
            graphBuilder = (ICaptureGraphBuilder2)new CaptureGraphBuilder2();
            DsError.ThrowExceptionForHR(graphBuilder.SetFiltergraph(graph));
            DsError.ThrowExceptionForHR(graph.AddSourceFilterForMoniker(
                device.Mon,
                null,
                device.Name,
                out sourceFilter));

            sampleGrabber = (ISampleGrabber)new SampleGrabber();
            sampleGrabberFilter = (IBaseFilter)sampleGrabber;
            DsError.ThrowExceptionForHR(graph.AddFilter(
                sampleGrabberFilter,
                "SnapCut camera preview"));
            nullRenderer = (IBaseFilter)new NullRenderer();
            DsError.ThrowExceptionForHR(graph.AddFilter(
                nullRenderer,
                "SnapCut camera sink"));

            // RGB24 lets the graph insert the system color converter when a
            // virtual driver publishes MJPG/YUY2. If the driver rejects the
            // requested subtype, the graph can still negotiate its native
            // media type below.
            var requestedType = new AMMediaType
            {
                majorType = MediaType.Video,
                subType = MediaSubType.RGB24,
                formatType = FormatType.VideoInfo,
            };
            var setTypeResult = sampleGrabber.SetMediaType(requestedType);
            if (setTypeResult < 0)
            {
                sampleGrabber.SetMediaType(new AMMediaType
                {
                    majorType = MediaType.Video,
                    formatType = FormatType.VideoInfo,
                });
            }
            DsUtils.FreeAMMediaType(requestedType);

            var renderResult = graphBuilder.RenderStream(
                PinCategory.Preview,
                MediaType.Video,
                sourceFilter!,
                null,
                sampleGrabberFilter);
            if (renderResult < 0)
            {
                renderResult = graphBuilder.RenderStream(
                    PinCategory.Capture,
                    MediaType.Video,
                    sourceFilter!,
                    null,
                    sampleGrabberFilter);
            }
            DsError.ThrowExceptionForHR(renderResult);
            // Complete the graph after the sample grabber. Without a sink,
            // several virtual-camera drivers keep the upstream pin stopped
            // and no callback is delivered even though Run succeeds.
            _ = graphBuilder.RenderStream(
                Guid.Empty,
                MediaType.Video,
                sampleGrabberFilter,
                null,
                nullRenderer);

            var connectedType = new AMMediaType();
            DsError.ThrowExceptionForHR(
                sampleGrabber.GetConnectedMediaType(connectedType));
            try
            {
                if (connectedType.formatPtr == IntPtr.Zero ||
                    connectedType.formatType != FormatType.VideoInfo)
                {
                    return null;
                }

                var header = Marshal.PtrToStructure<VideoInfoHeader>(
                    connectedType.formatPtr)!;
                var bitmapHeader = header.BmiHeader!;
                var width = Math.Abs(bitmapHeader.Width);
                var height = Math.Abs(bitmapHeader.Height);
                var bitCount = bitmapHeader.BitCount;
                if (width <= 0 || height <= 0 || bitCount != 24)
                {
                    return null;
                }

                var stride = ((width * bitCount + 31) / 32) * 4;
                callback = new SampleGrabberCallback();
                sampleGrabber.SetOneShot(false);
                // Keep a current sample as a fallback for virtual-camera
                // drivers that do not invoke the callback until a buffer is
                // requested. The callback remains the normal fast path.
                sampleGrabber.SetBufferSamples(true);
                DsError.ThrowExceptionForHR(sampleGrabber.SetCallback(callback, 1));
                var capture = new DirectShowCameraCapture(
                    graph,
                    graphBuilder,
                    sampleGrabber,
                    (IMediaControl)graph,
                    sourceFilter,
                    sampleGrabberFilter,
                    nullRenderer,
                    callback,
                    width,
                    height,
                    stride,
                    bitmapHeader.Height > 0);
                graph = null;
                graphBuilder = null;
                sourceFilter = null;
                sampleGrabberFilter = null;
                nullRenderer = null;
                sampleGrabber = null;
                callback = null;
                DsError.ThrowExceptionForHR(capture._mediaControl.Run());
                return capture;
            }
            finally
            {
                DsUtils.FreeAMMediaType(connectedType);
            }
        }
        catch
        {
            return null;
        }
        finally
        {
            device?.Dispose();
            if (callback is not null)
            {
                try { sampleGrabber?.SetCallback(null, 0); } catch { }
            }
            ReleaseCom(sampleGrabberFilter);
            ReleaseCom(nullRenderer);
            ReleaseCom(sampleGrabber);
            ReleaseCom(sourceFilter);
            ReleaseCom(graphBuilder);
            ReleaseCom(graph);
        }
    }

    private void OnBufferReceived(double sampleTime, IntPtr buffer, int length)
    {
        if (_disposed || length <= 0 || length < _stride * _height)
        {
            return;
        }

        lock (_frameSync)
        {
            _pendingBuffer ??= new byte[length];
            if (_pendingBuffer.Length < length)
            {
                _pendingBuffer = new byte[length];
            }
            Marshal.Copy(buffer, _pendingBuffer, 0, length);
            _pendingLength = length;
        }

        if (Interlocked.Exchange(ref _publishScheduled, 1) == 0)
        {
            _ = Task.Run(PublishLatestFrameAsync);
        }
    }

    private Task PublishLatestFrameAsync()
    {
        try
        {
            byte[]? buffer;
            int length;
            lock (_frameSync)
            {
                if (_pendingBuffer is null || _pendingLength == 0)
                {
                    return Task.CompletedTask;
                }

                buffer = _pendingBuffer;
                length = _pendingLength;
                _pendingBuffer = null;
                _pendingLength = 0;
            }

            var imageBytes = new byte[_stride * _height];
            if (_bottomUp)
            {
                for (var row = 0; row < _height; row++)
                {
                    Buffer.BlockCopy(
                        buffer,
                        row * _stride,
                        imageBytes,
                        (_height - row - 1) * _stride,
                        _stride);
                }
            }
            else
            {
                Buffer.BlockCopy(buffer, 0, imageBytes, 0, imageBytes.Length);
            }

            var image = BitmapSource.Create(
                _width,
                _height,
                96,
                96,
                PixelFormats.Bgr24,
                null,
                imageBytes,
                _stride);
            image.Freeze();
            FrameReady?.Invoke(image);
        }
        catch
        {
            // A camera can disappear while recording. Keep the graph alive
            // and let a later callback recover without interrupting capture.
        }
        finally
        {
            Volatile.Write(ref _publishScheduled, 0);
            lock (_frameSync)
            {
                if (!_disposed && _pendingBuffer is not null &&
                    Interlocked.Exchange(ref _publishScheduled, 1) == 0)
                {
                    _ = Task.Run(PublishLatestFrameAsync);
                }
            }
        }

        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;
        try { _sampleGrabber.SetCallback(null, 0); } catch { }
        try { _mediaControl.Stop(); } catch { }
        _callback.BufferReceived -= OnBufferReceived;
        ReleaseCom(_sampleGrabberFilter);
        ReleaseCom(_nullRenderer);
        ReleaseCom(_sourceFilter);
        ReleaseCom(_graphBuilder);
        ReleaseCom(_sampleGrabber);
        ReleaseCom(_graph);
        return ValueTask.CompletedTask;
    }

    private static void ReleaseCom(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            try { Marshal.FinalReleaseComObject(value); } catch { }
        }
    }

    private sealed class SampleGrabberCallback : ISampleGrabberCB
    {
        internal event Action<double, IntPtr, int>? BufferReceived;

        public int SampleCB(double sampleTime, IMediaSample sample) => 0;

        public int BufferCB(double sampleTime, IntPtr buffer, int length)
        {
            BufferReceived?.Invoke(sampleTime, buffer, length);
            return 0;
        }
    }
}
