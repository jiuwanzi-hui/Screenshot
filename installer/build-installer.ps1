param(
    [string]$Version = "2.6.0",
    [switch]$SkipTrackedManifestUpdate
)

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$appProject = Join-Path $repositoryRoot "src\Screenshot.App\Screenshot.App.csproj"
$publishDirectory = Join-Path $repositoryRoot "artifacts\publish\win-x64"
$installerOutputDirectory = Join-Path $PSScriptRoot "dist"
$setupScript = Join-Path $PSScriptRoot "Screenshot.iss"
$innoCompilerCandidates = @(
    (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe"),
    "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
    "C:\Program Files\Inno Setup 6\ISCC.exe"
)
$innoCompiler = $innoCompilerCandidates |
    Where-Object { Test-Path -LiteralPath $_ } |
    Select-Object -First 1

if (-not $innoCompiler) {
    throw "Inno Setup 6 was not found. Install JRSoftware.InnoSetup first."
}

foreach ($directory in @($publishDirectory, $installerOutputDirectory)) {
    $resolvedParent = [IO.Path]::GetFullPath((Split-Path -Parent $directory))
    $resolvedDirectory = [IO.Path]::GetFullPath($directory)
    if (-not $resolvedDirectory.StartsWith($resolvedParent, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clean an unexpected directory: $resolvedDirectory"
    }

    if (Test-Path -LiteralPath $resolvedDirectory) {
        Remove-Item -LiteralPath $resolvedDirectory -Recurse -Force
    }
}

dotnet publish $appProject `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -p:Version=$Version `
    -o $publishDirectory `
    --nologo

if ($LASTEXITCODE -ne 0) {
    throw "Screenshot publish failed."
}

& $innoCompiler "/DAppVersion=$Version" $setupScript
if ($LASTEXITCODE -ne 0) {
    throw "Installer compilation failed."
}

$installerPath = Join-Path `
    $installerOutputDirectory `
    "SnapCut-Setup-$Version-win-x64.exe"
if (-not (Test-Path -LiteralPath $installerPath)) {
    throw "Installer was not generated: $installerPath"
}

$portablePath = Join-Path `
    $installerOutputDirectory `
    "SnapCut-Portable-$Version-win-x64.zip"
Compress-Archive `
    -Path (Join-Path $publishDirectory "*") `
    -DestinationPath $portablePath `
    -CompressionLevel Optimal `
    -Force

if (-not (Test-Path -LiteralPath $portablePath)) {
    throw "Portable package was not generated: $portablePath"
}

$manifestPath = Join-Path $installerOutputDirectory "SnapCut-Update.json"
$legacyManifestPath = Join-Path $installerOutputDirectory "Screenshot-Update.json"
$trackedManifestDirectory = Join-Path $repositoryRoot "updates"
$trackedManifestPath = Join-Path $trackedManifestDirectory "SnapCut-Update.json"
$trackedLegacyManifestPath = Join-Path $trackedManifestDirectory "Screenshot-Update.json"
$installerFile = Get-Item -LiteralPath $installerPath
$portableFile = Get-Item -LiteralPath $portablePath
$githubReleaseBaseUrl = "https://github.com/jiuwanzi-hui/Screenshot/releases/latest/download"
$giteeReleaseBaseUrl = "https://gitee.com/wwangyunhui/screenshot/releases/download/v$Version"
$manifest = [ordered]@{
    version = $Version
    releasePage = "https://github.com/jiuwanzi-hui/Screenshot/releases/latest"
    installer = [ordered]@{
        fileName = $installerFile.Name
        githubUrl = "$githubReleaseBaseUrl/$($installerFile.Name)"
        giteeUrl = "$giteeReleaseBaseUrl/$($installerFile.Name)"
        size = $installerFile.Length
        sha256 = (Get-FileHash -LiteralPath $installerFile.FullName -Algorithm SHA256).Hash
    }
    portable = [ordered]@{
        fileName = $portableFile.Name
        githubUrl = "$githubReleaseBaseUrl/$($portableFile.Name)"
        giteeUrl = "$giteeReleaseBaseUrl/$($portableFile.Name)"
        size = $portableFile.Length
        sha256 = (Get-FileHash -LiteralPath $portableFile.FullName -Algorithm SHA256).Hash
    }
}
$manifest |
    ConvertTo-Json -Depth 4 |
    Set-Content -LiteralPath $manifestPath -Encoding UTF8
New-Item -ItemType Directory -Path $trackedManifestDirectory -Force | Out-Null

# Existing 2.1.x and 2.2.x clients request this fixed legacy manifest name. Its package
# entries intentionally point at the new SnapCut-named assets; clients shipped
# since 2.1.0 accept both brands, so only one copy of each large package needs
# to be uploaded to a release.
Copy-Item -LiteralPath $manifestPath -Destination $legacyManifestPath -Force
if (-not $SkipTrackedManifestUpdate) {
    Copy-Item -LiteralPath $manifestPath -Destination $trackedManifestPath -Force
    Copy-Item -LiteralPath $legacyManifestPath `
        -Destination $trackedLegacyManifestPath `
        -Force
}

$outputs = @(
    $installerPath
    $portablePath
    $manifestPath
    $legacyManifestPath
)
if (-not $SkipTrackedManifestUpdate) {
    $outputs += $trackedManifestPath, $trackedLegacyManifestPath
}

Get-Item -LiteralPath $outputs
