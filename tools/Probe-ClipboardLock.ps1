#requires -Version 5.1
<#
.SYNOPSIS
    Windows-only, bounded clipboard-lock probe. Never reads or changes content.
.DESCRIPTION
    Run while reproducing the transcription failure. By default it only observes
    the lock window. -TestExclusiveAccess also briefly opens/closes the clipboard
    to verify exclusive access; do not use that switch during transcription.
    OwnerPid identifies the last data owner, not the exclusive-lock holder.
#>
[CmdletBinding()]
param(
    [ValidateRange(1, 120)][int]$Seconds = 20,
    [switch]$TestExclusiveAccess,
    [string]$OutputPath
)
$ErrorActionPreference = 'Stop'
if (-not ('SnapCutClipboardLockProbe' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class SnapCutClipboardLockProbe {
    [DllImport("user32.dll")] public static extern IntPtr GetOpenClipboardWindow();
    [DllImport("user32.dll")] public static extern IntPtr GetClipboardOwner();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll", SetLastError=true)] public static extern bool OpenClipboard(IntPtr owner);
    [DllImport("user32.dll")] public static extern bool CloseClipboard();
    [DllImport("user32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    public static extern IntPtr CreateWindowEx(uint exStyle, string className, string name, uint style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
    [DllImport("user32.dll")] public static extern bool DestroyWindow(IntPtr window);
}
'@
}
function Get-WindowProcessId([IntPtr]$Window) {
    [uint32]$processIdValue = 0
    if ($Window -ne [IntPtr]::Zero) {
        [void][SnapCutClipboardLockProbe]::GetWindowThreadProcessId($Window, [ref]$processIdValue)
    }
    return $processIdValue
}
$events = [Collections.Generic.List[object]]::new()
$watch = [Diagnostics.Stopwatch]::StartNew()
$samples = 0
$failures = 0
$successes = 0
$snapCut = @(Get-Process -Name SnapCut,Screenshot -ErrorAction SilentlyContinue | Select-Object Id,ProcessName,Path)
$probeWindow = [IntPtr]::Zero
if ($TestExclusiveAccess) {
    $probeWindow = [SnapCutClipboardLockProbe]::CreateWindowEx(0, 'STATIC', '', 0, 0, 0, 0, 0,
        [IntPtr]::new(-3), [IntPtr]::Zero, [IntPtr]::Zero, [IntPtr]::Zero)
    if ($probeWindow -eq [IntPtr]::Zero) { throw 'Cannot create clipboard probe window.' }
}
try {
while ($watch.Elapsed.TotalSeconds -lt $Seconds) {
    $lockWindow = [SnapCutClipboardLockProbe]::GetOpenClipboardWindow()
    $lockPid = Get-WindowProcessId $lockWindow
    $ownerPid = Get-WindowProcessId ([SnapCutClipboardLockProbe]::GetClipboardOwner())
    $errorCode = 0
    if ($TestExclusiveAccess) {
        if ([SnapCutClipboardLockProbe]::OpenClipboard($probeWindow)) {
            try { $successes++ }
            finally { [void][SnapCutClipboardLockProbe]::CloseClipboard() }
        }
        else {
            $errorCode = [Runtime.InteropServices.Marshal]::GetLastWin32Error()
            $failures++
            $lockWindow = [SnapCutClipboardLockProbe]::GetOpenClipboardWindow()
            $lockPid = Get-WindowProcessId $lockWindow
        }
    }
    if ($lockWindow -ne [IntPtr]::Zero -or $errorCode -ne 0) {
        $events.Add([pscustomobject]@{
            Milliseconds = [int]$watch.Elapsed.TotalMilliseconds
            LockWindow = $lockWindow.ToInt64(); LockPid = $lockPid
            OwnerPid = $ownerPid; OpenError = $errorCode
        })
    }
    $samples++
    Start-Sleep -Milliseconds 20
}
}
finally {
    if ($probeWindow -ne [IntPtr]::Zero) { [void][SnapCutClipboardLockProbe]::DestroyWindow($probeWindow) }
}
$processes = @($events | Select-Object -ExpandProperty LockPid -Unique | ForEach-Object {
    if ($_ -ne 0) { Get-Process -Id $_ -ErrorAction SilentlyContinue | Select-Object Id,ProcessName,Path }
})
$report = [pscustomobject]@{
    Time = (Get-Date).ToString('o'); Seconds = $Seconds; Samples = $samples
    ActiveProbe = [bool]$TestExclusiveAccess; OpenSuccesses = $successes; OpenFailures = $failures
    SnapCutAtStart = $snapCut; LockProcesses = $processes; Events = $events
    Note = 'No clipboard content was read or written. A data owner is not a lock holder; a lock without a window may have no identifiable PID.'
} | ConvertTo-Json -Depth 6
if ($OutputPath) { $report | Set-Content -LiteralPath $OutputPath -Encoding UTF8 }
$report
