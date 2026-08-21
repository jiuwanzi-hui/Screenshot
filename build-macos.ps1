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
$ocrProject = Join-Path $repoRoot "src/SnapCut.Mac.OcrHost/SnapCut.Mac.OcrHost.csproj"
$template = Join-Path $repoRoot "src/SnapCut.Mac/Packaging/Info.plist"
$asset = Join-Path $repoRoot "src/Screenshot.App/Assets/Screenshot.png"
$thirdPartyNotices = Join-Path $repoRoot "src/SnapCut.Mac/THIRD-PARTY-NOTICES.txt"
$outputRoot = if ([IO.Path]::IsPathRooted($OutputDirectory)) {
    $OutputDirectory
} else {
    Join-Path $repoRoot $OutputDirectory
}
$publishDirectory = Join-Path $outputRoot "publish-$Runtime"
$ocrPublishDirectory = Join-Path $outputRoot "publish-ocr-$Runtime"
$appName = "SnapCut.app"
$appDirectory = Join-Path $outputRoot $appName
$contentsDirectory = Join-Path $appDirectory "Contents"
$macOsDirectory = Join-Path $contentsDirectory "MacOS"
$resourcesDirectory = Join-Path $contentsDirectory "Resources"
$ocrHelperDirectory = Join-Path $contentsDirectory "Helpers/OcrHost"
$helpersDirectory = Join-Path $contentsDirectory "Helpers"

dotnet publish $project `
    -c Release `
    -r $Runtime `
    --self-contained true `
    -p:Version=$Version `
    -p:PublishSingleFile=false `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -o $publishDirectory

if ($Runtime -eq "osx-arm64") {
    dotnet publish $ocrProject `
        -c Release `
        -r $Runtime `
        --self-contained true `
        -p:Version=$Version `
        -p:PublishSingleFile=false `
        -p:DebugType=None `
        -p:DebugSymbols=false `
        -o $ocrPublishDirectory
}

if (Test-Path -LiteralPath $appDirectory) {
    Remove-Item -LiteralPath $appDirectory -Recurse -Force
}

