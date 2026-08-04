param(
    [ValidateSet("osx-arm64", "osx-x64")]
    [string]$Runtime = "osx-arm64",
    [string]$Version = "0.1.0",
    [string]$OutputDirectory = "artifacts/mac",
    [string]$SignIdentity = ""
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$project = Join-Path $repoRoot "src/SnapCut.Mac/SnapCut.Mac.csproj"
$template = Join-Path $repoRoot "src/SnapCut.Mac/Packaging/Info.plist"
$asset = Join-Path $repoRoot "src/Screenshot.App/Assets/Screenshot.png"
$outputRoot = if ([IO.Path]::IsPathRooted($OutputDirectory)) {
    $OutputDirectory
} else {
    Join-Path $repoRoot $OutputDirectory
}
$publishDirectory = Join-Path $outputRoot "publish-$Runtime"
$appName = "SnapCut-$Version-$Runtime.app"
$appDirectory = Join-Path $outputRoot $appName
$contentsDirectory = Join-Path $appDirectory "Contents"
$macOsDirectory = Join-Path $contentsDirectory "MacOS"
$resourcesDirectory = Join-Path $contentsDirectory "Resources"

dotnet publish $project `
    -c Release `
    -r $Runtime `
    --self-contained true `
    -p:Version=$Version `
    -p:PublishSingleFile=false `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -o $publishDirectory

if (Test-Path -LiteralPath $appDirectory) {
    Remove-Item -LiteralPath $appDirectory -Recurse -Force
}

New-Item -ItemType Directory -Path $macOsDirectory -Force | Out-Null
New-Item -ItemType Directory -Path $resourcesDirectory -Force | Out-Null
Copy-Item -Path (Join-Path $publishDirectory "*") -Destination $macOsDirectory -Recurse
Copy-Item -LiteralPath $asset -Destination (Join-Path $resourcesDirectory "SnapCut.png")

$buildVersion = $Version.Replace("-", ".")
$plist = Get-Content -LiteralPath $template -Raw
$plist = $plist.Replace("__VERSION__", $Version)
$plist = $plist.Replace("__BUILD_VERSION__", $buildVersion)
[IO.File]::WriteAllText(
    (Join-Path $contentsDirectory "Info.plist"),
    $plist,
    [Text.UTF8Encoding]::new($false))

$zipPath = Join-Path $outputRoot "SnapCut-$Version-$Runtime.zip"
if (Test-Path -LiteralPath $zipPath) {
    Remove-Item -LiteralPath $zipPath -Force
}

if ($IsMacOS) {
    $iconSet = Join-Path $outputRoot "SnapCut.iconset"
    if (Test-Path -LiteralPath $iconSet) {
        Remove-Item -LiteralPath $iconSet -Recurse -Force
    }

    New-Item -ItemType Directory -Path $iconSet -Force | Out-Null
    foreach ($iconSize in @(16, 32, 128, 256, 512)) {
        & sips -z $iconSize $iconSize $asset --out (Join-Path $iconSet "icon_${iconSize}x${iconSize}.png") | Out-Null
        $retinaSize = $iconSize * 2
        & sips -z $retinaSize $retinaSize $asset --out (Join-Path $iconSet "icon_${iconSize}x${iconSize}@2x.png") | Out-Null
    }

    & iconutil -c icns $iconSet -o (Join-Path $resourcesDirectory "SnapCut.icns")
    Remove-Item -LiteralPath $iconSet -Recurse -Force
    & chmod +x (Join-Path $macOsDirectory "snapcut")
    if ($SignIdentity) {
        & codesign --force --deep --options runtime --sign $SignIdentity $appDirectory
        & codesign --verify --deep --strict --verbose=2 $appDirectory
    }

    & ditto -c -k --sequesterRsrc --keepParent $appDirectory $zipPath
} else {
    Compress-Archive -LiteralPath $appDirectory -DestinationPath $zipPath
    Write-Warning "Cross-build only. Re-run this script on macOS to preserve executable permissions and sign the app."
}

Write-Host "App: $appDirectory"
Write-Host "Zip: $zipPath"
