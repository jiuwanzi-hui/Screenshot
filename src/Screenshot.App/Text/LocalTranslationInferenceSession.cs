using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;
using Screenshot.App.Core;

namespace Screenshot.App.Text;

// One bounded, short-lived native worker per installed model. No screenshot text
// leaves loopback, and neither prompts nor inference output are written to disk.
internal sealed class LocalTranslationInferenceSession : IDisposable
{
    internal static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(10);
    private readonly LocalLargeTranslationModelManager _manager;
    private readonly Func<CancellationToken, Task<InferenceDevice?>> _deviceProbe;
    private readonly Func<string, string, Task<string>> _reportStartupFailure;
    private readonly LocalTranslationHardwareCache? _hardwareCache;
    private InferenceHardwareSnapshot? _hardware;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HttpClient _client = new(new SocketsHttpHandler { UseProxy = false })
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };
    private readonly System.Threading.Timer _idleTimer;
    private Process? _process;
    private TranslationWorkerLifetime? _workerLifetime;
    private bool _disposed;
    private Guid? _preparationOwner;
    private TimeSpan _overlappedPreparationElapsed;
    private long _lastUse;
    private int _requestedWorkItems = int.MaxValue;
    private ComputeResources _resources;
    private string? _deviceProbeFailure;
    private LocalInferenceStartupEvidence? _startupOutput;
    private readonly LocalInferenceStartupEvidence _startupHistory = new();
    internal static readonly TimeSpan DeviceProbeTimeout = TimeSpan.FromSeconds(3);
    internal static readonly TimeSpan SilentStartupTimeout = TimeSpan.FromSeconds(8);

    internal int Parallelism { get; private set; } = 1;
    internal string RuntimeDescription { get; private set; } = string.Empty;
    internal TimeSpan PreparationElapsed { get; private set; }

    internal LocalTranslationInferenceSession(LocalLargeTranslationModelManager manager,
        Func<CancellationToken, Task<InferenceDevice?>>? deviceProbe = null,
        Func<string, string, Task<string>>? reportStartupFailure = null)
    {
        _manager = manager;
        _deviceProbe = deviceProbe ?? FindDeviceAsync;
        if (deviceProbe is null)
            _hardwareCache = new(Path.Combine(Path.GetDirectoryName(manager.InstallationDirectory)!, "inference-hardware-b10276.json"));
        _reportStartupFailure = reportStartupFailure ?? ((model, details) => LocalInferenceStartupEvidence.SaveFailureAsync(model, details));
        _idleTimer = BackgroundTimer.Create(_ => UnloadIdle());
        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
    }

    internal async Task<T> RunAsync<T>(
        Func<HttpClient, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken, int workItems = int.MaxValue)
    {
        var waiting = _gate.WaitAsync(cancellationToken);
        if (!waiting.IsCompleted) TranslationProgress.Report("正在等待前一次本机翻译释放计算资源，可取消本次操作。 ");
        await waiting.ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var overlapped = _preparationOwner is not null ? _overlappedPreparationElapsed : TimeSpan.Zero;
            _preparationOwner = null;
            _requestedWorkItems = Math.Max(1, workItems);
            _idleTimer.Change(Timeout.Infinite, Timeout.Infinite);
            var preparation = Stopwatch.StartNew();
            await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
            PreparationElapsed = preparation.Elapsed + overlapped;
            _overlappedPreparationElapsed = TimeSpan.Zero;
            return await operation(_client, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Aborting HTTP alone can leave native generation queued. Stop the
            // owned worker so a cancelled capture cannot delay the next one.
            StopWorker();
            throw;
        }
        catch
        {
            StopWorker();
            throw;
        }
        finally
        {
            if (!_disposed && _process is not null)
            {
                _lastUse = Environment.TickCount64;
                _idleTimer.Change(IdleTimeout, Timeout.InfiniteTimeSpan);
            }
            _gate.Release();
        }
    }

    // Only an explicit translate action may overlap loading with OCR. Reuse a
    // validated hardware profile: never run Vulkan discovery or a CPU model
    // alongside OCR, and do not displace an active translation.
    internal async Task PrepareDuringRecognitionAsync(Guid owner, CancellationToken token)
    {
        if (_hardwareCache is null || !await _gate.WaitAsync(0, token).ConfigureAwait(false)) return;
        try
        {
            if (_disposed || _process is { HasExited: false } || !_manager.GetStatus().IsInstalled) return;
            _hardware = await Task.Run(WindowsInferenceHardware.Capture, token).ConfigureAwait(false);
            var profile = await _hardwareCache.ReadAsync(_hardware.Fingerprint, token).ConfigureAwait(false);
            if (profile?.Device is not { IsDiscrete: true } cached || profile.ProbeFailure is not null) return;
            _resources = await Task.Run(() => ComputeResourceBudget.Capture(profile.Cpu), token).ConfigureAwait(false);
            var device = RefreshCachedDevice(cached, _hardware.Adapters);
            if (device is null || _resources.AvailableRamBytes < 8L * 1024 * 1024 * 1024 ||
                _resources.CpuBusyFraction >= .7 || _resources.PhysicalCores < 6) return;
            device = device with { Parallelism = LocalTranslationStartupPolicy.Parallelism(
                device, _manager.ModelDownloadSize, _resources, int.MaxValue, _manager.IsLightweight) };
            var options = LocalTranslationStartupPolicy.Select(device, _manager.ModelDownloadSize,
                _resources.AvailableRamBytes, _manager.IsLightweight);
            if (!options.DirectOffload) return;
            _idleTimer.Change(Timeout.Infinite, Timeout.Infinite);
            _preparationOwner = owner;
            RuntimeDescription = $"显卡 {device.Name}（已与文字识别同时准备）";
            var timer = Stopwatch.StartNew();
            await StartWorkerAsync(cpuOnly: false, device, options, token).ConfigureAwait(false);
            _overlappedPreparationElapsed = timer.Elapsed;
        }
        catch (OperationCanceledException) { StopWorker(); }
        catch (Exception exception) when (exception is InvalidOperationException or TimeoutException or
            IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            // Optional preparation does not turn an OCR failure into a model
            // failure; the actual translation retains normal fallback/status.
            StopWorker();
        }
        finally
        {
            if (!_disposed && _process is not null)
            {
                _lastUse = Environment.TickCount64;
                _idleTimer.Change(IdleTimeout, Timeout.InfiniteTimeSpan);
            }
            _gate.Release();
        }
    }

    internal async Task CancelUnusedPreparationAsync(Guid owner)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_preparationOwner == owner) { StopWorker(); _preparationOwner = null; }
        }
        finally { _gate.Release(); }
    }

    private async Task EnsureStartedAsync(CancellationToken cancellationToken)
    {
        if (_process is { HasExited: false })
        {
            return;
        }

        _startupHistory.Reset();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(LocalLargeModelTranslationProvider.StartupTimeout);
        try
        {
            await StartAvailableWorkerAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw await StartupFailureAsync("离线模型启动总时限已到，尚未确认加载完成。").ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidOperationException or TimeoutException or
            System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        {
            throw await StartupFailureAsync(exception.Message, exception).ConfigureAwait(false);
        }
    }

    private async Task<InvalidOperationException> StartupFailureAsync(string message, Exception? cause = null)
    {
        var details = $"{_manager.DisplayName}\n{RuntimeDescription}\n{message}\n" +
            $"Resources: {_resources}\n" +
            _startupHistory.Snapshot() + _startupOutput?.Snapshot();
        StopWorker();
        _startupHistory.Stop();
        var log = await _reportStartupFailure(_manager.DisplayName, details).ConfigureAwait(false);
        return new InvalidOperationException($"{_manager.DisplayName} 启动失败：{message}\n{RuntimeDescription}\n{log}", cause);
    }

    private async Task StartAvailableWorkerAsync(CancellationToken cancellationToken)
    {
        TranslationProgress.Report($"正在准备 {_manager.DisplayName} 的本机运行配置…");
        TranslationHardwareProfile? profile = null;
        if (_hardwareCache is not null)
        {
            _hardware = await Task.Run(WindowsInferenceHardware.Capture, cancellationToken).ConfigureAwait(false);
            profile = await _hardwareCache.ReadAsync(_hardware.Fingerprint, cancellationToken).ConfigureAwait(false);
        }
        _resources = await Task.Run(() => ComputeResourceBudget.Capture(profile?.Cpu), cancellationToken).ConfigureAwait(false);
        var availableRam = _resources.AvailableRamBytes;
        if (LocalTranslationStartupPolicy.ResourceFailure(_manager.ModelDownloadSize, availableRam, cpuOnly: false) is { } failure)
            throw new InvalidOperationException(failure);
        string? fallbackReason = null;
        // Model size does not determine the execution device. The lightweight
        // model can fit GPUs which cannot hold the larger model.
        _deviceProbeFailure = null;
        InferenceDevice? device;
        if (profile is not null)
        {
            device = RefreshCachedDevice(profile.Device, _hardware!.Adapters);
            _deviceProbeFailure = profile.ProbeFailure is null ? null : "沿用上次探测结果：" + profile.ProbeFailure;
            _startupHistory.Append("Hardware configuration cache reused; Vulkan probe skipped.");
        }
        else
        {
            device = await _deviceProbe(cancellationToken).ConfigureAwait(false);
            if (_hardwareCache is not null && _hardware is not null)
                await _hardwareCache.SaveAsync(new(_hardware.Fingerprint, _resources, device, _deviceProbeFailure,
                    _deviceProbeFailure is null ? null : DateTimeOffset.UtcNow.AddMinutes(10)), cancellationToken).ConfigureAwait(false);
        }
        if (device is not null)
            device = device with { Parallelism = LocalTranslationStartupPolicy.Parallelism(
                device, _manager.ModelDownloadSize, _resources, _requestedWorkItems, _manager.IsLightweight) };
        if (device is not null)
        {
            var retryWithFit = true;
            var startupOptions = LocalTranslationStartupPolicy.Select(device, _manager.ModelDownloadSize,
                availableRam, _manager.IsLightweight);
            RuntimeDescription = device is null ? "GPU／CPU 自动适配" :
                startupOptions.DirectOffload ? $"显卡 {device.Name}" :
                device.IsDiscrete ? $"可用显存 {device.FreeMemoryMiB / 1024d:F1} GB，GPU／CPU 自动适配；可能较慢" :
                "集成显卡／CPU 适配，翻译可能较慢；可在设置 → 翻译选择在线翻译或轻量模型";
            if (startupOptions.DirectOffload)
            {
                try
                {
                    await StartWorkerAsync(cpuOnly: false, device, startupOptions, cancellationToken).ConfigureAwait(false);
                    return;
                }
                catch (Exception exception) when (exception is InvalidOperationException or TimeoutException)
                {
                    // Free memory can change after probing. Retry the same GPU
                    // with native fitting before considering CPU fallback.
                    _startupHistory.Append("GPU direct: " + exception.Message + "\n" + _startupOutput?.Snapshot());
                    StopWorker();
                    RuntimeDescription = "显卡快速加载失败，正在按当前显存重新分配；可能较慢";
                    // A silent loader/driver hang is not a VRAM fitting problem.
                    // Do not spend another full attempt initializing the same backend.
                    retryWithFit = exception is not TimeoutException;
                    fallbackReason = exception.Message;
                }
            }
            if (retryWithFit) try
            {
                await StartWorkerAsync(cpuOnly: false, device, default, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (Exception exception) when (exception is InvalidOperationException or TimeoutException)
            {
                _startupHistory.Append("GPU fitted: " + exception.Message + "\n" + _startupOutput?.Snapshot());
                StopWorker();
                fallbackReason = exception.Message;
            }
        }
        if (LocalTranslationStartupPolicy.ResourceFailure(_manager.ModelDownloadSize,
                LocalTranslationStartupPolicy.AvailableRamBytes(), cpuOnly: true) is { } cpuFailure)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(fallbackReason) ? cpuFailure :
                $"显卡加载失败：{fallbackReason}\n{cpuFailure}");
        if (device is not null && _hardwareCache is not null && _hardware is not null)
            await _hardwareCache.SaveAsync(new(_hardware.Fingerprint, _resources, device, fallbackReason,
                DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);
        RuntimeDescription = (device is null ? (_deviceProbeFailure ?? "推理程序未列出可用的 Vulkan 显卡") + "，使用 CPU；可能较慢" : "显卡加载失败，已回退 CPU；可能较慢") +
            "；可在设置 → 翻译选择在线翻译或其他离线模型";
        if (fallbackReason is not null) RuntimeDescription += $"（显卡失败原因：{fallbackReason}）";
        var recoveryEvidence = (profile is null && _deviceProbeFailure is not null || fallbackReason is not null)
            ? _startupHistory.Snapshot() : null;
        await StartWorkerAsync(cpuOnly: true, null, default, cancellationToken).ConfigureAwait(false);
        if (recoveryEvidence is not null)
            await _reportStartupFailure(_manager.DisplayName,
                $"显卡路径失败，已由独立 CPU 运行库恢复。\n{RuntimeDescription}\n{recoveryEvidence}").ConfigureAwait(false);
    }

    private async Task StartWorkerAsync(bool cpuOnly, InferenceDevice? device,
        LocalTranslationStartupOptions startupOptions, CancellationToken cancellationToken)
    {
        StopWorker();
        Parallelism = device?.Parallelism ?? 1;
        var threads = ComputeResourceBudget.TranslationThreads(_resources, gpu: !cpuOnly, batch: false);
        var batchThreads = ComputeResourceBudget.TranslationThreads(_resources, gpu: !cpuOnly, batch: true);
        RuntimeDescription += $"；{threads} 个 CPU 线程，{Parallelism} 路并发";
        TranslationProgress.Report($"正在加载 {_manager.DisplayName} · {RuntimeDescription}");
        using var startup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        // Give CPU loading the remaining operation budget rather than imposing
        // an additional 60s limit. The outer deadline still bounds all attempts.
        startup.CancelAfter(cpuOnly ? LocalLargeModelTranslationProvider.StartupTimeout :
            LocalLargeModelTranslationProvider.StartupTimeout / 3);
        var startupToken = startup.Token;
        var prepareWatch = Stopwatch.StartNew();
        var executable = await _manager.PrepareInferenceRuntimeAsync(cpuOnly, startupToken).ConfigureAwait(false);
        _startupHistory.Append($"Verified runtime: {(cpuOnly ? "CPU (no GPU backend)" : "Vulkan")}; prepare={prepareWatch.ElapsedMilliseconds} ms; executable={executable}");
        using var portReservation = new TcpListener(IPAddress.Loopback, 0);
        portReservation.Start();
        var port = ((IPEndPoint)portReservation.LocalEndpoint).Port;
        portReservation.Stop();
        var key = Guid.NewGuid().ToString("N");
        // HttpClient.BaseAddress cannot be changed after its first request.
        _endpoint = new Uri($"http://127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}/");
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        var start = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = Path.GetDirectoryName(executable)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        LocalInferenceRuntime.ConfigureEnvironment(start, cpuOnly);
        string[] arguments =
        [
            "-m", _manager.ModelPath, "--host", "127.0.0.1", "--port",
            port.ToString(CultureInfo.InvariantCulture), "--api-key", key,
            "-c", (4096 * Parallelism).ToString(CultureInfo.InvariantCulture), "-np",
            Parallelism.ToString(CultureInfo.InvariantCulture), "-t",
            threads.ToString(CultureInfo.InvariantCulture),
            "-tb", batchThreads.ToString(CultureInfo.InvariantCulture),
            "-b", cpuOnly ? (batchThreads >= 8 ? "512" : "256") : "512",
            "-ub", cpuOnly ? (batchThreads >= 8 ? "256" : "128") : "256", "--poll", "0", "--poll-batch", "0",
            "--no-webui", "--log-verbosity", "3", "--cache-ram", "0",
            // We send the model's exact raw translation template to /completion.
            // Its typed source/target chat fields are incompatible with the
            // server's generic chat parser and must not be auto-probed at startup.
            "--no-jinja", "--chat-template", "chatml",
            // Skip the extra fitting pass only with measured memory headroom.
            // CPU mode already passed our RAM check and has no GPU layers to fit.
            "--fit", cpuOnly || startupOptions.DirectOffload ? "off" : "on",
            "--split-mode", "none",
            "--no-warmup",
        ];
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }
        if (cpuOnly)
        {
            start.ArgumentList.Add("--device"); start.ArgumentList.Add("none");
            start.ArgumentList.Add("-ngl"); start.ArgumentList.Add("0");
        }
        else if (device is not null)
        {
            start.ArgumentList.Add("--device"); start.ArgumentList.Add(device.Id);
        }
        if (startupOptions.DirectOffload)
        {
            start.ArgumentList.Add("-ngl"); start.ArgumentList.Add("all");
        }
        if (startupOptions.BufferedLoading)
        {
            start.ArgumentList.Add("--load-mode"); start.ArgumentList.Add("none");
        }

        var startupWatch = Stopwatch.StartNew();
        // Starting a native executable can incur antivirus/loader waits.
        // Keep creation off the dispatcher even when a cached probe completes synchronously.
        using (var creationDeadline = CancellationTokenSource.CreateLinkedTokenSource(startupToken))
        {
            creationDeadline.CancelAfter(SilentStartupTimeout);
            try { (_process, _workerLifetime) = await TranslationWorkerLifetime.StartAsync(start, creationDeadline.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            { throw new TimeoutException("本机推理进程创建超时，尚未进入模型加载；若进程迟到启动，会自动结束。"); }
        }
        _startupHistory.Append($"Worker created: pid={_process.Id}; mode={(cpuOnly ? "CPU" : "Vulkan")}");
        var evidence = _startupOutput = new LocalInferenceStartupEvidence();
        // A bounded startup-only buffer retains driver/model errors without
        // retaining prompts, generated text or continuous operation logs.
        _process.OutputDataReceived += (_, args) => evidence.Append(args.Data?.Replace(key, "[redacted]", StringComparison.Ordinal));
        _process.ErrorDataReceived += (_, args) => evidence.Append(args.Data?.Replace(key, "[redacted]", StringComparison.Ordinal));
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (startup.IsCancellationRequested)
                throw new TimeoutException(cpuOnly ? "CPU 加载离线模型超时，可取消后重试，或在设置 → 翻译选择在线翻译。" : "显卡加载超时。");
            if (_process.HasExited)
            {
                // Drain asynchronous pipe callbacks before snapshotting the
                // failure, including the native loader's final error line.
                await _process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                throw new InvalidOperationException($"离线翻译程序启动失败（退出码 0x{_process.ExitCode:X8}），请校验模型及显卡驱动。");
            }
            if (startupWatch.Elapsed >= SilentStartupTimeout && string.IsNullOrWhiteSpace(evidence.Snapshot()))
            {
                _startupHistory.Append(ProcessState(_process));
                throw new TimeoutException($"本机推理程序启动 {SilentStartupTimeout.TotalSeconds:0} 秒仍无输出，尚未确认进入模型加载；已结束卡住的进程。");
            }

            try
            {
                using var response = await _client.GetAsync(new Uri(_endpoint, "health"), startupToken)
                    .ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    evidence.Stop();
                    _startupHistory.Stop();
                    return;
                }
            }
            catch (HttpRequestException)
            {
                // The port is not listening while native model loading starts.
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException(cpuOnly ? "CPU 加载离线模型超时，尚未收到本机推理程序就绪响应。" : "显卡加载超时。");
            }
            await Task.Delay(startupWatch.Elapsed < TimeSpan.FromSeconds(5) ? 50 : 200,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private Uri? _endpoint;

    internal static void ConfigureCpuEnvironment(ProcessStartInfo start)
    {
        LocalInferenceRuntime.ConfigureEnvironment(start, cpuOnly: true);
    }

    internal sealed record InferenceDevice(string Id, int Parallelism)
    {
        public bool IsDiscrete { get; init; }
        public long FreeMemoryMiB { get; init; }
        public string Name { get; init; } = string.Empty;
    }

    internal static InferenceDevice? RefreshCachedDevice(InferenceDevice? cached, IReadOnlyList<InferenceAdapter> adapters)
    {
        if (cached is null) return null;
        var matching = adapters.Where(adapter => adapter.Name.Equals(cached.Name, StringComparison.OrdinalIgnoreCase)).ToArray();
        // Mirrored/virtual adapters may repeat a GPU's name. Use the smallest
        // matching allowance, never a stale or optimistic free-memory number.
        var budget = matching.Length > 0 ? matching.Min(adapter => adapter.BudgetMiB) : 0;
        return cached with { FreeMemoryMiB = budget,
            Parallelism = !cached.IsDiscrete ? 1 : budget switch { >= 10000 => 8, >= 6000 => 4, >= 2700 => 2, _ => 1 } };
    }

    internal static InferenceDevice? SelectDevice(string listing)
    {
        return Regex.Matches(listing, @"(?m)^\s*(Vulkan\d+):\s*(.+)\s+\((\d+) MiB, (\d+) MiB free\)",
                RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100))
            .Select(match => new
            {
                Id = match.Groups[1].Value,
                Name = match.Groups[2].Value.Trim(),
                Discrete = match.Groups[2].Value.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ||
                    match.Groups[2].Value.Contains("Radeon RX", StringComparison.OrdinalIgnoreCase) ||
                    match.Groups[2].Value.Contains("Radeon PRO", StringComparison.OrdinalIgnoreCase) ||
                    match.Groups[2].Value.Contains("Arc", StringComparison.OrdinalIgnoreCase) &&
                    Regex.IsMatch(match.Groups[2].Value, @"\b[AB][357]\d{2}\b", RegexOptions.CultureInvariant,
                        TimeSpan.FromMilliseconds(100)),
                Free = long.Parse(match.Groups[4].Value, CultureInfo.InvariantCulture),
            })
            .OrderByDescending(item => item.Discrete).ThenByDescending(item => item.Free)
            .Select(item => new InferenceDevice(item.Id, !item.Discrete ? 1 : item.Free switch
            { >= 10000 => 8, >= 6000 => 4, >= 2700 => 2, _ => 1 })
            { IsDiscrete = item.Discrete, FreeMemoryMiB = item.Free, Name = item.Name }).FirstOrDefault();
    }

    private async Task<InferenceDevice?> FindDeviceAsync(CancellationToken cancellationToken)
    {
        // Query only when a translation actually needs a worker. Shared-memory
        // GPUs and CPU fallback keep one slot; discrete GPUs get a bounded pool.
        var prepareWatch = Stopwatch.StartNew();
        var executable = await _manager.PrepareInferenceRuntimeAsync(cpuOnly: false, cancellationToken).ConfigureAwait(false);
        _startupHistory.Append($"Vulkan runtime verified in {prepareWatch.ElapsedMilliseconds} ms: {executable}");
        var start = new ProcessStartInfo(executable, "--list-devices")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(executable)!,
        };
        LocalInferenceRuntime.ConfigureEnvironment(start, cpuOnly: false);
        // Error-only loader messages help distinguish missing drivers/layers from
        // a native process which never reached Vulkan at all; not a global setting.
        start.Environment["VK_LOADER_DEBUG"] = "error";
        return await ProbeDeviceAsync(start, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<InferenceDevice?> ProbeDeviceAsync(ProcessStartInfo start, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(DeviceProbeTimeout);
        Process? probe = null;
        TranslationWorkerLifetime? lifetime = null;
        var probeOutput = new LocalInferenceStartupEvidence();
        try
        {
            (probe, lifetime) = await TranslationWorkerLifetime.StartAsync(start, timeout.Token).ConfigureAwait(false);
            probe.OutputDataReceived += (_, args) => probeOutput.Append(args.Data);
            probe.ErrorDataReceived += (_, args) => probeOutput.Append(args.Data);
            probe.BeginOutputReadLine();
            probe.BeginErrorReadLine();
            await probe.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            var listing = probeOutput.Snapshot();
            if (probe.ExitCode != 0)
            {
                _deviceProbeFailure = $"Vulkan 显卡探测失败（退出码 0x{probe.ExitCode:X8}），设备状态未确认";
                _startupHistory.Append(_deviceProbeFailure);
                return null;
            }
            return SelectDevice(listing);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _deviceProbeFailure = $"Vulkan 显卡探测超过 {DeviceProbeTimeout.TotalSeconds:0} 秒，设备状态未确认";
            _startupHistory.Append(_deviceProbeFailure);
            return null;
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            _deviceProbeFailure = "Vulkan 显卡探测失败：" + exception.Message;
            _startupHistory.Append(_deviceProbeFailure);
            return null;
        }
        finally
        {
            // Even a hung/crashed probe can have emitted the driver and stage
            // responsible. Preserve its bounded partial output before killing it.
            _startupHistory.Append("Vulkan probe:\n" + probeOutput.Snapshot() + "\n" +
                (probe is null ? "Native process creation did not complete." : ProcessState(probe)));
            probeOutput.Stop();
            lifetime?.Dispose();
            try { if (probe is { HasExited: false }) probe.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
            finally { probe?.Dispose(); }
        }
    }

    private static string ProcessState(Process process)
    {
        try
        {
            if (process.HasExited) return $"Native pid={process.Id}; exit=0x{process.ExitCode:X8}";
            process.Refresh();
            var modules = string.Join(", ", process.Modules.Cast<ProcessModule>().Select(module => module.ModuleName).Take(48));
            return $"Native pid={process.Id}; cpu={process.TotalProcessorTime.TotalMilliseconds:F0} ms; " +
                $"private={process.PrivateMemorySize64 / 1048576d:F1} MiB; modules={modules}";
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        { return "Native state unavailable: " + exception.Message; }
    }

    internal async Task<string> CompleteAsync(string prompt, int tokenLimit, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_endpoint!, "completion"))
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                prompt,
                n_predict = tokenLimit,
                temperature = _manager.IsLightweight ? 0.7 : 0,
                top_k = _manager.IsLightweight ? 20 : 40,
                top_p = _manager.IsLightweight ? 0.6 : 0.95,
                repeat_penalty = _manager.IsLightweight ? 1.05 : 1.0,
                cache_prompt = true,
                stream = false,
            }), System.Text.Encoding.UTF8, "application/json"),
        };
        using var response = await _client.SendAsync(request, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var document = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false), cancellationToken: token)
            .ConfigureAwait(false);
        var result = document.RootElement;
        if (result.TryGetProperty("truncated", out var truncated) && truncated.GetBoolean() ||
            result.TryGetProperty("stop_type", out var stop) && stop.GetString() == "limit" ||
            result.TryGetProperty("stopped_limit", out var limit) && limit.GetBoolean())
        {
            throw new InvalidOperationException("译文长度超过模型容量，请缩小选区后重试；未将截断内容作为译文。");
        }
        return result.GetProperty("content").GetString()?.Trim() ?? string.Empty;
    }

    private void UnloadIdle()
    {
        if (!_gate.Wait(0))
        {
            return;
        }
        try
        {
            if (_disposed || _process is null) return;
            // A callback queued before the latest request must not unload a
            // just-used worker. The gate also keeps teardown out of inference.
            var remaining = IdleTimeout - TimeSpan.FromMilliseconds(Environment.TickCount64 - _lastUse);
            if (remaining > TimeSpan.Zero) _idleTimer.Change(remaining, Timeout.InfiniteTimeSpan);
            else StopWorker();
        }
        finally { _gate.Release(); }
    }

    private void StopWorker()
    {
        _overlappedPreparationElapsed = TimeSpan.Zero;
        _startupOutput?.Stop();
        _startupOutput = null;
        Interlocked.Exchange(ref _workerLifetime, null)?.Dispose();
        var process = Interlocked.Exchange(ref _process, null);
        if (process is null) return;
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
        finally { process.Dispose(); }
    }

    private void OnProcessExit(object? sender, EventArgs e) => StopWorker();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _idleTimer.Dispose();
        StopWorker();
        _client.Dispose();
        AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
    }
}
