param(
    [Parameter(Mandatory = $true)]
    [string]$Version,

    [string]$RepoSlug = "alexzhong233/GearDeck",

    [string]$Notes = "",

    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    [string]$Platform = "x64"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

Add-Type -AssemblyName System.IO.Compression.FileSystem

$repoRoot = Split-Path -Parent $PSScriptRoot
$appOutDir = Join-Path $repoRoot "src\GearDeck.App\bin\$Platform\$Configuration\net8.0-windows10.0.22621.0"
$appExe = Join-Path $appOutDir "GearDeck.App.exe"
$widgetExe = Join-Path $appOutDir "widget\GearDeck.Widget.exe"

if (-not (Test-Path $appExe)) {
    throw "GearDeck.App.exe not found at: $appExe"
}
if (-not (Test-Path $widgetExe)) {
    throw "GearDeck.Widget.exe not found at: $widgetExe"
}

$tag = "v$($Version.TrimStart('v', 'V'))"
$cleanVersion = $Version.TrimStart('v', 'V')

$releaseDir = Join-Path $repoRoot "releases\$tag"
$fullStage = Join-Path $releaseDir "_stage_full"
$patchStage = Join-Path $releaseDir "_stage_patch"

if (Test-Path $releaseDir) {
    Remove-Item $releaseDir -Recurse -Force
}
New-Item -ItemType Directory -Path $fullStage -Force | Out-Null
New-Item -ItemType Directory -Path $patchStage -Force | Out-Null

Write-Host "[package-release] Staging full release from $appOutDir ..."

# 1. Full standalone release staging (exclude PDBs and dev runtimeconfig)
Get-ChildItem -Path $appOutDir -Recurse -File | ForEach-Object {
    if ($_.Extension -eq ".pdb" -or $_.Name -like "*.runtimeconfig.dev.json") {
        return
    }
    $rel = $_.FullName.Substring($appOutDir.Length).TrimStart('\', '/')
    $dest = Join-Path $fullStage $rel
    $destDir = Split-Path -Parent $dest
    if (-not (Test-Path $destDir)) {
        New-Item -ItemType Directory -Path $destDir -Force | Out-Null
    }
    Copy-Item -Path $_.FullName -Destination $dest -Force
}

$fullZipName = "GearDeck-$tag-win-$Platform.zip"
$fullZipPath = Join-Path $releaseDir $fullZipName
[System.IO.Compression.ZipFile]::CreateFromDirectory($fullStage, $fullZipPath, [System.IO.Compression.CompressionLevel]::Optimal, $false)

Write-Host "[package-release] Staging fast patch release ..."

# 2. Fast binary & UI patch zip for in-app self-update (reuses existing WinAppSDK runtime DLLs in target install)
$rootPatchNames = @(
    "GearDeck.App.exe",
    "GearDeck.App.dll",
    "GearDeck.App.deps.json",
    "GearDeck.App.runtimeconfig.json",
    "GearDeck.Core.dll",
    "HidSharp.dll",
    "resources.pri"
)

foreach ($name in $rootPatchNames) {
    $src = Join-Path $appOutDir $name
    if (Test-Path $src) {
        Copy-Item -Path $src -Destination (Join-Path $patchStage $name) -Force
    }
}

# Copy all compiled XAML binary files (*.xbf) preserving relative directory structure (e.g. App.xbf, MainWindow.xbf, Pages\*.xbf)
Get-ChildItem -Path $appOutDir -Filter "*.xbf" -Recurse -File | ForEach-Object {
    $rel = $_.FullName.Substring($appOutDir.Length).TrimStart('\', '/')
    $dest = Join-Path $patchStage $rel
    $destDir = Split-Path -Parent $dest
    if (-not (Test-Path $destDir)) {
        New-Item -ItemType Directory -Path $destDir -Force | Out-Null
    }
    Copy-Item -Path $_.FullName -Destination $dest -Force
}

# Copy Assets\ and widget\ folders
foreach ($sub in @("Assets", "widget")) {
    $srcSub = Join-Path $appOutDir $sub
    if (Test-Path $srcSub) {
        Get-ChildItem -Path $srcSub -Recurse -File | ForEach-Object {
            if ($_.Extension -eq ".pdb" -or $_.Name -like "*.runtimeconfig.dev.json") {
                return
            }
            $rel = $_.FullName.Substring($appOutDir.Length).TrimStart('\', '/')
            $dest = Join-Path $patchStage $rel
            $destDir = Split-Path -Parent $dest
            if (-not (Test-Path $destDir)) {
                New-Item -ItemType Directory -Path $destDir -Force | Out-Null
            }
            Copy-Item -Path $_.FullName -Destination $dest -Force
        }
    }
}

$patchZipName = "GearDeck-patch-$tag-win-$Platform.zip"
$patchZipPath = Join-Path $releaseDir $patchZipName
[System.IO.Compression.ZipFile]::CreateFromDirectory($patchStage, $patchZipPath, [System.IO.Compression.CompressionLevel]::Optimal, $false)

Remove-Item $fullStage -Recurse -Force
Remove-Item $patchStage -Recurse -Force

$fullHash = (Get-FileHash -Path $fullZipPath -Algorithm SHA256).Hash.ToUpperInvariant()
$fullSize = (Get-Item $fullZipPath).Length
$patchHash = (Get-FileHash -Path $patchZipPath -Algorithm SHA256).Hash.ToUpperInvariant()
$patchSize = (Get-Item $patchZipPath).Length

$manifest = [ordered]@{
    version     = $cleanVersion
    tag         = $tag
    publishedAt = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
    htmlUrl     = "https://github.com/$RepoSlug/releases/tag/$tag"
    downloadUrl = "https://github.com/$RepoSlug/releases/download/$tag/$fullZipName"
    fullSha256  = $fullHash
    fullSize    = $fullSize
    patchUrl    = "https://github.com/$RepoSlug/releases/download/$tag/$patchZipName"
    patchSha256 = $patchHash
    patchSize   = $patchSize
    notes       = $Notes
}

$jsonText = $manifest | ConvertTo-Json -Depth 4
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)

$releaseLatestPath = Join-Path $releaseDir "latest.json"
$rootLatestPath = Join-Path $repoRoot "latest.json"
[System.IO.File]::WriteAllText($releaseLatestPath, $jsonText, $utf8NoBom)
[System.IO.File]::WriteAllText($rootLatestPath, $jsonText, $utf8NoBom)

Write-Host "[package-release] Done:"
Write-Host "  Full : $fullZipPath ($fullSize bytes, SHA256=$fullHash)"
Write-Host "  Patch: $patchZipPath ($patchSize bytes, SHA256=$patchHash)"
Write-Host "  JSON : $releaseLatestPath & $rootLatestPath"
