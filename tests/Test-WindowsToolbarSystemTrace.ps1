param([string]$EvidenceDirectory)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$source = Join-Path $repo 'installer\diagnostics\Collect-ToolbarSystemTrace.ps1'
$tokens = $null; $errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile($source, [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw ($errors | Out-String) }
# Load the actual production parent/child boundary without asking for admin
# rights or enabling a system recording in the test runner.
$function = $ast.Find({ param($node)
    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
    $node.Name -eq 'Invoke-ToolbarTraceCollectorWorker'
}, $false)
if ($null -eq $function) { throw 'Collector process function missing.' }
. ([scriptblock]::Create($function.Extent.Text))
if (!$EvidenceDirectory) {
    $EvidenceDirectory = Join-Path $repo ('artifacts\collector-regression-' + [Guid]::NewGuid().ToString('N'))
}
$root = [IO.Path]::GetFullPath($EvidenceDirectory)
New-Item -ItemType Directory -Path $root -Force | Out-Null
$child = Join-Path $root '模拟 collector worker.ps1'
$stub = @'
param([switch]$Worker, [int]$Seconds, [string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
if (!$Worker) { throw 'Worker argument lost.' }
if ($Seconds -eq 2) {
    'native-error-marker: WPR code 123' | Set-Content -LiteralPath (Join-Path $OutputDirectory 'recorder.txt')
    [Console]::Error.WriteLine('worker-error-marker')
    exit 7
}
if ($Seconds -eq 3) { throw 'worker-throw-marker' }
if ($Seconds -eq 4) { exit 0 }
$etl = Join-Path $OutputDirectory 'toolbar-startup.etl'
[IO.File]::WriteAllBytes($etl, [byte[]]@(1,2,3,4))
if ($Seconds -eq 5) { [IO.File]::WriteAllBytes($etl, [byte[]]@()); exit 0 }
$recorder = Join-Path $OutputDirectory 'recorder.txt'
$info = Join-Path $OutputDirectory 'trace-info.json'
'saved' | Set-Content -LiteralPath $recorder
'{"test":true}' | Set-Content -LiteralPath $info
if ($Seconds -eq 6) {
    Compress-Archive -LiteralPath $recorder, $info -DestinationPath ($OutputDirectory + '.zip')
    exit 0
}
if ($Seconds -eq 7) { 'invalid zip' | Set-Content -LiteralPath ($OutputDirectory + '.zip'); exit 0 }
Compress-Archive -LiteralPath $etl, $recorder, $info -DestinationPath ($OutputDirectory + '.zip')
exit 0
'@
[IO.File]::WriteAllText($child, $stub, [Text.UTF8Encoding]::new($true))
$results = @()
foreach ($case in 1..7) {
    $folder = Join-Path $root ("中文 path $case")
    New-Item -ItemType Directory -Path $folder | Out-Null
    $failure = $null
    try { $zip = Invoke-ToolbarTraceCollectorWorker -ScriptPath $child -Folder $folder -Duration $case }
    catch { $failure = $_.Exception.Message }
    if ($case -eq 1) {
        if ($failure -or $zip -ne ($folder + '.zip')) { throw "Successful child misreported: $failure" }
    } else {
        if (!$failure) { throw "Case $case incorrectly reported success." }
        if ($case -eq 2 -and ($failure -notmatch '7' -or $failure -notmatch 'worker-error-marker' -or
            $failure -notmatch 'native-error-marker')) { throw "Original failure details lost: $failure" }
        if ($case -eq 3 -and $failure -notmatch 'worker-throw-marker') { throw "Unhandled child error lost: $failure" }
    }
    $results += [pscustomobject]@{ Case = $case; Passed = $true; Failure = $failure }
}
$results | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $root 'results.json') -Encoding utf8
Write-Output "Toolbar system collector: 7 process/artifact cases passed under PowerShell $($PSVersionTable.PSVersion)."
