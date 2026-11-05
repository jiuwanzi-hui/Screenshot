param([Parameter(Mandatory)][string]$Directory)
$ErrorActionPreference = 'Stop'
$Directory = (Resolve-Path -LiteralPath $Directory).Path
$logs = @(Get-ChildItem -LiteralPath $Directory -File -Filter 'capture-timing-*.log')
if ($logs.Count -eq 0) { throw '没有捕获到本次诊断日志。请确认运行的是本次诊断版，退出旧进程后重新开始；不要把这次当作已完成诊断。' }
$frequency = $null
$events = @(foreach ($file in $logs) {
    foreach ($line in Get-Content -LiteralPath $file.FullName -Encoding UTF8) {
        if ($line -match 'qpcFrequency=(\d+)') { $frequency = [double]$Matches[1] }
        if ($line -match 'pid=(\d+) qpc=(\d+) tid=(\d+) (?:mark|begin|exception) stage=([^ ]+)(.*)$') {
            [pscustomobject]@{ Process=[int]$Matches[1]; Qpc=[long]$Matches[2]; Thread=[int]$Matches[3]; Stage=$Matches[4]; Details=$Matches[5].Trim() }
        }
    }
})
if (-not $frequency -or $events.Count -eq 0) { throw '日志缺少时间基准或阶段记录，无法可靠计算耗时。' }
$events = @($events | Sort-Object Qpc)
$zero = $events[0].Qpc
$events | Select-Object @{n='TimeMs';e={[math]::Round(($_.Qpc-$zero)*1000/$frequency,3)}},Process,Thread,Stage,Details |
    Export-Csv -LiteralPath (Join-Path $Directory 'timeline.csv') -NoTypeInformation -Encoding UTF8
$report = [Collections.Generic.List[string]]::new()
$report.Add('# SnapCut 工具栏诊断')
$report.Add('')
$report.Add('下面时间从本次应用收到鼠标松开消息开始；尚不能单凭此表认定 CPU、磁盘、GPU 或驱动是根因。完整跨进程顺序、CPU 累计时间、缺页计数及内存在 timeline.csv 和原始日志中。')
$report.Add('')
$index = 0
foreach ($start in @($events | Where-Object Stage -eq 'resident-selection-start')) {
    $index++
    $next = $events | Where-Object { $_.Process -eq $start.Process -and $_.Stage -eq 'resident-selection-start' -and $_.Qpc -gt $start.Qpc } | Select-Object -First 1
    $end = if ($next) { $next.Qpc } else { [long]::MaxValue }
    $range = @($events | Where-Object { $_.Qpc -ge $start.Qpc -and $_.Qpc -lt $end })
    $up = $range | Where-Object { $_.Process -eq $start.Process -and $_.Stage -eq 'resident-selection-mouse-up' } | Select-Object -First 1
    $done = $range | Where-Object { $_.Process -eq $start.Process -and $_.Stage -eq 'resident-handoff-finished' -and $_.Details -match 'transferred=True' } | Select-Object -First 1
    $report.Add("## 框选 $index")
    $report.Add($start.Details)
    if ($up -and $done -and $done.Qpc -ge $up.Qpc) {
        $duration = [math]::Round(($done.Qpc-$up.Qpc)*1000/$frequency,1)
        $report.Add("松开消息 → 工具栏交接完成：$duration ms")
    } else { $report.Add('未得到完整的松开／呈现确认（可能取消或尚未完成），不填造耗时。') }
    $reference = if ($up) { $up.Qpc } else { $start.Qpc }
    foreach ($event in $range) {
        if ($done -and $event.Qpc -gt $done.Qpc) { continue }
        $ms = [math]::Round(($event.Qpc-$reference)*1000/$frequency,1)
        $report.Add("- $ms ms · PID $($event.Process) · $($event.Stage) · $($event.Details)")
    }
    $report.Add('')
}
if ($index -eq 0) { $report.Add('未记录到由常驻进程发起的真实框选。请按说明再次复现；进程启动日志不能代替工具栏呈现证据。') }
$report | Set-Content -LiteralPath (Join-Path $Directory 'summary.md') -Encoding UTF8
# Only collect this run's explicitly named diagnostic files, never ScreenshotData as a whole.
$files = @($logs.FullName) + @(Join-Path $Directory 'timeline.csv') + @(Join-Path $Directory 'summary.md')
$hardware = Join-Path $Directory 'hardware.json'
if (Test-Path -LiteralPath $hardware) { $files += $hardware }
$zip = $Directory.TrimEnd('\') + '.zip'
Compress-Archive -LiteralPath $files -DestinationPath $zip -Force
Write-Host '诊断文件已生成，请发回这个 ZIP：'
Write-Output $zip
