param(
    [Parameter(Mandatory = $true)]
    [string]$PublishDirectory,
    [Parameter(Mandatory = $true)]
    [string]$DestinationPath,
    [string]$SevenZipPath,
    [long]$MaximumBytes = 100000000
)

$ErrorActionPreference = 'Stop'
$sourceDirectory = (Resolve-Path -LiteralPath $PublishDirectory).Path
$archivePath = [IO.Path]::GetFullPath($DestinationPath)
if (-not $SevenZipPath) {
    $command = Get-Command 7z.exe, 7za.exe -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($command) { $SevenZipPath = $command.Source }
    elseif (Test-Path -LiteralPath 'C:\Program Files\7-Zip\7z.exe') {
        $SevenZipPath = 'C:\Program Files\7-Zip\7z.exe'
    }
}
if (-not $SevenZipPath -or -not (Test-Path -LiteralPath $SevenZipPath)) {
    throw 'Install 7-Zip/NanaZip or supply -SevenZipPath to build the Gitee portable package.'
}
if (-not (Test-Path -LiteralPath (Join-Path $sourceDirectory 'SnapCut.exe'))) {
    throw 'The publish directory does not contain SnapCut.exe.'
}
if ($MaximumBytes -le 0) { throw 'MaximumBytes must be positive.' }
New-Item -ItemType Directory -Path (Split-Path -Parent $archivePath) -Force | Out-Null
# Always create a fresh archive: updating an old archive can retain removed files.
$temporaryArchive = $archivePath + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
Push-Location -LiteralPath $sourceDirectory
try {
    # A 64 MiB dictionary keeps extraction practical on low-memory machines.
    # Preserve the executable bytes; compression happens only at distribution time.
    $compressionArguments = @('a', '-t7z', '-mx=9', '-m0=LZMA2', '-md=64m',
        '-mfb=64', '-ms=on', '-mmt=2', $temporaryArchive,
        (Join-Path $sourceDirectory '*'), '-xr!ScreenshotData', '-xr!TranslationModels', '-xr!*.pdb', '-bsp0')
    & $SevenZipPath @compressionArguments
    if ($LASTEXITCODE -ne 0) { throw '7z portable compression failed.' }
    & $SevenZipPath t $temporaryArchive -bsp0
    if ($LASTEXITCODE -ne 0) { throw '7z portable integrity check failed.' }
    $archiveSize = (Get-Item -LiteralPath $temporaryArchive).Length
    if ($archiveSize -ge $MaximumBytes) {
        throw "Gitee portable package is $archiveSize bytes; it must be below $MaximumBytes bytes."
    }
    Move-Item -LiteralPath $temporaryArchive -Destination $archivePath -Force
}
finally {
    Pop-Location
    if (Test-Path -LiteralPath $temporaryArchive) {
        Remove-Item -LiteralPath $temporaryArchive -Force
    }
}
Get-Item -LiteralPath $archivePath
