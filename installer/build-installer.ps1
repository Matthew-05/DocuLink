#Requires -Version 5.1
<#
.SYNOPSIS
    Full clean rebuild of DocuLink and packaging into a single MSI.

.DESCRIPTION
    Runs every build step from scratch in order:
      1. Clean all previous build artifacts
      2. Build the Python OCR worker (downloads Ghostscript + Tesseract if needed,
         then bundles them via PyInstaller — they end up inside worker.exe)
      3. Build the C# add-in in Release mode (also builds TypeScript web apps and
         copies the worker into bin\Release\)
      4. Harvest the Release output into a WiX component group (doculink-files.wxs)
      5. Compile and link into DocuLink-Setup-<version>.msi

.PREREQUISITES
    - Python 3.12+ on PATH
    - Node.js + npm on PATH
    - Visual Studio 2019 or later (for MSBuild)
    - WiX Toolset v3 (https://github.com/wixtoolset/wix3/releases)

.OUTPUTS
    installer\Output\DocuLink-Setup-1.0.0.msi
#>
param(
    [string]$Version = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$RepoRoot     = Split-Path $PSScriptRoot -Parent
$InstallerDir = $PSScriptRoot

# ── Version prompt ────────────────────────────────────────────────────────────
Write-Host ""
if (-not $Version) {
    $Version = Read-Host "Enter version number for this release (e.g. 1.2.0)"
}
if (-not $Version) { Write-Error "Version is required."; exit 1 }
if ($Version -notmatch '^\d+\.\d+\.\d+$') { Write-Error "Version must be in x.y.z format."; exit 1 }
Write-Host "  Building version: $Version" -ForegroundColor Green

# ── Helper: fail with a clear message ────────────────────────────────────────
function Fail([string]$msg) {
    Write-Error "[build-installer] $msg"
    exit 1
}

function Step([string]$label) {
    Write-Host ""
    Write-Host "==> $label" -ForegroundColor Cyan
}

# ── Locate tools ─────────────────────────────────────────────────────────────

Step "Locating build tools"

# Visual Studio MSBuild (required for .NET Framework VSTO project)
$vsWherePath = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
if (-not (Test-Path $vsWherePath)) { Fail "vswhere.exe not found. Install Visual Studio." }
$vsInstall = & $vsWherePath -latest -requires Microsoft.Component.MSBuild -property installationPath 2>$null
if (-not $vsInstall) { Fail "No Visual Studio installation found by vswhere." }
$MSBuild = Join-Path $vsInstall "MSBuild\Current\Bin\MSBuild.exe"
if (-not (Test-Path $MSBuild)) { Fail "MSBuild.exe not found at $MSBuild" }
Write-Host "  MSBuild : $MSBuild"

# WiX Toolset v3
$wixInstalls = Get-ChildItem "${env:ProgramFiles(x86)}\WiX Toolset v3*" -Directory -ErrorAction SilentlyContinue |
               Sort-Object Name -Descending
if (-not $wixInstalls) {
    Fail "WiX Toolset v3 not found under '${env:ProgramFiles(x86)}\WiX Toolset v3*'.`nDownload and install it from: https://github.com/wixtoolset/wix3/releases"
}
$WixBin = Join-Path $wixInstalls[0].FullName "bin"
$Heat   = Join-Path $WixBin "heat.exe"
$Candle = Join-Path $WixBin "candle.exe"
$Light  = Join-Path $WixBin "light.exe"
foreach ($tool in @($Heat, $Candle, $Light)) {
    if (-not (Test-Path $tool)) { Fail "$tool not found." }
}
Write-Host "  WiX bin : $WixBin"

# Python
if (-not (Get-Command python -ErrorAction SilentlyContinue)) { Fail "python not found on PATH." }
$pythonVer = python --version 2>&1
Write-Host "  Python  : $pythonVer"

# ── Paths ─────────────────────────────────────────────────────────────────────
$PythonDir    = Join-Path $RepoRoot "src\python"
$PythonDist   = Join-Path $PythonDir "dist"
$AddinProj    = Join-Path $RepoRoot "src\DocuLink.Addin\DocuLink.Addin.csproj"
$ReleaseDir   = Join-Path $RepoRoot "src\DocuLink.Addin\bin\Release"
$ObjDir       = Join-Path $InstallerDir "obj"
$OutputDir    = Join-Path $InstallerDir "Output"
$FilesWxs     = Join-Path $InstallerDir "doculink-files.wxs"
$MainWxs      = Join-Path $InstallerDir "doculink.wxs"
$MsiOut       = Join-Path $OutputDir "DocuLink-Setup-$Version.msi"

# ── Step 1: Clean ─────────────────────────────────────────────────────────────
Step "Cleaning previous build artifacts"

foreach ($dir in @($PythonDist, $ReleaseDir, $ObjDir, $OutputDir)) {
    if (Test-Path $dir) {
        Write-Host "  Removing $dir"
        Remove-Item $dir -Recurse -Force
    }
}
if (Test-Path $FilesWxs) { Remove-Item $FilesWxs -Force }

# ── Step 2: Python worker (embeddable Python + scripts + tool binaries) ───────
Step "Building Python OCR worker"

$buildWorker = Join-Path $PythonDir "build-worker.ps1"
if (-not (Test-Path $buildWorker)) { Fail "build-worker.ps1 not found at $buildWorker" }

& powershell.exe -NonInteractive -ExecutionPolicy Bypass -File $buildWorker
if ($LASTEXITCODE -ne 0) { Fail "build-worker.ps1 failed (exit $LASTEXITCODE)." }

$workerPython = Join-Path $PythonDist "worker\python.exe"
if (-not (Test-Path $workerPython)) { Fail "python.exe not found after build: $workerPython" }
Write-Host "  python.exe built OK"

# ── Step 3: C# add-in Release build ──────────────────────────────────────────
Step "Building C# add-in (Release)"

& $MSBuild $AddinProj /t:Rebuild /p:Configuration=Release /p:Platform=AnyCPU /p:AppVersion=$Version /nologo /v:minimal
if ($LASTEXITCODE -ne 0) { Fail "MSBuild failed (exit $LASTEXITCODE)." }

$addinDll = Join-Path $ReleaseDir "DocuLink.Addin.dll"
if (-not (Test-Path $addinDll)) { Fail "DocuLink.Addin.dll not found after build: $addinDll" }
Write-Host "  Build output: $ReleaseDir"

# ── Step 4: Harvest Release output into WiX component group ──────────────────
Step "Harvesting Release files"

New-Item -ItemType Directory -Force $ObjDir | Out-Null
New-Item -ItemType Directory -Force $OutputDir | Out-Null

& $Heat dir $ReleaseDir `
    -nologo `
    -cg ReleaseFiles `
    -dr INSTALLDIR `
    -var var.SourceDir `
    -gg -sreg -srd `
    -out $FilesWxs

if ($LASTEXITCODE -ne 0) { Fail "heat.exe failed (exit $LASTEXITCODE)." }
Write-Host "  Harvested: $FilesWxs"

# ── Step 5: Compile .wxs → .wixobj ───────────────────────────────────────────
Step "Compiling WiX sources"

& $Candle `
    -nologo `
    "-dSourceDir=$ReleaseDir" `
    "-dProductVersion=$Version" `
    -ext WixUIExtension `
    -out "$ObjDir\" `
    $MainWxs $FilesWxs

if ($LASTEXITCODE -ne 0) { Fail "candle.exe failed (exit $LASTEXITCODE)." }

# ── Step 6: Link .wixobj → .msi ──────────────────────────────────────────────
Step "Linking MSI"

$wixObjs = Get-ChildItem $ObjDir -Filter "*.wixobj" | ForEach-Object { $_.FullName }
& $Light `
    -nologo `
    -ext WixUIExtension `
    -out $MsiOut `
    $wixObjs

if ($LASTEXITCODE -ne 0) { Fail "light.exe failed (exit $LASTEXITCODE)." }

# ── Done ─────────────────────────────────────────────────────────────────────
Write-Host ""
Write-Host "Build complete." -ForegroundColor Green
Write-Host "  MSI: $MsiOut" -ForegroundColor Green
Write-Host ""