New-Item -ItemType Directory -Path $macOsDirectory -Force | Out-Null
New-Item -ItemType Directory -Path $resourcesDirectory -Force | Out-Null
New-Item -ItemType Directory -Path $helpersDirectory -Force | Out-Null
if ($Runtime -eq "osx-arm64") {
    New-Item -ItemType Directory -Path $ocrHelperDirectory -Force | Out-Null
}
Copy-Item -Path (Join-Path $publishDirectory "*") -Destination $macOsDirectory -Recurse
if ($Runtime -eq "osx-arm64") {
    Copy-Item -Path (Join-Path $ocrPublishDirectory "*") -Destination $ocrHelperDirectory -Recurse
}
Copy-Item -LiteralPath $asset -Destination (Join-Path $resourcesDirectory "SnapCut.png")
Copy-Item -LiteralPath $thirdPartyNotices `
    -Destination (Join-Path $resourcesDirectory "THIRD-PARTY-NOTICES.txt")

if ($Runtime -eq "osx-x64") {
    $helperCache = Join-Path $outputRoot "helper-cache"
    New-Item -ItemType Directory -Path $helperCache -Force | Out-Null
    foreach ($helper in @(
        @{ Name = "ffmpeg"; Url = "https://evermeet.cx/ffmpeg/getrelease/zip" },
        @{ Name = "ffprobe"; Url = "https://evermeet.cx/ffmpeg/getrelease/ffprobe/zip" }
    )) {
        $cachedExecutable = Join-Path $helperCache $helper.Name
        if (-not (Test-Path -LiteralPath $cachedExecutable)) {
            $archive = Join-Path $helperCache ($helper.Name + ".zip")
            $expanded = Join-Path $helperCache ($helper.Name + "-expanded")
            Invoke-WebRequest -Uri $helper.Url -OutFile $archive
            if (Test-Path -LiteralPath $expanded) {
                Remove-Item -LiteralPath $expanded -Recurse -Force
            }
            Expand-Archive -LiteralPath $archive -DestinationPath $expanded -Force
            Copy-Item -LiteralPath (Join-Path $expanded $helper.Name) `
                -Destination $cachedExecutable
            Remove-Item -LiteralPath $expanded -Recurse -Force
            Remove-Item -LiteralPath $archive -Force
        }
        Copy-Item -LiteralPath $cachedExecutable `
            -Destination (Join-Path $helpersDirectory $helper.Name)
    }
} else {
    Write-Warning "FFmpeg helper is not bundled for osx-arm64 yet; MP4/GIF post-processing requires an arm64 helper."
}

$buildVersion = $Version.Replace("-", ".")
$plist = Get-Content -LiteralPath $template -Raw
$plist = $plist.Replace("__VERSION__", $Version)
$plist = $plist.Replace("__BUILD_VERSION__", $buildVersion)
[IO.File]::WriteAllText(
    (Join-Path $contentsDirectory "Info.plist"),
    $plist,
    [Text.UTF8Encoding]::new($false))

# Keep one stable download name. Older archives are removed before packaging.
Get-ChildItem -LiteralPath $outputRoot -File -Filter "SnapCut-*.zip" -ErrorAction SilentlyContinue |
    Remove-Item -Force
Get-ChildItem -LiteralPath $outputRoot -File -Filter "SnapCut-*.tar.gz" -ErrorAction SilentlyContinue |
    Remove-Item -Force
$zipPath = Join-Path $outputRoot "SnapCut-$Runtime.zip"
$tarGzPath = Join-Path $outputRoot "SnapCut-$Runtime.tar.gz"

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
    if (Test-Path -LiteralPath (Join-Path $ocrHelperDirectory "snapcut-ocr")) {
        & chmod +x (Join-Path $ocrHelperDirectory "snapcut-ocr")
    }
    if (Test-Path -LiteralPath (Join-Path $helpersDirectory "ffmpeg")) {
        & chmod +x (Join-Path $helpersDirectory "ffmpeg")
        & chmod +x (Join-Path $helpersDirectory "ffprobe")
    }
    if ($SignIdentity) {
        & codesign --force --deep --options runtime --sign $SignIdentity $appDirectory
        & codesign --verify --deep --strict --verbose=2 $appDirectory
    }

    & ditto -c -k --sequesterRsrc --keepParent $appDirectory $zipPath
} else {
    Compress-Archive -LiteralPath $appDirectory -DestinationPath $zipPath

    $fileReadMode =
        [IO.UnixFileMode]::UserRead -bor
        [IO.UnixFileMode]::UserWrite -bor
        [IO.UnixFileMode]::GroupRead -bor
        [IO.UnixFileMode]::OtherRead
    $executableMode =
        $fileReadMode -bor
        [IO.UnixFileMode]::UserExecute -bor
        [IO.UnixFileMode]::GroupExecute -bor
        [IO.UnixFileMode]::OtherExecute
    $archiveStream = [IO.File]::Create($tarGzPath)
    $gzipStream = [IO.Compression.GZipStream]::new(
        $archiveStream,
        [IO.Compression.CompressionLevel]::Optimal,
        $false)
    $tarWriter = [System.Formats.Tar.TarWriter]::new(
        $gzipStream,
        [System.Formats.Tar.TarEntryFormat]::Pax,
        $false)
    try {
        $archiveRoot = Split-Path -Parent $appDirectory
        $archiveItems = @((Get-Item -LiteralPath $appDirectory)) +
            @(Get-ChildItem -LiteralPath $appDirectory -Force -Recurse)
        foreach ($item in $archiveItems) {
            $entryName = [IO.Path]::GetRelativePath(
                $archiveRoot,
                $item.FullName).Replace("\", "/")
            $entryType = if ($item.PSIsContainer) {
                [System.Formats.Tar.TarEntryType]::Directory
            } else {
                [System.Formats.Tar.TarEntryType]::RegularFile
            }
            $entry = [System.Formats.Tar.PaxTarEntry]::new(
                $entryType,
                $entryName)
            $entry.ModificationTime = $item.LastWriteTimeUtc
            $entry.Mode = if ($item.PSIsContainer -or
                $entryName.EndsWith("/Contents/MacOS/snapcut") -or
                $entryName.EndsWith("/Contents/MacOS/createdump") -or
                $entryName.EndsWith("/Contents/Helpers/OcrHost/snapcut-ocr") -or
                $entryName.EndsWith("/Contents/Helpers/OcrHost/createdump") -or
                $entryName.EndsWith("/Contents/Helpers/ffmpeg") -or
                $entryName.EndsWith("/Contents/Helpers/ffprobe")) {
                $executableMode
            } else {
                $fileReadMode
            }

            if ($item.PSIsContainer) {
                $tarWriter.WriteEntry($entry)
                continue
            }

            $entryStream = [IO.File]::OpenRead($item.FullName)
            try {
                $entry.DataStream = $entryStream
                $tarWriter.WriteEntry($entry)
            } finally {
                $entryStream.Dispose()
            }
        }
    } finally {
        $tarWriter.Dispose()
        $gzipStream.Dispose()
        $archiveStream.Dispose()
    }

    Write-Warning "Cross-build only. The TAR.GZ preserves executable permissions; signing and notarization still require macOS."
}

Write-Host "App: $appDirectory"
Write-Host "Zip: $zipPath"
if (Test-Path -LiteralPath $tarGzPath) {
    Write-Host "Tar: $tarGzPath"
}
