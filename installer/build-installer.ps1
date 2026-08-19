param(
    [string]$Version = "3.2.0",
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

foreach ($linkerFileName in @(
    "onnxruntime.lib",
    "onnxruntime_providers_shared.lib"
)) {
    $linkerFilePath = Join-Path $publishDirectory $linkerFileName
    if (Test-Path -LiteralPath $linkerFilePath) {
        Remove-Item -LiteralPath $linkerFilePath -Force
    }
}

$screenRecorderLibraryPath = Join-Path `
    $publishDirectory `
    "ScreenRecorderLib.dll"
if (-not (Test-Path -LiteralPath $screenRecorderLibraryPath)) {
    throw "Required recording component was not published: $screenRecorderLibraryPath"
}

# ScreenRecorderLib is a C++/CLI component. Ship the native Visual C++ runtime
# beside it so recording also works on clean Windows installations that do not
# already have the machine-wide redistributable installed.
$visualCppRuntimeFileNames = @(
    "concrt140.dll",
    "msvcp140.dll",
    "msvcp140_1.dll",
    "vcruntime140.dll",
    "vcruntime140_1.dll"
)
$visualCppRuntimeCandidates = @()
if (-not [string]::IsNullOrWhiteSpace($env:VCToolsRedistDir)) {
    $visualCppRuntimeCandidates += Get-ChildItem `
        -Path (Join-Path $env:VCToolsRedistDir "x64") `
        -Directory `
        -Filter "Microsoft.VC*.CRT" `
        -ErrorAction SilentlyContinue
}

$visualStudioRoot = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio"
if (Test-Path -LiteralPath $visualStudioRoot) {
    $visualCppRuntimeCandidates += Get-ChildItem `
        -Path $visualStudioRoot `
        -Directory `
        -Filter "Microsoft.VC*.CRT" `
        -Recurse `
        -ErrorAction SilentlyContinue |
        Where-Object { $_.Parent.Name -eq "x64" }
}

$systemRuntimeDirectory = [Environment]::GetFolderPath(
    [Environment+SpecialFolder]::System)
$visualCppRuntimeCandidates += Get-Item `
    -LiteralPath $systemRuntimeDirectory `
    -ErrorAction SilentlyContinue
$visualCppRuntimeDirectory = $visualCppRuntimeCandidates |
    Where-Object {
        $candidate = $_.FullName
        -not ($visualCppRuntimeFileNames | Where-Object {
            -not (Test-Path -LiteralPath (Join-Path $candidate $_))
        })
    } |
    Sort-Object LastWriteTimeUtc -Descending |
    Select-Object -First 1

if (-not $visualCppRuntimeDirectory) {
    throw "The x64 Visual C++ runtime required by ScreenRecorderLib was not found."
}

foreach ($runtimeFileName in $visualCppRuntimeFileNames) {
    Copy-Item `
        -LiteralPath (Join-Path $visualCppRuntimeDirectory.FullName $runtimeFileName) `
        -Destination (Join-Path $publishDirectory $runtimeFileName) `
        -Force
}

$requiredPublishFiles = @(
    "SnapCut.exe",
    "ScreenRecorderLib.dll",
    "LICENSE.txt",
    "THIRD-PARTY-NOTICES.txt",
    "SnapCut-Releases.json"
) + $visualCppRuntimeFileNames
foreach ($requiredFileName in $requiredPublishFiles) {
    $requiredFilePath = Join-Path $publishDirectory $requiredFileName
    if (-not (Test-Path -LiteralPath $requiredFilePath) -or
        (Get-Item -LiteralPath $requiredFilePath).Length -le 0) {
        throw "Required publish file is missing or empty: $requiredFilePath"
    }
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
