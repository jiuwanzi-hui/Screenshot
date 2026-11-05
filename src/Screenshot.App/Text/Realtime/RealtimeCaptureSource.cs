using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Screenshot.App.Capture;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;

namespace Screenshot.App.Text.Realtime;

internal interface IRealtimeCaptureSource : IDisposable
{
    Task<RealtimeFrame?> CaptureAsync(CancellationToken token);
    void Suspend() { }
}

internal sealed class RealtimeCaptureSource : IRealtimeCaptureSource
{
    private readonly nint _window;
    private readonly bool _preferBorderless;
    private readonly ScreenRegion? _fixedRegion;
    private readonly ScreenRegion _monitorBounds;
    private GraphicsCaptureItem? _item;
    private IDirect3DDevice? _device;
    private Direct3D11CaptureFramePool? _pool;
    private GraphicsCaptureSession? _session;
    private byte[]? _pixels;
    private Windows.Graphics.SizeInt32 _size;
    private bool _disposed;
    private bool _initialized;
    internal string? Notice { get; private set; }

    internal RealtimeCaptureSource(nint window, ScreenRegion? region, bool preferBorderless = false)
    {
        _window = window;
        _preferBorderless = preferBorderless;
        _fixedRegion = region;
        var bounds = region ?? RealtimeWindowTarget.Bounds(window);
        var monitor = System.Windows.Forms.Screen.FromRectangle(new(bounds.X, bounds.Y, bounds.Width, bounds.Height));
        _monitorBounds = new(monitor.Bounds.X, monitor.Bounds.Y, monitor.Bounds.Width, monitor.Bounds.Height);
    }

    private void Initialize()
    {
        _initialized = true;
        Notice = null;
        var region = _fixedRegion;
        var bounds = region ?? RealtimeWindowTarget.Bounds(_window);
        try
        {
            if (!GraphicsCaptureSession.IsSupported() ||
                (_window == 0 && region is { } r && ScreenRegion.Intersect(r, _monitorBounds) != r))
                throw new NotSupportedException();
            _device = CreateDevice();
            _item = CreateItem(_window, _window != 0 ? 0 : MonitorFromPoint(new(bounds.X, bounds.Y), 2));
            _size = _item.Size;
            _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(_device,
                DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, _size);
            _session = _pool.CreateCaptureSession(_item);
            _session.IsCursorCaptureEnabled = false;
            // No permission prompt here. Windows retains its indicator if
            // borderless capture is unavailable or not permitted on this PC.
            if (_preferBorderless) TryPreferBorderless(_session);
            _session.StartCapture();
        }
        catch (Exception exception) when (exception is COMException or NotSupportedException or ArgumentException)
        {
            ReleaseCapture();
            // A desktop fallback would recapture our visible translations,
            // replace the original text, and repeatedly invalidate the result.
            if (_window != 0)
                throw new InvalidOperationException("无法读取目标窗口，请尝试窗口化或无边框模式后重新框选。", exception);
            Notice = "当前显示环境使用兼容屏幕采集；游戏可尝试窗口化或无边框模式。";
        }
    }

