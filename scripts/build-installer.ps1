#Requires -Version 5.1
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repoRoot 'src\PixSnap\PixSnap\PixSnap.csproj'
$stagingDir = Join-Path $repoRoot 'installer\staging'
$issPath = Join-Path $repoRoot 'installer\PixSnap.iss'
$outputDir = Join-Path $repoRoot 'installer\output'

function Find-MSBuild {
    $candidates = @(
        "$env:ProgramFiles\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe",
        "$env:ProgramFiles\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\MSBuild.exe",
        "$env:ProgramFiles\Microsoft Visual Studio\2022\Enterprise\MSBuild\Current\Bin\MSBuild.exe",
        "$env:ProgramFiles\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe",
        "$env:ProgramFiles\Microsoft Visual Studio\18\Professional\MSBuild\Current\Bin\MSBuild.exe",
        "$env:ProgramFiles\Microsoft Visual Studio\18\Enterprise\MSBuild\Current\Bin\MSBuild.exe"
    )

    foreach ($path in $candidates) {
        if (Test-Path $path) { return $path }
    }

    $vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
    if (Test-Path $vswhere) {
        $found = & $vswhere -latest -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
        if ($found -and (Test-Path $found)) { return $found }
    }

    throw 'MSBuild not found. Install Visual Studio with .NET desktop and C++ desktop workloads.'
}

function Find-ISCC {
    if ($env:PIXSNAP_ISCC -and (Test-Path $env:PIXSNAP_ISCC)) {
        return $env:PIXSNAP_ISCC
    }

    $candidates = @(
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
    )

    foreach ($path in $candidates) {
        if (Test-Path $path) { return $path }
    }

    throw 'Inno Setup 6 not found. Install from https://jrsoftware.org/isdl.php or set PIXSNAP_ISCC.'
}

function Find-7z {
    if ($env:PIXSNAP_7Z -and (Test-Path $env:PIXSNAP_7Z)) {
        return $env:PIXSNAP_7Z
    }

    $candidates = @(
        "$env:ProgramFiles\7-Zip\7z.exe",
        "${env:ProgramFiles(x86)}\7-Zip\7z.exe"
    )

    foreach ($path in $candidates) {
        if (Test-Path $path) { return $path }
    }

    $fromPath = Get-Command 7z.exe -ErrorAction SilentlyContinue
    if ($fromPath) { return $fromPath.Source }

    throw '7-Zip not found. Install from https://www.7-zip.org/ or set PIXSNAP_7Z.'
}

function New-PortableArchive {
    param(
        [Parameter(Mandatory = $true)][string]$SevenZip,
        [Parameter(Mandatory = $true)][string]$SourceDir,
        [Parameter(Mandatory = $true)][string]$ArchivePath
    )

    if (Test-Path $ArchivePath) {
        Remove-Item -LiteralPath $ArchivePath -Force
    }

    $archiveParent = Split-Path -Parent $ArchivePath
    if (-not (Test-Path $archiveParent)) {
        New-Item -ItemType Directory -Path $archiveParent -Force | Out-Null
    }

    # 归档 staging 目录内容；解压到任意文件夹即可直接运行（绿色版）
    Push-Location $SourceDir
    try {
        & $SevenZip a -t7z -mx=9 -mmt=on -bd $ArchivePath * | Out-Host
        if ($LASTEXITCODE -ne 0) {
            throw "7z failed with exit code $LASTEXITCODE"
        }
    }
    finally {
        Pop-Location
    }
}

function Get-ProjectVersion {
    [xml]$proj = Get-Content -LiteralPath $projectPath
    foreach ($group in @($proj.Project.PropertyGroup)) {
        if ($group.Version) {
            return [string]$group.Version
        }
    }
    return '1.0.0'
}

Write-Host '==> Finding MSBuild...' -ForegroundColor Cyan
$msbuild = Find-MSBuild
Write-Host "    $msbuild"

$version = Get-ProjectVersion
Write-Host "==> Version: $version" -ForegroundColor Cyan

Write-Host '==> Cleaning staging...' -ForegroundColor Cyan
if (Test-Path $stagingDir) {
    Remove-Item -LiteralPath $stagingDir -Recurse -Force
}
New-Item -ItemType Directory -Path $stagingDir -Force | Out-Null
New-Item -ItemType Directory -Path $outputDir -Force | Out-Null

Write-Host '==> Restoring packages (win-x64)...' -ForegroundColor Cyan
& dotnet restore $projectPath -r win-x64

Write-Host '==> Publishing PixSnap (self-contained x64)...' -ForegroundColor Cyan
& $msbuild $projectPath `
    /t:Publish `
    /p:Configuration=Release `
    /p:Platform=x64 `
    /p:PublishProfile=InstallerProfile `
    /m `
    /v:minimal

$publishedExe = Join-Path $stagingDir 'PixSnap.exe'
$publishedNative = Join-Path $stagingDir 'NativeScreenCapturer.dll'
if (-not (Test-Path $publishedExe)) {
    throw "Publish failed: $publishedExe not found."
}
if (-not (Test-Path $publishedNative)) {
    throw "Publish failed: NativeScreenCapturer.dll not found. Build Release|x64 in Visual Studio first."
}

Write-Host '==> Removing debug symbols (.pdb) from staging...' -ForegroundColor Cyan
$pdbFiles = @(Get-ChildItem -LiteralPath $stagingDir -Filter '*.pdb' -Recurse -File -ErrorAction SilentlyContinue)
foreach ($pdb in $pdbFiles) {
    Remove-Item -LiteralPath $pdb.FullName -Force
}
Write-Host "    Removed $($pdbFiles.Count) .pdb file(s)" -ForegroundColor Cyan

Write-Host '==> Finding 7-Zip...' -ForegroundColor Cyan
$sevenZip = Find-7z
Write-Host "    $sevenZip"

$portableFile = Join-Path $outputDir "PixSnap-$version-x64-portable.7z"
Write-Host '==> Creating portable archive...' -ForegroundColor Cyan
New-PortableArchive -SevenZip $sevenZip -SourceDir $stagingDir -ArchivePath $portableFile
if (-not (Test-Path $portableFile)) {
    throw "Portable archive not created: $portableFile"
}
$portableSizeMb = [math]::Round((Get-Item $portableFile).Length / 1MB, 1)
Write-Host "    $portableFile ($portableSizeMb MB)" -ForegroundColor Cyan

Write-Host '==> Finding Inno Setup...' -ForegroundColor Cyan
$iscc = Find-ISCC
Write-Host "    $iscc"

Write-Host '==> Compiling installer...' -ForegroundColor Cyan
& $iscc $issPath "/DMyAppVersion=$version" "/DStagingDir=$stagingDir"

$setupFile = Join-Path $outputDir "PixSnap-Setup-$version-x64.exe"
if (-not (Test-Path $setupFile)) {
    throw "Installer not created: $setupFile"
}

$sizeMb = [math]::Round((Get-Item $setupFile).Length / 1MB, 1)
Write-Host ''
Write-Host "Done:" -ForegroundColor Green
Write-Host "  Installer: $setupFile ($sizeMb MB)" -ForegroundColor Green
Write-Host "  Portable:  $portableFile ($portableSizeMb MB)" -ForegroundColor Green
