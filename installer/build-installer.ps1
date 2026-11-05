param(
    [string]$Version = "4.2.0",
    [switch]$SkipTrackedManifestUpdate,
    [switch]$EnableCaptureDiagnostics,
    [string]$OutputDirectory,
    [switch]$EnableInputDiagnostics,
    [switch]$LocalTest
)

$ErrorActionPreference = "Stop"
if (($EnableCaptureDiagnostics -or $EnableInputDiagnostics) -and -not $LocalTest) {
    throw 'Diagnostic builds are local tests only; use -LocalTest and never publish their manifests.'
}
if ($LocalTest) { $SkipTrackedManifestUpdate = $true }
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$appProject = Join-Path $repositoryRoot "src\Screenshot.App\Screenshot.App.csproj"
$publishDirectory = Join-Path $repositoryRoot ("artifacts\publish\" + [Guid]::NewGuid().ToString('N') + "\win-x64")
$installerOutputDirectory = if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    Join-Path $PSScriptRoot "dist"
} else {
    [IO.Path]::GetFullPath($OutputDirectory)
}
$setupScript = Join-Path $PSScriptRoot "Screenshot.iss"
# Every packaging session must preserve Gitee-only detection and verified updates.
# This suite uses fake HTTP and isolated files; it never installs on the desktop.
dotnet test (Join-Path $repositoryRoot 'tests\Screenshot.App.Tests\Screenshot.App.Tests.csproj') `
    -c Release --nologo --filter 'FullyQualifiedName~ApplicationUpdateServiceTests|FullyQualifiedName~UpdateRecoveryTests|FullyQualifiedName~PortableUpdateArchiveTests|FullyQualifiedName~RollbackSettingsTests|FullyQualifiedName~UpdateArtifactValidationTests|FullyQualifiedName~UpdateButtonIsReadyWhileReleaseHistoryIsStillPending'
if ($LASTEXITCODE -ne 0) { throw 'Update regression checks failed; packaging stopped.' }
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

# A fresh staging directory prevents stale/private files from entering a release.
# Keep previous delivery packages; a build must never clear the whole dist folder.
New-Item -ItemType Directory -Path $publishDirectory, $installerOutputDirectory -Force | Out-Null

$runtimeArchive = & (Join-Path $PSScriptRoot 'prepare-translation-runtime.ps1')
$publishDefineConstants = "TRACE"
$informationalVersion = if ($LocalTest) { "$Version-localtest" } else { $Version }
if ($EnableCaptureDiagnostics) {
    # MSBuild treats semicolons as list separators even when passed through
    # PowerShell's native argument binder. Escape them for dotnet publish.
    $publishDefineConstants += "%3BSNAPCUT_CAPTURE_DIAGNOSTICS"
    if ($EnableInputDiagnostics) { $publishDefineConstants += "%3BSNAPCUT_INPUT_DIAGNOSTICS" }
}

dotnet publish $appProject `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DefineConstants=$publishDefineConstants `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -p:Version=$Version `
    -p:InformationalVersion=$informationalVersion `
    -o $publishDirectory `
    --nologo

if ($LASTEXITCODE -ne 0) {
    throw "Screenshot publish failed."
}

# Local test deliveries have their own prerelease history. Do not change the
# tracked public history or advertise an unpublished build as a stable release.
if ($LocalTest) {
    $publishedHistoryPath = Join-Path $publishDirectory 'SnapCut-Releases.json'
    $publicHistory = Get-Content -LiteralPath $publishedHistoryPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $testAssetNames = @("SnapCut-Setup-$Version-win-x64.exe", "SnapCut-Portable-$Version-win-x64.zip",
        "SnapCut-Portable-$Version-win-x64.7z", 'SnapCut-Update.json', 'Screenshot-Update.json', 'SnapCut-Releases.json')
    $testRelease = [ordered]@{
        tag_name = "v$Version"
        name = "SnapCut $Version local test"
        body = 'Local test build only. Not published; reserved download URLs are not live.'
        draft = $false
        prerelease = $true
        assets = @($testAssetNames | ForEach-Object { [ordered]@{ name = $_ } })
    }
    @($testRelease) + @($publicHistory | Where-Object { $_.tag_name -ne "v$Version" }) |
        ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $publishedHistoryPath -Encoding UTF8
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
    "SnapCut-Releases.json",
    "Licenses\Hy-MT2-1.8B.txt",
    "Licenses\Qwen3-VL-4B.txt",
    "Licenses\RapidOcrNet.txt"
) + $visualCppRuntimeFileNames
foreach ($requiredFileName in $requiredPublishFiles) {
    $requiredFilePath = Join-Path $publishDirectory $requiredFileName
    if (-not (Test-Path -LiteralPath $requiredFilePath) -or
        (Get-Item -LiteralPath $requiredFilePath).Length -le 0) {
        throw "Required publish file is missing or empty: $requiredFilePath"
    }
}

