param(
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$Directory,
    [switch]$LocalTest
)
$ErrorActionPreference = 'Stop'
$manifestPath = Join-Path $Directory 'SnapCut-Update.json'
$legacyPath = Join-Path $Directory 'Screenshot-Update.json'
$historyPath = Join-Path $Directory 'SnapCut-Releases.json'
foreach ($requiredPath in @($manifestPath, $legacyPath, $historyPath)) {
    if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
        throw "Missing release metadata: $requiredPath"
    }
}
if ((Get-FileHash -LiteralPath $manifestPath).Hash -ne (Get-FileHash -LiteralPath $legacyPath).Hash) {
    throw 'Legacy and current update manifests differ.'
}
$manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
if ($manifest.version -cne $Version) { throw 'Update manifest version does not match the package version.' }
if ([bool]$manifest.prerelease -ne [bool]$LocalTest) { throw 'Update manifest does not match the requested stable or local test channel.' }
$assetKinds = [ordered]@{ installer = 'Setup'; portable = 'Portable'; portable7z = 'Portable' }
$extensions = @{ installer = 'exe'; portable = 'zip'; portable7z = '7z' }
foreach ($kind in $assetKinds.Keys) {
    $asset = $manifest.$kind
    $expectedName = "SnapCut-$($assetKinds[$kind])-$Version-win-x64.$($extensions[$kind])"
    if ($null -eq $asset -or $asset.fileName -cne $expectedName) { throw "Missing or incorrectly named $kind package." }
    $path = Join-Path $Directory $expectedName
    $file = Get-Item -LiteralPath $path
    if ($file.Length -le 0 -or $file.Length -ne $asset.size -or
        (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $asset.sha256) {
        throw "Package size or SHA-256 mismatch: $expectedName"
    }
    if ($kind -ne 'portable' -and $file.Length -ge 100000000) { throw "Gitee attachment exceeds its size limit: $expectedName" }
    if ($asset.githubUrl -cne "https://github.com/jiuwanzi-hui/Screenshot/releases/download/v$Version/$expectedName" -or
        $asset.giteeUrl -cne "https://gitee.com/wwangyunhui/screenshot/releases/download/v$Version/$expectedName") {
        throw "Package URLs must use the matching release tag on both mirrors: $expectedName"
    }
}
# Assign the decoded array directly: Windows PowerShell 5.1 otherwise wraps
# the whole history in one pipeline item, unlike PowerShell 7.
$history = Get-Content -LiteralPath $historyPath -Raw -Encoding UTF8 | ConvertFrom-Json
$latest = @($history | Where-Object { -not $_.draft -and ([bool]$_.prerelease -eq [bool]$LocalTest) } |
    Sort-Object { [version]($_.tag_name -replace '^[vV]', '') } -Descending)[0]
if ($null -eq $latest -or $latest.tag_name -cne "v$Version") { throw 'Release history is stale or does not match this version.' }
foreach ($name in @('SnapCut-Update.json', 'Screenshot-Update.json', $manifest.installer.fileName,
    $manifest.portable.fileName, $manifest.portable7z.fileName)) {
    if ($name -notin @($latest.assets.name)) { throw "Release history is missing attachment: $name" }
}
Write-Output "Verified $Version update metadata, hashes, both mirrors, and Gitee size limits."
if ($LocalTest) {
    Write-Output 'Local test artifacts only. Download URLs are reserved; do not upload these manifests as a stable update.'
    return
}
Write-Output 'GitHub attachments: installer EXE, portable ZIP, portable 7z, SnapCut-Update.json, Screenshot-Update.json, SnapCut-Releases.json.'
Write-Output 'Gitee attachments: installer EXE, portable 7z, SnapCut-Update.json, Screenshot-Update.json, SnapCut-Releases.json. Do not rely on raw/main metadata.'
