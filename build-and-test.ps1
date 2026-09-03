#requires -Version 5.1
<#
.SYNOPSIS
    Builds the Release output into
    src\Screenshot.App\bin\Release\net8.0-windows10.0.19041.0 and runs the
    full test suite, writing everything to build-and-test.log.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\build-and-test.ps1
#>
[CmdletBinding()]
param(
    [switch]$SkipTests
)

$ErrorActionPreference = 'Continue'
Set-Location -Path $PSScriptRoot

$log = Join-Path $PSScriptRoot 'build-and-test.log'
$output = Join-Path $PSScriptRoot 'src\Screenshot.App\bin\Release\net8.0-windows10.0.19041.0'
$lines = New-Object System.Collections.Generic.List[string]

function Add-Section {
    param([string]$Title)
    $lines.Add('')
    $lines.Add('=== ' + $Title + ' ===')
    Write-Host ''
    Write-Host ('=== ' + $Title + ' ===') -ForegroundColor Cyan
}

function Invoke-Step {
    param([string]$Title, [string[]]$Arguments)
    Add-Section $Title
    $lines.Add('dotnet ' + ($Arguments -join ' '))
    $result = & dotnet @Arguments 2>&1 | ForEach-Object { $_.ToString() }
    $exit = $LASTEXITCODE
    foreach ($line in $result) {
        $lines.Add($line)
        Write-Host $line
    }
    $lines.Add('EXIT=' + $exit)
    Write-Host ('EXIT=' + $exit)
    return $exit
}

Add-Section 'environment'
$lines.Add('date: ' + (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'))
$lines.Add('sdk : ' + (& dotnet --version 2>&1))

# A running instance keeps the exe locked and fails the copy step.
Get-Process -Name 'Screenshot','SnapCut' -ErrorAction SilentlyContinue |
    Stop-Process -Force -ErrorAction SilentlyContinue

$buildExit = Invoke-Step 'build Release' @(
    'build',
    'src\Screenshot.App\Screenshot.App.csproj',
    '-c', 'Release',
    '--nologo'
)

$testExit = 0
if (-not $SkipTests -and $buildExit -eq 0) {
    $testExit = Invoke-Step 'test Release' @(
        'test',
        'tests\Screenshot.App.Tests\Screenshot.App.Tests.csproj',
        '-c', 'Release',
        '--nologo',
        '--verbosity', 'minimal'
    )
}

Add-Section 'result'
$lines.Add('build exit : ' + $buildExit)
$lines.Add('test  exit : ' + $testExit)
$lines.Add('output dir : ' + $output)
if (Test-Path $output) {
    $exe = Join-Path $output 'SnapCut.exe'
    if (Test-Path $exe) {
        $item = Get-Item $exe
        $lines.Add('SnapCut.exe : ' + $item.Length + ' bytes, ' +
            $item.LastWriteTime.ToString('yyyy-MM-dd HH:mm:ss'))
    }
    else {
        $lines.Add('SnapCut.exe : MISSING')
    }
}
else {
    $lines.Add('output dir : MISSING')
}

$lines | Set-Content -Path $log -Encoding UTF8
Write-Host ''
Write-Host ('日志已写入 ' + $log) -ForegroundColor Green

if ($buildExit -ne 0) { exit $buildExit }
exit $testExit
