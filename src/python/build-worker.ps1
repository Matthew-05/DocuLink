<#
.SYNOPSIS
    Builds the DocuLink OCR worker using Python embeddable distribution.

.DESCRIPTION
    Prerequisites:
      - Internet access on first run (Python embeddable zip, Tesseract, and
        Ghostscript are downloaded automatically if not already present).
        Override tool locations with $env:TESSERACT_DIR or $env:GHOSTSCRIPT_DIR.

    Output:
      src/python/dist/worker-runtime.zip (Python + scripts + tool binaries)

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

$scriptDir = $PSScriptRoot
$exitCode = 0

$PythonEmbedVersion = "3.12.10"
$PythonEmbedUrl = "https://www.python.org/ftp/python/$PythonEmbedVersion/python-$PythonEmbedVersion-embed-amd64.zip"

try {

# ── Ensure Tesseract is present (downloads if needed) ─────────────────────────
$defaultTessDir = Join-Path $scriptDir "tesseract"

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

# ── Set up output directory ───────────────────────────────────────────────────
$workerDir = Join-Path $scriptDir "dist\worker"
if (Test-Path $workerDir) {
    Remove-Item $workerDir -Recurse -Force
}
New-Item -ItemType Directory -Force $workerDir | Out-Null

# ── Download and extract Python embeddable ────────────────────────────────────
Write-Host "`nDownloading Python $PythonEmbedVersion embeddable package..." -ForegroundColor Cyan
$embedZip = Join-Path $workerDir "python-embed.zip"
Invoke-WebRequest $PythonEmbedUrl -OutFile $embedZip
Expand-Archive $embedZip -DestinationPath $workerDir -Force
Remove-Item $embedZip
Write-Host "Python embeddable extracted." -ForegroundColor Green

# ── Enable site-packages in the _pth file ─────────────────────────────────────
$pthFile = Get-Item (Join-Path $workerDir "python312._pth")
$pthContent = Get-Content $pthFile.FullName -Raw
$pthContent = $pthContent -replace '#import site', 'import site'
$utf8NoBom = New-Object System.Text.UTF8Encoding $false
[System.IO.File]::WriteAllText($pthFile.FullName, $pthContent, $utf8NoBom)
Write-Host "Enabled site-packages in $($pthFile.Name)." -ForegroundColor Green

# ── Bootstrap pip ─────────────────────────────────────────────────────────────
Write-Host "`nBootstrapping pip..." -ForegroundColor Cyan
$getPipScript = Join-Path $workerDir "get-pip.py"
Invoke-WebRequest "https://bootstrap.pypa.io/get-pip.py" -OutFile $getPipScript
& (Join-Path $workerDir "python.exe") $getPipScript --quiet
if ($LASTEXITCODE -ne 0) { throw "pip bootstrap failed." }
Remove-Item $getPipScript
Write-Host "pip installed." -ForegroundColor Green

# ── Install Python dependencies ───────────────────────────────────────────────
# setuptools and wheel first. The embeddable distribution ships neither, and pip
# needs setuptools.build_meta the moment a requirement resolves to a source
# distribution rather than a wheel — the failure is a bare
# "Cannot import 'setuptools.build_meta'" a long way from the package that
# caused it. Prefer wheel-only dependencies in requirements.txt regardless; this
# is here so an unavoidable sdist does not break the build outright.
Write-Host "`nInstalling build prerequisites..." -ForegroundColor Cyan
& (Join-Path $workerDir "python.exe") -m pip install --upgrade setuptools wheel --quiet --no-warn-script-location
if ($LASTEXITCODE -ne 0) { throw "Could not install setuptools and wheel." }

Write-Host "`nInstalling Python dependencies..." -ForegroundColor Cyan
& (Join-Path $workerDir "python.exe") -m pip install -r (Join-Path $scriptDir "requirements.txt") --quiet --no-warn-script-location
if ($LASTEXITCODE -ne 0) { throw "pip install failed." }
Write-Host "Dependencies installed." -ForegroundColor Green

# ── Copy Python source files ──────────────────────────────────────────────────
Write-Host "`nCopying worker scripts..." -ForegroundColor Cyan
Copy-Item (Join-Path $scriptDir "worker.py") $workerDir
Copy-Item (Join-Path $scriptDir "engines")  (Join-Path $workerDir "engines")  -Recurse -Force
Copy-Item (Join-Path $scriptDir "schemas")  (Join-Path $workerDir "schemas")  -Recurse -Force

# ── Copy tool binaries ────────────────────────────────────────────────────────
Write-Host "Copying Tesseract..." -ForegroundColor Cyan
Copy-Item $tessDir (Join-Path $workerDir "tesseract") -Recurse -Force

Write-Host "Copying Ghostscript..." -ForegroundColor Cyan
Copy-Item $gsDir (Join-Path $workerDir "ghostscript") -Recurse -Force

# ── Verify output ─────────────────────────────────────────────────────────────
$outputExe = Join-Path $workerDir "python.exe"
if (Test-Path $outputExe) {
    # Build tools, documentation and bytecode caches are not needed at runtime.
    # Removing them saves substantial space and thousands of extracted files.
    Write-Host "`nPruning build-only runtime files..." -ForegroundColor Cyan

    $sitePackages = Join-Path $workerDir "Lib\site-packages"
    $prunePaths = @(
        (Join-Path $sitePackages "pip"),
        (Join-Path $sitePackages "setuptools"),
        (Join-Path $sitePackages "wheel"),
        (Join-Path $sitePackages "_distutils_hack"),
        (Join-Path $sitePackages "distutils-precedence.pth"),
        (Join-Path $workerDir "ghostscript\doc"),
        (Join-Path $workerDir "ghostscript\examples"),
        (Join-Path $workerDir 'ghostscript\$PLUGINSDIR'),
        (Join-Path $workerDir "ghostscript\vcredist_x64.exe"),
        (Join-Path $workerDir "ghostscript\uninstgs.exe.nsis"),
        (Join-Path $workerDir "ghostscript\bin\gswin64.exe"),
        (Join-Path $workerDir "ghostscript\bin\gsdll64.lib")
    )

    $prunePaths += Get-ChildItem $sitePackages -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match '^(pip|setuptools|wheel)-.*\.dist-info$' } |
        ForEach-Object { $_.FullName }
    $prunePaths += Get-ChildItem (Join-Path $workerDir "Scripts") -File -Filter "pip*.exe" -ErrorAction SilentlyContinue |
        ForEach-Object { $_.FullName }
    $prunePaths += Get-ChildItem (Join-Path $workerDir "tesseract") -File -Filter "*.exe" -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -ne "tesseract.exe" } |
        ForEach-Object { $_.FullName }

    foreach ($path in $prunePaths) {
        if (Test-Path -LiteralPath $path) {
            Remove-Item -LiteralPath $path -Recurse -Force
        }
    }

    # Python is launched with -B by the host, so these caches are neither needed
    # in the archive nor recreated in the per-user runtime cache.
    Get-ChildItem -LiteralPath $workerDir -Directory -Recurse -Filter "__pycache__" -ErrorAction SilentlyContinue |
        Sort-Object FullName -Descending |
        Remove-Item -Recurse -Force
    Get-ChildItem -LiteralPath $workerDir -File -Recurse -Filter "*.pyc" -ErrorAction SilentlyContinue |
        Remove-Item -Force

    $archivePath = Join-Path $scriptDir "dist\worker-runtime.zip"
    if (Test-Path -LiteralPath $archivePath) {
        Remove-Item -LiteralPath $archivePath -Force
    }

    Write-Host "Packing OCR runtime into a single installer payload..." -ForegroundColor Cyan
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem

    # Create a deterministic archive: stable ordering and timestamps make its
    # SHA-256 (and therefore the runtime cache key) stay unchanged when the
    # runtime contents have not changed between DocuLink releases.
    $archiveStream = [System.IO.File]::Open(
        $archivePath,
        [System.IO.FileMode]::CreateNew,
        [System.IO.FileAccess]::Write,
        [System.IO.FileShare]::None)
    $archive = New-Object System.IO.Compression.ZipArchive(
        $archiveStream,
        [System.IO.Compression.ZipArchiveMode]::Create,
        $false)
    $fixedTimestamp = New-Object System.DateTimeOffset(
        2000, 1, 1, 0, 0, 0, [System.TimeSpan]::Zero)

    try {
        $runtimeFiles = Get-ChildItem -LiteralPath $workerDir -File -Recurse |
            Sort-Object FullName

        foreach ($file in $runtimeFiles) {
            $relativePath = $file.FullName.Substring($workerDir.Length + 1).Replace('\', '/')
            $entry = $archive.CreateEntry(
                $relativePath,
                [System.IO.Compression.CompressionLevel]::Optimal)
            $entry.LastWriteTime = $fixedTimestamp

            $input = $file.OpenRead()
            $output = $entry.Open()
            try {
                $input.CopyTo($output)
            } finally {
                $output.Dispose()
                $input.Dispose()
            }
        }
    } finally {
        $archive.Dispose()
        $archiveStream.Dispose()
    }

    $archiveSizeMb = [math]::Round((Get-Item -LiteralPath $archivePath).Length / 1MB, 1)
    Write-Host "`nBuild complete: $archivePath ($archiveSizeMb MB)" -ForegroundColor Green
    Write-Host "Build the C# project to copy the runtime archive into the add-in output."
} else {
    throw "Build appeared to succeed but python.exe not found at: $outputExe"
}

} catch {
    Write-Host $_.Exception.Message -ForegroundColor Red
    $exitCode = 1
}

exit $exitCode
