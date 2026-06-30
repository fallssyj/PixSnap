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
Write-Host "Done: $setupFile ($sizeMb MB)" -ForegroundColor Green