$publishedRuntime = Join-Path $publishDirectory ("OfflinePackages\" + [IO.Path]::GetFileName($runtimeArchive))
if (-not (Test-Path -LiteralPath $publishedRuntime) -or
    (Get-FileHash -LiteralPath $publishedRuntime).Hash -ne (Get-FileHash -LiteralPath $runtimeArchive).Hash) {
    throw 'The Windows package is missing its verified offline translation runtime.'
}

# Diagnostic collectors remain developer tools, never package payload. Apply
# the same allowlist to local tests so trace flags cannot ship scripts/markers.
$allowedFiles = @($requiredPublishFiles) + @("OfflinePackages\" + [IO.Path]::GetFileName($runtimeArchive))
foreach ($publishedFile in Get-ChildItem -LiteralPath $publishDirectory -File -Recurse) {
    $relativeName = $publishedFile.FullName.Substring($publishDirectory.Length + 1)
    if ($relativeName -notin $allowedFiles) {
        throw "Unexpected file in binary release: $relativeName"
    }
}

& $innoCompiler "/DAppVersion=$Version" "/DPublishDir=$publishDirectory" "/O$installerOutputDirectory" $setupScript
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

# Keep standard ZIP for older automatic updaters. New clients select the
# smaller 7z from an additive manifest field and extract it internally.
$giteePortablePath = Join-Path $installerOutputDirectory "SnapCut-Portable-$Version-win-x64.7z"
& (Join-Path $PSScriptRoot 'build-portable-7z.ps1') `
    -PublishDirectory $publishDirectory -DestinationPath $giteePortablePath

$manifestPath = Join-Path $installerOutputDirectory "SnapCut-Update.json"
$legacyManifestPath = Join-Path $installerOutputDirectory "Screenshot-Update.json"
$trackedManifestDirectory = Join-Path $repositoryRoot "updates"
$trackedManifestPath = Join-Path $trackedManifestDirectory "SnapCut-Update.json"
$trackedLegacyManifestPath = Join-Path $trackedManifestDirectory "Screenshot-Update.json"
$installerFile = Get-Item -LiteralPath $installerPath
$portableFile = Get-Item -LiteralPath $portablePath
$giteePortableFile = Get-Item -LiteralPath $giteePortablePath
if ($installerFile.Length -ge 100000000) {
    throw 'The Gitee installer must be smaller than 100,000,000 bytes.'
}
$githubReleaseBaseUrl = "https://github.com/jiuwanzi-hui/Screenshot/releases/download/v$Version"
$giteeReleaseBaseUrl = "https://gitee.com/wwangyunhui/screenshot/releases/download/v$Version"
$manifest = [ordered]@{
    version = $Version
    releasePage = "https://github.com/jiuwanzi-hui/Screenshot/releases/tag/v$Version"
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
    portable7z = [ordered]@{
        fileName = $giteePortableFile.Name
        githubUrl = "$githubReleaseBaseUrl/$($giteePortableFile.Name)"
        giteeUrl = "$giteeReleaseBaseUrl/$($giteePortableFile.Name)"
        size = $giteePortableFile.Length
        sha256 = (Get-FileHash -LiteralPath $giteePortableFile.FullName -Algorithm SHA256).Hash
    }
}
if ($LocalTest) { $manifest['prerelease'] = $true }
$manifest |
    ConvertTo-Json -Depth 4 |
    Set-Content -LiteralPath $manifestPath -Encoding UTF8
New-Item -ItemType Directory -Path $trackedManifestDirectory -Force | Out-Null

# Existing 2.1.x and 2.2.x clients request this fixed legacy manifest name. Its package
# entries intentionally point at the new SnapCut-named assets; clients shipped
# since 2.1.0 accept both brands, so only one copy of each large package needs
# to be uploaded to a release.
Copy-Item -LiteralPath $manifestPath -Destination $legacyManifestPath -Force
$releaseHistoryPath = Join-Path $installerOutputDirectory 'SnapCut-Releases.json'
Copy-Item -LiteralPath (Join-Path $publishDirectory 'SnapCut-Releases.json') -Destination $releaseHistoryPath -Force
& (Join-Path $PSScriptRoot 'verify-update-artifacts.ps1') -Version $Version -Directory $installerOutputDirectory -LocalTest:$LocalTest
if (-not $SkipTrackedManifestUpdate) {
    Copy-Item -LiteralPath $manifestPath -Destination $trackedManifestPath -Force
    Copy-Item -LiteralPath $legacyManifestPath `
        -Destination $trackedLegacyManifestPath `
        -Force
}

$outputs = @(
    $installerPath
    $portablePath
    $giteePortablePath
    $manifestPath
    $legacyManifestPath
    $releaseHistoryPath
)
if (-not $SkipTrackedManifestUpdate) {
    $outputs += $trackedManifestPath, $trackedLegacyManifestPath
}

Get-Item -LiteralPath $outputs
