<#
.SYNOPSIS
    Builds the DocuLink OCR worker as a self-contained PyInstaller bundle.

.DESCRIPTION
    Prerequisites:
      - Python 3.12 on PATH (https://www.python.org/downloads/)
      - Internet access on first run (Tesseract and Ghostscript are downloaded
        automatically to src/python/tesseract/ and src/python/ghostscript/ via
        download-tesseract.ps1 and download-ghostscript.ps1 if not already present).
        Override locations with $env:TESSERACT_DIR or $env:GHOSTSCRIPT_DIR.

    Output:
      src/python/dist/worker/worker.exe  (plus supporting DLLs and tessdata)

    The dist/ folder is gitignored. After building, the C# project copies
    the worker to the addin output directory automatically on the next build.

.EXAMPLE
    # From the repo root:
    .\src\python\build-worker.ps1

    # Custom tool paths:
    $env:TESSERACT_DIR = "D:\Tools\Tesseract-OCR"
    $env:GHOSTSCRIPT_DIR = "D:\Tools\gs\gs10.04.0"
    .\src\python\build-worker.ps1
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Wait-ForKeyPress {
    Write-Host ""
    Write-Host "Press any key to exit..." -ForegroundColor DarkGray
    try {
        $null = $Host.UI.RawUI.ReadKey("NoEcho,IncludeKeyDown")
    } catch {
        Read-Host "Press Enter to exit"
    }
}

$scriptDir = $PSScriptRoot
$exitCode = 0

try {

# ── Verify Python 3.12 ───────────────────────────────────────────────────────
$pyVersion = & python --version 2>&1
if ($LASTEXITCODE -ne 0) {
    throw "Python not found on PATH. Install Python 3.12 from https://www.python.org/downloads/"
}

if ($pyVersion -notmatch 'Python 3\.12\.\d+') {
    throw "Python 3.12 is required. Found: $pyVersion. Install Python 3.12 from https://www.python.org/downloads/"
}

Write-Host "Found $pyVersion" -ForegroundColor Green

# ── Ensure Tesseract is present (downloads if needed) ─────────────────────────
$defaultTessDir = Join-Path $scriptDir "tesseract"

# Allow override via env var, otherwise use the repo-local copy
$tessDir = $env:TESSERACT_DIR
if (-not $tessDir) {
    $tessDir = $defaultTessDir
}

if (-not (Test-Path (Join-Path $tessDir "tesseract.exe"))) {
    if ($tessDir -eq $defaultTessDir) {
        Write-Host "`nTesseract not found - running download-tesseract.ps1..." -ForegroundColor Cyan
        & (Join-Path $scriptDir "download-tesseract.ps1") -NoPause
        if ($LASTEXITCODE -ne 0) {
            throw "Tesseract download failed. Aborting build."
        }
    } else {
        throw "TESSERACT_DIR is set to '$tessDir' but tesseract.exe was not found there."
    }
} else {
    Write-Host "Found Tesseract at: $tessDir" -ForegroundColor Green
}

$env:TESSERACT_DIR = $tessDir

# ── Ensure Ghostscript is present (downloads if needed) ───────────────────────
$defaultGsDir = Join-Path $scriptDir "ghostscript"

$gsDir = $env:GHOSTSCRIPT_DIR
if (-not $gsDir) {
    $gsDir = $defaultGsDir
}

if (-not (Test-Path (Join-Path $gsDir "bin\gswin64c.exe"))) {
    if ($gsDir -eq $defaultGsDir) {
        Write-Host "`nGhostscript not found - running download-ghostscript.ps1..." -ForegroundColor Cyan
        & (Join-Path $scriptDir "download-ghostscript.ps1") -NoPause
        if ($LASTEXITCODE -ne 0) {
            throw "Ghostscript download failed. Aborting build."
        }
    } else {
        throw "GHOSTSCRIPT_DIR is set to '$gsDir' but bin\gswin64c.exe was not found there."
    }
} else {
    Write-Host "Found Ghostscript at: $gsDir" -ForegroundColor Green
}

$env:GHOSTSCRIPT_DIR = $gsDir

# ── Install Python dependencies ───────────────────────────────────────────────
Write-Host "`nInstalling Python dependencies..." -ForegroundColor Cyan
Push-Location $scriptDir
try {
    & python -m pip install --upgrade pip --quiet
    & python -m pip install -r requirements.txt --quiet
    if ($LASTEXITCODE -ne 0) {
        throw "pip install failed."
    }
} finally {
    Pop-Location
}
Write-Host "Dependencies installed." -ForegroundColor Green

# ── Run PyInstaller ───────────────────────────────────────────────────────────
Write-Host "`nRunning PyInstaller..." -ForegroundColor Cyan
Push-Location $scriptDir
try {
    & python -m PyInstaller worker.spec --clean --noconfirm
    if ($LASTEXITCODE -ne 0) {
        throw "PyInstaller failed."
    }
} finally {
    Pop-Location
}

$outputExe = Join-Path $scriptDir "dist\worker\worker.exe"
if (Test-Path $outputExe) {
    Write-Host "`nBuild complete: $outputExe" -ForegroundColor Green
    Write-Host "Build the C# project to automatically copy the worker to the addin output."
} else {
    throw "Build appeared to succeed but worker.exe not found at: $outputExe"
}

} catch {
    Write-Host $_.Exception.Message -ForegroundColor Red
    $exitCode = 1
} finally {
    Wait-ForKeyPress
}

exit $exitCode
