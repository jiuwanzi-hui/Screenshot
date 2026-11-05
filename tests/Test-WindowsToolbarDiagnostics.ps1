$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$export = Join-Path $repo 'installer\diagnostics\Export-ToolbarDiagnosis.ps1'
foreach ($script in @($export, (Join-Path $repo 'installer\diagnostics\Start-ToolbarDiagnosis.ps1'))) {
    $tokens = $null; $errors = $null
    [void][Management.Automation.Language.Parser]::ParseFile($script,[ref]$tokens,[ref]$errors)
    if ($errors.Count) { throw ($errors | Out-String) }
}
$root = [IO.Path]::GetFullPath((Join-Path $repo 'artifacts\diagnostic-collector-tests'))
$run = Join-Path $root ([Guid]::NewGuid().ToString('N'))
$session = Join-Path $run 'ToolbarTrace-test'
New-Item -ItemType Directory -Path $session -Force | Out-Null
try {
    @'
2026-09-18 21:00:00.0000000 +08:00 pid=100 qpc=1000 tid=1 mark stage=process-entry qpcFrequency=1000 role=resident cpuMs=10
2026-09-18 21:00:00.1000000 +08:00 pid=100 qpc=1100 tid=1 mark stage=resident-selection-start selection=1 action=RegionCapture cpuMs=11
2026-09-18 21:00:00.2000000 +08:00 pid=100 qpc=1200 tid=1 mark stage=resident-selection-mouse-up window=1 cpuMs=12
2026-09-18 21:00:00.7000000 +08:00 pid=100 qpc=1700 tid=1 mark stage=resident-handoff-finished selection=1 transferred=True cpuMs=13
2026-09-18 21:00:01.0000000 +08:00 pid=100 qpc=2000 tid=1 mark stage=resident-selection-start selection=2 action=VideoRecording cpuMs=14
2026-09-18 21:00:01.1000000 +08:00 pid=100 qpc=2100 tid=1 mark stage=resident-handoff-finished selection=2 transferred=False cpuMs=15
'@ | Set-Content -LiteralPath (Join-Path $session 'capture-timing-100.log') -Encoding UTF8
    @'
2026-09-18 21:00:00.3000000 +08:00 pid=200 qpc=1300 tid=1 mark stage=process-entry qpcFrequency=1000 role=worker cpuMs=20
2026-09-18 21:00:00.6000000 +08:00 pid=200 qpc=1600 tid=1 mark stage=editor-presented-ack cpuMs=30
'@ | Set-Content -LiteralPath (Join-Path $session 'capture-timing-200.log') -Encoding UTF8
    'do not collect' | Set-Content -LiteralPath (Join-Path $session 'settings.json') -Encoding UTF8
    'do not collect' | Set-Content -LiteralPath (Join-Path $session 'screenshot.png') -Encoding UTF8
    & $export -Directory $session | Out-Null
    $summary = Get-Content -LiteralPath (Join-Path $session 'summary.md') -Raw -Encoding UTF8
    if ($summary -notmatch '500 ms' -or $summary -notmatch 'PID 200' -or
        [regex]::Matches($summary,'工具栏交接完成：500 ms').Count -ne 1) { throw 'Cross-process handoff duration or cancellation handling is wrong.' }
    $timeline = @(Import-Csv -LiteralPath (Join-Path $session 'timeline.csv'))
    if ($timeline.Count -ne 8 -or $timeline[3].Process -ne '200') { throw 'Timeline is not sorted across processes.' }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::OpenRead($session + '.zip')
    try {
        $names = @($zip.Entries.FullName)
        if ($names.Count -ne 4 -or $names -contains 'settings.json' -or $names -contains 'screenshot.png') {
            throw 'Collector included unapproved data.'
        }
    } finally { $zip.Dispose() }
    'Toolbar diagnostic collector: parsing, 500 ms cross-process timing, cancellation and private-file exclusion passed.'
}
finally {
    $resolved = (Resolve-Path -LiteralPath $run).Path
    if (-not $resolved.StartsWith($root + '\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe test cleanup target.' }
    Remove-Item -LiteralPath $resolved -Recurse
}
