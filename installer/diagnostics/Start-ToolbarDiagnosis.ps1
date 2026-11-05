param([ValidateSet('Automatic', 'Software', 'Hardware')][string]$RenderingMode = 'Automatic')
$ErrorActionPreference = 'Stop'
$exe = Join-Path $PSScriptRoot 'SnapCut.exe'
if (-not (Test-Path -LiteralPath $exe)) { throw '请把诊断脚本和本次 SnapCut.exe 放在同一目录。' }
if (Get-Process SnapCut,Screenshot -ErrorAction SilentlyContinue) {
    throw '请先保存当前工作，在托盘退出所有 SnapCut，然后重新双击 Start-ToolbarDiagnosis.cmd。不会强制终止录制或关闭窗口。'
}
$diagnostics = Join-Path $PSScriptRoot 'ScreenshotData\Diagnostics'
$session = Join-Path $diagnostics ('ToolbarTrace-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0,6))
New-Item -ItemType Directory -Path $session -Force | Out-Null
Write-Host '正在记录基础硬件信息，完成后才启动 SnapCut，不在截图时运行硬件扫描。'
$hardware = [ordered]@{
    capturedAt = [DateTimeOffset]::Now.ToString('o')
    version = [Diagnostics.FileVersionInfo]::GetVersionInfo($exe).ProductVersion
    executableSha256 = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash
    qpcFrequency = [Diagnostics.Stopwatch]::Frequency
    requestedRenderingMode = $RenderingMode
    cpu = @(); os = $null; gpu = @(); displays = @(); errors = @()
}
try { $hardware.cpu = @(Get-CimInstance Win32_Processor | Select-Object Name,NumberOfCores,NumberOfLogicalProcessors,MaxClockSpeed,CurrentClockSpeed,LoadPercentage) }
catch { $hardware.errors += 'CPU probe: ' + $_.Exception.Message }
try { $hardware.os = Get-CimInstance Win32_OperatingSystem | Select-Object Caption,Version,BuildNumber,TotalVisibleMemorySize,FreePhysicalMemory }
catch { $hardware.errors += 'OS probe: ' + $_.Exception.Message }
try { $hardware.gpu = @(Get-CimInstance Win32_VideoController | Select-Object Name,DriverVersion,CurrentHorizontalResolution,CurrentVerticalResolution,CurrentRefreshRate) }
catch { $hardware.errors += 'GPU probe: ' + $_.Exception.Message }
try {
    Add-Type -AssemblyName System.Windows.Forms
    $hardware.displays = @([Windows.Forms.Screen]::AllScreens | ForEach-Object {
        [ordered]@{ Device=$_.DeviceName; Primary=$_.Primary; Bounds=$_.Bounds.ToString(); WorkingArea=$_.WorkingArea.ToString() }
    })
} catch { $hardware.errors += 'Display probe: ' + $_.Exception.Message }
$hardware | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $session 'hardware.json') -Encoding UTF8
$marker = Join-Path $diagnostics 'capture-timing.enabled'
$markerText = [ordered]@{ directory=$session; expiresUtc=[DateTimeOffset]::UtcNow.AddMinutes(15).ToString('o') } | ConvertTo-Json
$hadMarker = Test-Path -LiteralPath $marker
[byte[]]$oldMarker = if ($hadMarker) { [IO.File]::ReadAllBytes($marker) } else { @() }
try {
    [IO.File]::WriteAllText($marker,$markerText,[Text.UTF8Encoding]::new($false))
    $hardware.launchQpc = [Diagnostics.Stopwatch]::GetTimestamp()
    $launchArguments = @('--background')
    if ($RenderingMode -ne 'Automatic') { $launchArguments += '--' + $RenderingMode.ToLowerInvariant() + '-rendering' }
    $launched = Start-Process -FilePath $exe -WorkingDirectory $PSScriptRoot -ArgumentList $launchArguments -WindowStyle Hidden -PassThru
    $hardware.launchReturnedQpc = [Diagnostics.Stopwatch]::GetTimestamp()
    $hardware.launchPid = $launched.Id
    $hardware | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $session 'hardware.json') -Encoding UTF8
    Write-Host ''
    Write-Host '诊断已启动（最多 15 分钟），日志保存在：' $session
    Write-Host "本次界面渲染模式：$RenderingMode。只作用于本次启动，不改变 OCR、翻译或录制编码的设备设置。"
    Write-Host '请保持平时相同的主题、屏幕和录制配置：'
    Write-Host '1. 等待 15 秒，截图框选一次，观察工具栏，然后取消。'
    Write-Host '2. 马上连续截图一次；再等待 15 秒，截图一次。'
    Write-Host '3. 等待 15 秒，框选录屏区域，观察工具栏，然后取消；不必真的开始录制。'
    Write-Host '4. 在 SnapCut 托盘退出软件，返回这里按 Enter，生成诊断 ZIP。'
    Write-Host '只记录阶段耗时、资源计数和显示配置，不记录截图像素、识别正文或键盘输入，不自动上传。'
    [void](Read-Host '完成后按 Enter')
    & (Join-Path $PSScriptRoot 'Export-ToolbarDiagnosis.ps1') -Directory $session
}
finally {
    # Restore only our own marker. Do not remove another investigation's state.
    if ((Test-Path -LiteralPath $marker) -and [IO.File]::ReadAllText($marker) -ceq $markerText) {
        if ($hadMarker) { [IO.File]::WriteAllBytes($marker,$oldMarker) }
        else { Remove-Item -LiteralPath $marker }
    }
}
