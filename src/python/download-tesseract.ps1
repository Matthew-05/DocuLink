<#
.SYNOPSIS
    Downloads and silently installs a pinned version of Tesseract OCR into
    src/python/tesseract/ so the Python worker build is fully self-contained.

.DESCRIPTION
    If Tesseract is already installed under Program Files, files are copied into
    src/python/tesseract/ without re-running the installer, then the Program Files
    copy is uninstalled. Otherwise the installer is downloaded and run once,
    copied into the repo-local folder, then removed from Program Files.

    The output directory (src/python/tesseract/) is gitignored. Re-run this
    script any time the directory is missing (e.g. after a fresh clone).

.EXAMPLE
    # From the repo root:
    .\src\python\download-tesseract.ps1

    # Or from src/python/:
    .\download-tesseract.ps1

.PARAMETER NoPause
    Skip the "press any key" prompt (used when invoked from build-worker.ps1).
#>

param(
    [switch]$NoPause
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Wait-ForKeyPress {
    if ($NoPause) { return }
    Write-Host ""
    Write-Host "Press any key to exit..." -ForegroundColor DarkGray
    try {
        $null = $Host.UI.RawUI.ReadKey('NoEcho,IncludeKeyDown')
    } catch {
        Read-Host "Press Enter to exit"
    }
}

function Get-TesseractDownloadUrls {
    param([string]$InstallerFilename)
    @(
        "https://github.com/tesseract-ocr/tesseract/releases/download/5.5.0/$InstallerFilename"
        "https://sourceforge.net/projects/tesseract-ocr.mirror/files/5.5.0/$InstallerFilename/download"
    )
}

function Invoke-TesseractDownload {
    param(
        [string]$Url,
        [string]$DestinationPath
    )

    # GitHub/CDN downloads often fail with default Invoke-WebRequest settings.
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

    $headers = @{
        'User-Agent' = 'DocuLink-Build/1.0 (Windows; Tesseract OCR installer download)'
    }

    $curl = Get-Command curl.exe -ErrorAction SilentlyContinue
    if ($curl) {
        $args = @(
            '-fL', '--retry', '3', '--retry-delay', '2',
            '-A', $headers['User-Agent'],
            '-o', $DestinationPath,
            $Url
        )
        & curl.exe @args
        if ($LASTEXITCODE -eq 0) { return }
        if (Test-Path $DestinationPath) { Remove-Item -Force $DestinationPath }
    }

    Invoke-WebRequest -Uri $Url -OutFile $DestinationPath -UseBasicParsing -Headers $headers
}

function Test-TesseractInstallerFile {
    param([string]$Path)
    if (-not (Test-Path $Path)) { return $false }
    $info = Get-Item $Path
    # Real installer is ~20 MB; reject HTML error pages and partial downloads.
    return ($info.Length -gt 10MB)
}

function Get-SystemTesseractDir {
    $candidates = @(
        (Join-Path ${env:ProgramFiles} "Tesseract-OCR")
    )
    if (${env:ProgramFiles(x86)}) {
        $candidates += Join-Path ${env:ProgramFiles(x86)} "Tesseract-OCR"
    }
    foreach ($dir in $candidates) {
        if (Test-Path (Join-Path $dir "tesseract.exe")) {
            return $dir
        }
    }
    return $null
}

function Copy-TesseractToRepo {
    param(
        [string]$SourceDir,
        [string]$DestDir
    )

    if (Test-Path $DestDir) {
        Remove-Item -Recurse -Force $DestDir
    }
    New-Item -ItemType Directory -Force -Path $DestDir | Out-Null

    & robocopy.exe $SourceDir $DestDir /E /NFL /NDL /NJH /NJS /nc /ns /np | Out-Null
    if ($LASTEXITCODE -ge 8) {
        throw "Failed to copy Tesseract from $SourceDir to $DestDir (robocopy exit $LASTEXITCODE)."
    }
}

function Remove-SystemTesseractInstall {
    param([string]$SystemDir)

    if ([string]::IsNullOrWhiteSpace($SystemDir) -or -not (Test-Path $SystemDir)) {
        return
    }

    Write-Host "Removing system Tesseract from: $SystemDir" -ForegroundColor Cyan

    $uninstaller = Join-Path $SystemDir "tesseract-uninstall.exe"
    if (Test-Path $uninstaller) {
        $proc = Start-Process `
            -FilePath $uninstaller `
            -ArgumentList @("/S") `
            -Wait `
            -PassThru
        if ($proc.ExitCode -ne 0) {
            Write-Warning "Uninstaller exit code $($proc.ExitCode)."
        }
    }

    if (Test-Path $SystemDir) {
        try {
            Remove-Item -Recurse -Force $SystemDir
        } catch {
            Write-Warning "Could not delete $SystemDir : $($_.Exception.Message)"
            Write-Warning "Run this script from an elevated PowerShell to remove the Program Files copy."
        }
    }

    if (-not (Get-SystemTesseractDir)) {
        Write-Host "System Tesseract removed." -ForegroundColor Green
    } else {
        Write-Warning "System Tesseract folder may still be present."
    }
}

$exitCode = 0
try {

    # Configuration
    $TesseractVersion  = "5.5.0.20241111"
    $InstallerFilename = "tesseract-ocr-w64-setup-$TesseractVersion.exe"
    $DownloadUrls      = Get-TesseractDownloadUrls -InstallerFilename $InstallerFilename

    $ScriptDir = $PSScriptRoot
    $TessDir   = Join-Path $ScriptDir "tesseract"
    $TessExe   = Join-Path $TessDir "tesseract.exe"

    # Already present in repo-local folder?
    if (Test-Path $TessExe) {
        $ver = & $TessExe --version 2>&1 | Select-Object -First 1
        Write-Host "Tesseract already present: $ver" -ForegroundColor Green
        return
    }

    # Re-use an existing system install (do not re-run the NSIS installer).
    # Re-running the installer when Tesseract is already installed opens the
    # maintenance/repair UI even with /S (tesseract-ocr#4360, #4477).
    $systemInstallDir = Get-SystemTesseractDir
    $systemDirToRemove = $null
    if ($systemInstallDir) {
        Write-Host "Found system Tesseract at: $systemInstallDir" -ForegroundColor Green
        Write-Host "Copying to repo-local folder (skipping installer)..." -ForegroundColor Cyan
        $systemDirToRemove = $systemInstallDir
        Copy-TesseractToRepo -SourceDir $systemInstallDir -DestDir $TessDir
    } else {

    # Download installer only when Tesseract is not installed on this machine.
    $tmpInstaller = Join-Path $env:TEMP $InstallerFilename

    if ((Test-Path $tmpInstaller) -and -not (Test-TesseractInstallerFile -Path $tmpInstaller)) {
        Write-Host "Removing incomplete cached installer..." -ForegroundColor DarkGray
        Remove-Item -Force $tmpInstaller
    }

    if (-not (Test-TesseractInstallerFile -Path $tmpInstaller)) {
        Write-Host "Downloading Tesseract $TesseractVersion..." -ForegroundColor Cyan
        Write-Host "  To: $tmpInstaller"

        $downloaded = $false
        $lastError  = $null

        foreach ($url in $DownloadUrls) {
            Write-Host "  Trying: $url" -ForegroundColor DarkGray
            try {
                if (Test-Path $tmpInstaller) { Remove-Item -Force $tmpInstaller }
                Invoke-TesseractDownload -Url $url -DestinationPath $tmpInstaller
                if (Test-TesseractInstallerFile -Path $tmpInstaller) {
                    $downloaded = $true
                    break
                }
                $lastError = "Downloaded file is too small (expected ~20 MB)."
                if (Test-Path $tmpInstaller) { Remove-Item -Force $tmpInstaller }
            } catch {
                $lastError = $_.Exception.Message
                if (Test-Path $tmpInstaller) { Remove-Item -Force $tmpInstaller }
            }
        }

        if (-not $downloaded) {
            throw "Could not download Tesseract installer. $lastError"
        }

        Write-Host "Download complete." -ForegroundColor Green
    } else {
        Write-Host "Installer already cached at $tmpInstaller" -ForegroundColor DarkGray
    }

    # Run installer only on machines with no system Tesseract.
    # Note: /S is best-effort; some 5.5.x builds still show UI (tesseract-ocr#4477).
    Write-Host ""
    Write-Host "No system Tesseract found. Running installer..." -ForegroundColor Cyan
    Write-Host "If a setup window appears, complete it once; later runs will copy only." -ForegroundColor DarkGray

    $proc = Start-Process `
        -FilePath $tmpInstaller `
        -ArgumentList @("/S") `
        -Wait `
        -PassThru

    if ($proc.ExitCode -ne 0) {
        throw "Tesseract installer exited with code $($proc.ExitCode)."
    }

    $systemInstallDir = Get-SystemTesseractDir
    if (-not $systemInstallDir) {
        throw "Installer finished but tesseract.exe was not found under Program Files\Tesseract-OCR."
    }

    Write-Host "Copying Tesseract to: $TessDir" -ForegroundColor Cyan
    $systemDirToRemove = $systemInstallDir
    Copy-TesseractToRepo -SourceDir $systemInstallDir -DestDir $TessDir

    } # end else (no system install)

    if (-not (Test-Path $TessExe)) {
        throw "tesseract.exe not found at: $TessExe"
    }

    $ver = & $TessExe --version 2>&1 | Select-Object -First 1
    Write-Host "Tesseract installed: $ver" -ForegroundColor Green

    $engData = Join-Path $TessDir "tessdata\eng.traineddata"
    if (-not (Test-Path $engData)) {
        Write-Warning "eng.traineddata not found at $engData - OCR may fail at runtime."
    } else {
        Write-Host "eng.traineddata present." -ForegroundColor Green
    }

    if ($systemDirToRemove) {
        Remove-SystemTesseractInstall -SystemDir $systemDirToRemove
    }

    Write-Host ""
    Write-Host "Tesseract ready at: $TessDir" -ForegroundColor Green

} catch {
    Write-Host $_.Exception.Message -ForegroundColor Red
    $exitCode = 1
} finally {
    Wait-ForKeyPress
}

exit $exitCode