    public async Task<RealtimeFrame?> CaptureAsync(CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        token.ThrowIfCancellationRequested();
        if (!_initialized) Initialize();
        var bounds = _fixedRegion ?? RealtimeWindowTarget.Bounds(_window);
        if (bounds.IsEmpty) throw new InvalidOperationException("目标窗口已关闭。");
        var captureBounds = _window != 0 ? RealtimeWindowTarget.Bounds(_window) : _monitorBounds;
        if (_window != 0 && ScreenRegion.Intersect(bounds, captureBounds) != bounds)
            throw new InvalidOperationException("目标窗口已移出翻译选区，请重新框选。");
        if (_pool is null) return new(ScreenCaptureService.CaptureIncludingLayeredWindows(bounds), bounds);
        Direct3D11CaptureFrame? latest = null;
        try
        {
            // Drain the bounded GPU pool; no queued screenshots or PNG encoding.
            while (_pool.TryGetNextFrame() is { } frame) { latest?.Dispose(); latest = frame; }
            if (latest is null) return null;
            if (latest.ContentSize.Width != _size.Width || latest.ContentSize.Height != _size.Height)
            {
                _size = latest.ContentSize;
                latest.Dispose(); latest = null;
                if (_size.Width > 0 && _size.Height > 0)
                    _pool.Recreate(_device!, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, _size);
                return null;
            }
            using var software = await SoftwareBitmap.CreateCopyFromSurfaceAsync(latest.Surface,
                BitmapAlphaMode.Ignore).AsTask(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            var length = checked(software.PixelWidth * software.PixelHeight * 4);
            if (_pixels?.Length != length) _pixels = new byte[length];
            software.CopyToBuffer(_pixels.AsBuffer());
            var crop = _fixedRegion is { } area
                ? new Rectangle(area.X - captureBounds.X, area.Y - captureBounds.Y, area.Width, area.Height)
                : new Rectangle(0, 0, software.PixelWidth, software.PixelHeight);
            var availableCrop = Rectangle.Intersect(crop, new(0, 0, software.PixelWidth, software.PixelHeight));
            if (_window != 0 && _fixedRegion is not null && availableCrop != crop)
                throw new InvalidOperationException("目标窗口已移出翻译选区，请重新框选。");
            crop = availableCrop;
            if (crop.Width <= 0 || crop.Height <= 0) return null;
            var bitmap = new Bitmap(crop.Width, crop.Height, PixelFormat.Format32bppArgb);
            try
            {
                var data = bitmap.LockBits(new(0, 0, crop.Width, crop.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                try
                {
                    for (var y = 0; y < crop.Height; y++)
                        Marshal.Copy(_pixels, ((crop.Y + y) * software.PixelWidth + crop.X) * 4,
                            data.Scan0 + y * data.Stride, crop.Width * 4);
                }
                finally { bitmap.UnlockBits(data); }
                return new(new CapturedImage(bitmap, bounds), bounds);
            }
            catch { bitmap.Dispose(); throw; }
        }
        finally { latest?.Dispose(); }
    }

    private static void TryPreferBorderless(GraphicsCaptureSession session)
    {
        // IGraphicsCaptureSession3 is newer than our Windows 10 SDK target.
        // Query it optionally; access denied leaves the system indicator intact.
        var id = new Guid("F2CDD966-22AE-5EA1-9596-3A289344C3BE");
        var inspectable = ((WinRT.IWinRTObject)session).NativeObject.ThisPtr;
        if (Marshal.QueryInterface(inspectable, ref id, out var session3) < 0) return;
        try
        {
            var method = Marshal.ReadIntPtr(Marshal.ReadIntPtr(session3), 7 * IntPtr.Size);
            _ = Marshal.GetDelegateForFunctionPointer<SetBorderRequired>(method)(session3, 0);
        }
        finally { Marshal.Release(session3); GC.KeepAlive(session); }
    }
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetBorderRequired(nint session, byte required);

    private void ReleaseCapture()
    {
        _session?.Dispose(); _session = null;
        _pool?.Dispose(); _pool = null;
        _device?.Dispose(); _device = null;
        _item = null;
        _pixels = null;
    }

    public void Dispose() { if (_disposed) return; _disposed = true; ReleaseCapture(); }
    // Called only by the capture loop, never concurrently with a surface copy.
    public void Suspend() { ReleaseCapture(); _initialized = false; }

    private static IDirect3DDevice CreateDevice()
    {
        nint device = 0, context = 0, dxgi = 0, inspectable = 0;
        try
        {
            Marshal.ThrowExceptionForHR(D3D11CreateDevice(0, 1, 0, 0x20, 0, 0, 7, out device, out _, out context));
            var iid = new Guid("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(device, ref iid, out dxgi));
            Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi, out inspectable));
            return WinRT.MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
        }
        finally
        {
            if (inspectable != 0) Marshal.Release(inspectable);
            if (dxgi != 0) Marshal.Release(dxgi);
            if (context != 0) Marshal.Release(context);
            if (device != 0) Marshal.Release(device);
        }
    }

    private static GraphicsCaptureItem CreateItem(nint window, nint monitor)
    {
        const string name = "Windows.Graphics.Capture.GraphicsCaptureItem";
        nint className = 0, factory = 0, item = 0;
        object? interop = null;
        try
        {
            Marshal.ThrowExceptionForHR(WindowsCreateString(name, name.Length, out className));
            var factoryId = typeof(ICaptureItemInterop).GUID;
            Marshal.ThrowExceptionForHR(RoGetActivationFactory(className, ref factoryId, out factory));
            interop = Marshal.GetObjectForIUnknown(factory);
            var captureId = new Guid("79c3f95b-31f7-4ec2-a464-632ef5d30760");
            if (monitor == 0) ((ICaptureItemInterop)interop).CreateForWindow(window, ref captureId, out item);
            else ((ICaptureItemInterop)interop).CreateForMonitor(monitor, ref captureId, out item);
            return WinRT.MarshalInterface<GraphicsCaptureItem>.FromAbi(item);
        }
        finally
        {
            if (item != 0) Marshal.Release(item);
            if (interop is not null) Marshal.ReleaseComObject(interop);
            if (factory != 0) Marshal.Release(factory);
            if (className != 0) _ = WindowsDeleteString(className);
        }
    }

    [ComImport, Guid("3628e81b-3cac-4c60-b7f4-23ce0e0c3356"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ICaptureItemInterop
    {
        void CreateForWindow(nint window, ref Guid iid, out nint result);
        void CreateForMonitor(nint monitor, ref Guid iid, out nint result);
    }
    [DllImport("d3d11.dll")] private static extern int D3D11CreateDevice(nint adapter, int driverType, nint software, uint flags, nint levels, uint count, uint sdkVersion, out nint device, out int level, out nint context);
    [DllImport("d3d11.dll")] private static extern int CreateDirect3D11DeviceFromDXGIDevice(nint device, out nint result);
    [DllImport("combase.dll", CharSet = CharSet.Unicode)] private static extern int WindowsCreateString(string value, int length, out nint result);
    [DllImport("combase.dll")] private static extern int WindowsDeleteString(nint value);
    [DllImport("combase.dll")] private static extern int RoGetActivationFactory(nint name, ref Guid iid, out nint factory);
    [DllImport("user32.dll")] private static extern nint MonitorFromPoint(Point point, uint flags);
}

internal static class RealtimeWindowTarget
{
    internal static nint FromRegion(ScreenRegion region)
    {
        var window = ForegroundWindowCaptureService.GetRootWindowHandle(
            CaptureTargetWindowResolver.FindUnderlyingWindow(region));
        return window != 0 && ScreenRegion.Intersect(region, Bounds(window)) == region ? window : 0;
    }


    internal static bool IsRegionVisible(nint window, ScreenRegion region) =>
        IsAvailable(window) && ForegroundWindowCaptureService.GetRootWindowHandle(
            CaptureTargetWindowResolver.FindUnderlyingWindow(region)) == window;
    internal static nint Foreground => GetForegroundWindow();
    internal static bool IsExternal(nint window)
    {
        _ = GetWindowThreadProcessId(window, out var pid);
        var bounds = Bounds(window);
        return window != 0 && pid != Environment.ProcessId && IsWindowVisible(window) && bounds.Width >= 100 && bounds.Height >= 80;
    }
    internal sealed record Choice(nint Handle, string Title);
    internal static IReadOnlyList<Choice> ListWindows()
    {
        var choices = new List<Choice>();
        _ = EnumWindows((handle, _) =>
        {
            if (!IsExternal(handle)) return true;
            var title = new char[512];
            var length = GetWindowText(handle, title, title.Length);
            if (length > 0) choices.Add(new(handle, new string(title, 0, length)));
            return true;
        }, 0);
        return choices;
    }
    internal static void Activate(nint window)
    {
        if (IsIconic(window)) _ = ShowWindow(window, 9);
        _ = SetForegroundWindow(window);
    }
    internal static bool IsAvailable(nint window)
    {
        if (!IsWindow(window)) throw new InvalidOperationException("目标窗口已关闭，采集已结束。");
        return !IsIconic(window);
    }
    internal static bool IsForeground(nint window)
    {
        var foreground = GetForegroundWindow();
        return foreground == window || GetAncestor(foreground, 3) == GetAncestor(window, 3);
    }
    internal static ScreenRegion Bounds(nint window)
    {
        if (!IsWindow(window)) return default;
        if (DwmGetWindowAttribute(window, 9, out var rect, Marshal.SizeOf<NativeRect>()) != 0 && !GetWindowRect(window, out rect)) return default;
        return new(rect.Left, rect.Top, Math.Max(0, rect.Right - rect.Left), Math.Max(0, rect.Bottom - rect.Top));
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    private delegate bool EnumCallback(nint window, nint data);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumCallback callback, nint data);
    [DllImport("user32.dll", EntryPoint = "GetWindowTextW", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint window, [Out] char[] text, int count);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint window, int command);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint pid);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out NativeRect rect);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint window, int attribute, out NativeRect rect, int size);
}
