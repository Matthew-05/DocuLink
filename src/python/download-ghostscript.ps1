<#
.SYNOPSIS
    Downloads and silently installs a pinned version of Ghostscript into
    src/python/ghostscript/ so the Python worker build is fully self-contained.

.DESCRIPTION
    If Ghostscript is already installed under Program Files, files are copied into
    src/python/ghostscript/ without re-running the installer, then the Program Files
    copy is uninstalled. Otherwise the installer EXE is downloaded and its payload
    is extracted with 7-Zip (Artifex disabled NSIS /S silent install in 10.01+).
    Uses 7-Zip from PATH/Program Files, or downloads 7zr.exe to %TEMP% if needed.

    The output directory (src/python/ghostscript/) is gitignored. Re-run this
    script any time the directory is missing (e.g. after a fresh clone).

.EXAMPLE
    # From the repo root:
    .\src\python\download-ghostscript.ps1

    # Or from src/python/:
    .\download-ghostscript.ps1

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

function Get-GhostscriptDownloadUrls {
    param([string]$InstallerFilename)
    @(
        "https://github.com/ArtifexSoftware/ghostpdl-downloads/releases/download/gs10040/$InstallerFilename"
    )
}

function Invoke-GhostscriptDownload {
    param(
        [string]$Url,
        [string]$DestinationPath
    )

    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

    $headers = @{
        'User-Agent' = 'DocuLink-Build/1.0 (Windows; Ghostscript installer download)'
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

function Test-GhostscriptInstallerFile {
    param([string]$Path)
    if (-not (Test-Path $Path)) { return $false }
    $info = Get-Item $Path
    # Real installer is ~60 MB; reject HTML error pages and partial downloads.
    return ($info.Length -gt 40MB)
}

function Get-SystemGhostscriptDir {
    $gsRoot = Join-Path ${env:ProgramFiles} "gs"
    if (-not (Test-Path $gsRoot)) { return $null }

    $bestDir = $null
    $bestVersion = [version]"0.0.0"

    foreach ($dir in Get-ChildItem $gsRoot -Directory) {
        $binExe = Join-Path $dir.FullName "bin\gswin64c.exe"
        if (-not (Test-Path $binExe)) { continue }

        $verStr = $dir.Name
        if ($verStr -match '^gs(.+)$') {
            $verStr = $Matches[1]
        }
        try {
            $ver = [version]$verStr
        } catch {
            $ver = [version]"0.0.0"
        }

        if ($ver -ge $bestVersion) {
            $bestVersion = $ver
            $bestDir = $dir.FullName
        }
    }

    return $bestDir
}

function Find-GhostscriptInstallRoot {
    param([string]$SearchRoot)

    if (-not (Test-Path $SearchRoot)) { return $null }

    $directExe = Join-Path $SearchRoot "bin\gswin64c.exe"
    if (Test-Path $directExe) { return $SearchRoot }

    foreach ($dir in Get-ChildItem $SearchRoot -Directory -ErrorAction SilentlyContinue) {
        $exe = Join-Path $dir.FullName "bin\gswin64c.exe"
        if (Test-Path $exe) { return $dir.FullName }
    }

    return $null
}

function Stop-StuckGhostscriptInstaller {
    # Prior runs may hang on NSIS /S (disabled in Ghostscript 10.01+).
    Get-Process -Name 'gs10040w64' -ErrorAction SilentlyContinue | ForEach-Object {
        Write-Host "Stopping stuck installer process (PID $($_.Id))..." -ForegroundColor DarkGray
        Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue
    }
}

function Get-SevenZipExecutable {
    $fromPath = Get-Command 7z.exe -ErrorAction SilentlyContinue
    if ($fromPath) { return $fromPath.Source }

    foreach ($candidate in @(
        (Join-Path ${env:ProgramFiles} "7-Zip\7z.exe")
        (Join-Path ${env:ProgramFiles(x86)} "7-Zip\7z.exe")
    )) {
        if ($candidate -and (Test-Path $candidate)) { return $candidate }
    }

    return $null
}

function Ensure-SevenZipExecutable {
    $existing = Get-SevenZipExecutable
    if ($existing) { return $existing }

    $toolsDir = Join-Path $env:TEMP "DocuLink-build-tools"
    $sevenZip = Join-Path $toolsDir "7zr.exe"
    if (Test-Path $sevenZip) { return $sevenZip }

    New-Item -ItemType Directory -Force -Path $toolsDir | Out-Null
    $url = "https://www.7-zip.org/a/7zr.exe"
    Write-Host "Downloading 7zr.exe (needed to unpack Ghostscript)..." -ForegroundColor Cyan
    Invoke-GhostscriptDownload -Url $url -DestinationPath $sevenZip

    if (-not (Test-Path $sevenZip) -or (Get-Item $sevenZip).Length -lt 100KB) {
        throw "Failed to download 7zr.exe from $url"
    }

    return $sevenZip
}

function Invoke-ExtractGhostscriptInstaller {
    param(
        [string]$InstallerPath,
        [string]$ExtractDir
    )

    if (Test-Path $ExtractDir) {
        Remove-Item -Recurse -Force $ExtractDir
    }
    New-Item -ItemType Directory -Force -Path $ExtractDir | Out-Null

    $sevenZip = Ensure-SevenZipExecutable
    $extractDir = [System.IO.Path]::GetFullPath($ExtractDir)

    Write-Host "Extracting Ghostscript with 7-Zip (NSIS /S is disabled in 10.01+)..." -ForegroundColor Cyan
    Write-Host "  Tool: $sevenZip" -ForegroundColor DarkGray
    Write-Host "  Into: $extractDir" -ForegroundColor DarkGray

    & $sevenZip x $InstallerPath "-o$extractDir" -y | Out-Null
    # 7-Zip: 0 = OK, 1 = warning (e.g. harmless), 2+ = error
    if ($LASTEXITCODE -ge 2) {
        throw "7-Zip extraction failed with exit code $LASTEXITCODE."
    }

    $root = Find-GhostscriptInstallRoot -SearchRoot $extractDir
    if (-not $root) {
        throw "Extraction finished but gswin64c.exe was not found under $extractDir."
    }

    return $root
}

function Remove-GhostscriptTree {
    param([string]$Dir)

    if ([string]::IsNullOrWhiteSpace($Dir) -or -not (Test-Path $Dir)) {
        return
    }

    $uninstaller = Join-Path $Dir "uninstgs.exe"
    if (Test-Path $uninstaller) {
        $proc = Start-Process `
            -FilePath $uninstaller `
            -ArgumentList @('/S') `
            -Wait `
            -PassThru `
            -WindowStyle Hidden
        if ($proc.ExitCode -ne 0) {
            Write-Warning "Silent uninstaller exit code $($proc.ExitCode)."
        }
    }

    if (Test-Path $Dir) {
        Remove-Item -Recurse -Force $Dir -ErrorAction SilentlyContinue
    }
}

function Copy-GhostscriptToRepo {
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
        throw "Failed to copy Ghostscript from $SourceDir to $DestDir (robocopy exit $LASTEXITCODE)."
    }
}

function Remove-SystemGhostscriptInstall {
    param([string]$SystemDir)

    if ([string]::IsNullOrWhiteSpace($SystemDir) -or -not (Test-Path $SystemDir)) {
        return
    }

    Write-Host "Removing system Ghostscript from: $SystemDir" -ForegroundColor Cyan

    $uninstaller = Join-Path $SystemDir "uninstgs.exe"
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

    if (-not (Get-SystemGhostscriptDir)) {
        Write-Host "System Ghostscript removed." -ForegroundColor Green
    } else {
        Write-Warning "System Ghostscript folder may still be present."
    }
}

$exitCode = 0
try {

    # Configuration — keep in sync with worker.spec / ocrmypdf minimum (9.54+)
    $GhostscriptVersion  = "10.04.0"
    $InstallerFilename   = "gs10040w64.exe"
    $DownloadUrls        = Get-GhostscriptDownloadUrls -InstallerFilename $InstallerFilename

    $ScriptDir = $PSScriptRoot
    $GsDir     = Join-Path $ScriptDir "ghostscript"
    $GsExe     = Join-Path $GsDir "bin\gswin64c.exe"

    if (Test-Path $GsExe) {
        $ver = & $GsExe --version 2>&1 | Select-Object -First 1
        Write-Host "Ghostscript already present: $ver" -ForegroundColor Green
        return
    }

    $systemInstallDir = Get-SystemGhostscriptDir
    $systemDirToRemove = $null
    if ($systemInstallDir) {
        Write-Host "Found system Ghostscript at: $systemInstallDir" -ForegroundColor Green
        Write-Host "Copying to repo-local folder (skipping installer)..." -ForegroundColor Cyan
        $systemDirToRemove = $systemInstallDir
        Copy-GhostscriptToRepo -SourceDir $systemInstallDir -DestDir $GsDir
    } else {

    $tmpInstaller = Join-Path $env:TEMP $InstallerFilename

    if ((Test-Path $tmpInstaller) -and -not (Test-GhostscriptInstallerFile -Path $tmpInstaller)) {
        Write-Host "Removing incomplete cached installer..." -ForegroundColor DarkGray
        Remove-Item -Force $tmpInstaller
    }

    if (-not (Test-GhostscriptInstallerFile -Path $tmpInstaller)) {
        Write-Host "Downloading Ghostscript $GhostscriptVersion..." -ForegroundColor Cyan
        Write-Host "  To: $tmpInstaller"

        $downloaded = $false
        $lastError  = $null

        foreach ($url in $DownloadUrls) {
            Write-Host "  Trying: $url" -ForegroundColor DarkGray
            try {
                if (Test-Path $tmpInstaller) { Remove-Item -Force $tmpInstaller }
                Invoke-GhostscriptDownload -Url $url -DestinationPath $tmpInstaller
                if (Test-GhostscriptInstallerFile -Path $tmpInstaller) {
                    $downloaded = $true
                    break
                }
                $lastError = "Downloaded file is too small (expected ~60 MB)."
                if (Test-Path $tmpInstaller) { Remove-Item -Force $tmpInstaller }
            } catch {
                $lastError = $_.Exception.Message
                if (Test-Path $tmpInstaller) { Remove-Item -Force $tmpInstaller }
            }
        }

        if (-not $downloaded) {
            throw "Could not download Ghostscript installer. $lastError"
        }

        Write-Host "Download complete." -ForegroundColor Green
    } else {
        Write-Host "Installer already cached at $tmpInstaller" -ForegroundColor DarkGray
    }

    Stop-StuckGhostscriptInstaller

    $stagingRoot = Join-Path $env:TEMP "DocuLink-ghostscript-staging"

    try {
        $installedDir = Invoke-ExtractGhostscriptInstaller `
            -InstallerPath $tmpInstaller `
            -ExtractDir $stagingRoot

        Write-Host "Copying Ghostscript to: $GsDir" -ForegroundColor Cyan
        Copy-GhostscriptToRepo -SourceDir $installedDir -DestDir $GsDir
    } finally {
        if (Test-Path $stagingRoot) {
            Write-Host "Removing staging files..." -ForegroundColor DarkGray
            Remove-GhostscriptTree -Dir $stagingRoot
        }
    }

    } # end else (no system install)

    if (-not (Test-Path $GsExe)) {
        throw "gswin64c.exe not found at: $GsExe"
    }

    $ver = & $GsExe --version 2>&1 | Select-Object -First 1
    Write-Host "Ghostscript installed: $ver" -ForegroundColor Green

    $resourceDir = Join-Path $GsDir "Resource"
    if (-not (Test-Path $resourceDir)) {
        Write-Warning "Resource folder not found at $resourceDir - Ghostscript may fail at runtime."
    } else {
        Write-Host "Resource folder present." -ForegroundColor Green
    }

    if ($systemDirToRemove) {
        Remove-SystemGhostscriptInstall -SystemDir $systemDirToRemove
    }

    Write-Host ""
    Write-Host "Ghostscript ready at: $GsDir" -ForegroundColor Green

} catch {
    Write-Host $_.Exception.Message -ForegroundColor Red
    $exitCode = 1
} finally {
    Wait-ForKeyPress
}

exit $exitCode
