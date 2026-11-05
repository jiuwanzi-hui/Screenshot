param(
    [string]$ArchivePath,
    [string]$OutputDirectory = (Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts\translation-runtime')
)

$ErrorActionPreference = 'Stop'
$runtimeName = 'llama-b10276-bin-win-vulkan-x64.zip'
$runtimeSize = 34096096
$runtimeHash = 'e9f0f8cd772258a1bbc3c1f39bedbb63b2ce176ddfd1d2eecdd2be65406eba70'
$destination = Join-Path ([IO.Path]::GetFullPath($OutputDirectory)) $runtimeName
function Test-RuntimeArchive([string]$Path) {
    return (Test-Path -LiteralPath $Path -PathType Leaf) -and
        (Get-Item -LiteralPath $Path).Length -eq $runtimeSize -and
        (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -eq $runtimeHash
}

if (-not (Test-RuntimeArchive $destination)) {
    if (-not $ArchivePath) {
        $repositoryRoot = Split-Path -Parent $PSScriptRoot
        $existingArchive = Join-Path $repositoryRoot "src\Screenshot.App\bin\Release\net8.0-windows10.0.19041.0\TranslationModels\TranslateGemma-4B-Q4-K-M\$runtimeName"
        if (Test-RuntimeArchive $existingArchive) { $ArchivePath = $existingArchive }
    }
    New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
    if ($ArchivePath) {
        if (-not (Test-RuntimeArchive $ArchivePath)) { throw 'The supplied translation runtime failed SHA-256 validation.' }
        Copy-Item -LiteralPath $ArchivePath -Destination $destination -Force
    } else {
        # Build-time dependency only. Customers receive this archive inside
        # both Windows packages and do not download it from GitHub at runtime.
        $download = "$destination.part"
        Invoke-WebRequest -Uri "https://github.com/ggml-org/llama.cpp/releases/download/b10276/$runtimeName" `
            -OutFile $download -TimeoutSec 120 -MaximumRetryCount 2
        if (-not (Test-RuntimeArchive $download)) { throw 'The downloaded translation runtime failed SHA-256 validation.' }
        Move-Item -LiteralPath $download -Destination $destination -Force
    }
}
Write-Output $destination
