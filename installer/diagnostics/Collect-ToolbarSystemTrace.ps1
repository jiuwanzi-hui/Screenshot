param(
    [ValidateRange(5, 60)][int]$Seconds = 25,
    [string]$OutputDirectory = [Environment]::GetFolderPath('Desktop'),
    [switch]$ValidateOnly,
    [switch]$Worker
)

function Invoke-ToolbarTraceCollectorWorker {
    param([string]$ScriptPath, [string]$Folder, [int]$Duration)

    $arguments = @('-NoProfile','-ExecutionPolicy','Bypass','-File',('"' + $ScriptPath + '"'),
        '-Worker','-Seconds',([string]$Duration),'-OutputDirectory',('"' + $Folder + '"'))
    $collector = $null
    try {
        $collector = Start-Process -FilePath (Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe') `
            -ArgumentList $arguments -WindowStyle Hidden -PassThru `
            -RedirectStandardOutput (Join-Path $Folder 'collector-output.txt') `
            -RedirectStandardError (Join-Path $Folder 'collector-error.txt')
        # Windows PowerShell 5.1 may lose access to the exit code if this
        # Process object has not retained a handle before the child exits.
        # Keep the worker independent: -Wait would also wait for descendants.
        [void]$collector.Handle
        Write-Host "正在采集 $Duration 秒，请现在复现卡顿；随后自动保存。关闭本窗口后，后台仍会按时结束采集。"
        $collector.WaitForExit()
        $exitCode = $collector.ExitCode
        if ($null -eq $exitCode) { throw '无法读取采集进程的退出状态。' }
        if ($exitCode -ne 0) { throw "采集进程退出码：$exitCode。" }

        # A successful process exit alone is not proof that a trace was saved.
        $etlPath = Join-Path $Folder 'toolbar-startup.etl'
        $zipPath = $Folder + '.zip'
        if (!(Test-Path -LiteralPath $etlPath -PathType Leaf) -or
            (Get-Item -LiteralPath $etlPath).Length -eq 0) { throw '未生成有效 ETL 文件。' }
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $archive = [IO.Compression.ZipFile]::OpenRead($zipPath)
        try {
            foreach ($name in @('toolbar-startup.etl', 'recorder.txt', 'trace-info.json')) {
                $entry = $archive.GetEntry($name)
                if ($null -eq $entry -or $entry.Length -eq 0) { throw "压缩包缺少有效文件：$name。" }
            }
            if ($archive.GetEntry('toolbar-startup.etl').Length -ne (Get-Item -LiteralPath $etlPath).Length) {
                throw '压缩包中的 ETL 大小与原文件不一致。'
            }
        } finally { $archive.Dispose() }
        return $zipPath
    }
    catch {
        $reason = $_.Exception.Message
        $details = foreach ($name in @('collector-error.txt', 'recorder.txt')) {
            $path = Join-Path $Folder $name
            if (Test-Path -LiteralPath $path -PathType Leaf) {
                $content = (Get-Content -LiteralPath $path -Raw -ErrorAction SilentlyContinue)
                if (![string]::IsNullOrWhiteSpace($content)) { "$name`r`n$content" }
            }
        }
        throw "系统跟踪未能完成：$reason`r`n$($details -join "`r`n")`r`n原始文件保留在：$Folder"
    }
    finally {
        if ($null -ne $collector) { $collector.Dispose() }
    }
}

$ErrorActionPreference = 'Stop'
$wpr = Join-Path $env:SystemRoot 'System32\wpr.exe'
$profile = (Join-Path $PSScriptRoot 'ToolbarStartup.wprp') + '!ToolbarStartup'
if (!(Test-Path -LiteralPath $wpr)) { throw 'Windows Performance Recorder (wpr.exe) is unavailable.' }
if ($ValidateOnly) {
    & $wpr -profiledetails $profile
    if ($LASTEXITCODE -ne 0) { throw 'WPR profile validation failed.' }
    return
}
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
if (!([Security.Principal.WindowsPrincipal]$identity).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw '请右键 Collect-ToolbarSystemTrace.cmd，选择“以管理员身份运行”。Windows 内核线程等待跟踪需要管理员权限。'
}

if (!$Worker) {
Write-Host '本次采集 Windows 线程调用栈、等待和桌面合成事件，用于分析工具栏冷启动。'
Write-Host '可能包含其他进程名称、程序路径和窗口元数据；不主动截屏、读取键盘文本或采集网络内容，不自动上传。'
Write-Host '配置缓冲区合计 192 MiB，记录时长固定；保存 ETL 时还会产生额外临时开销。'
Write-Host '无需更换 SnapCut。请先等待其空闲内存释放，回到需要截图的软件。'
[void](Read-Host '准备好后按 Enter；倒数 5 秒开始。随后做一次冷截图、取消，再立即截图一次；等待自动保存')
for ($left = 5; $left -gt 0; $left--) { Write-Host $left; Start-Sleep -Seconds 1 }
$root = [IO.Path]::GetFullPath($OutputDirectory)
$folder = Join-Path $root ('ToolbarSystemTrace-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0,6))
New-Item -ItemType Directory -Path $folder -ErrorAction Stop | Out-Null
# The bounded recorder lives outside this console. Closing the console does
# not leave kernel tracing running: the child still stops/saves its own session.
$zip = Invoke-ToolbarTraceCollectorWorker -ScriptPath $PSCommandPath -Folder $folder -Duration $Seconds
Write-Host ('已生成：' + $zip)
return
}
$folder = [IO.Path]::GetFullPath($OutputDirectory)
if (!(Test-Path -LiteralPath $folder -PathType Container)) { throw 'Recorder output directory is missing.' }
$session = 'SnapCutToolbar-' + [Guid]::NewGuid().ToString('N')
$etl = Join-Path $folder 'toolbar-startup.etl'
$started = $false
try {
    # A private instance never cancels or stops another WPR user's recording.
    # Also clean up a partially started instance if WPR reports a startup error.
    $started = $true
    $startOutput = & $wpr -start $profile -instancename $session 2>&1
    $startOutput | Out-File -LiteralPath (Join-Path $folder 'recorder.txt') -Encoding utf8
    if ($LASTEXITCODE -ne 0) { throw 'WPR 未能启动，可能有跟踪冲突；未停止其他跟踪。详情见 recorder.txt。' }
    Write-Host "开始采集，$Seconds 秒后自动停止，请现在复现卡顿。"
    # No screen polling, process scanning, hashing or forced GC during timing.
    Start-Sleep -Seconds $Seconds
    Write-Host '正在保存调用栈，请稍候。'
    $stopOutput = & $wpr -stop $etl -instancename $session 2>&1
    $stopCode = $LASTEXITCODE
    $stopOutput | Out-File -LiteralPath (Join-Path $folder 'recorder.txt') -Encoding utf8 -Append
    if ($stopCode -ne 0) { throw '保存系统跟踪失败，详情见 recorder.txt。' }
    $started = $false
    if (!(Test-Path -LiteralPath $etl) -or (Get-Item -LiteralPath $etl).Length -eq 0) { throw '未生成有效 ETL 文件。' }
    [pscustomobject]@{ RecordedAt = (Get-Date).ToString('o'); Seconds = $Seconds;
        Instance = $session; BufferMiB = 192; SystemVersion = [Environment]::OSVersion.VersionString;
        TraceSha256 = (Get-FileHash -LiteralPath $etl -Algorithm SHA256).Hash } |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $folder 'trace-info.json') -Encoding utf8
    $zip = $folder + '.zip'
    Compress-Archive -LiteralPath $etl,(Join-Path $folder 'recorder.txt'),(Join-Path $folder 'trace-info.json') -DestinationPath $zip
    Write-Host "已生成：$zip"
    Write-Host '请提供这个 ZIP。该文件不同于原来只包含阶段耗时的 ToolbarTrace。'
}
finally {
    if ($started) {
        # Error/cancellation cleanup is restricted to the instance we started.
        & $wpr -cancel -instancename $session 2>&1 | Out-File -LiteralPath (Join-Path $folder 'recorder.txt') -Encoding utf8 -Append
    }
}
