#Requires -Version 5.1
<#
.SYNOPSIS
    Build DocuLink, tag the release, and publish to GitHub.

.DESCRIPTION
    Full release pipeline in one command:
      1. Verify git is clean and on main
      2. Build the MSI via build-installer.ps1
      3. Commit any pending changes, create and push a v{version} tag
      4. Create a GitHub release and upload the MSI

.PREREQUISITES
    - All build-installer.ps1 prerequisites (Python, Node, VS, WiX)
    - GitHub CLI (gh) installed and authenticated (gh auth login)

.EXAMPLE
    .\release.ps1
    .\release.ps1 -Version 1.2.0
#>
param(
    [string]$Version = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$RepoRoot     = Split-Path $PSScriptRoot -Parent
$InstallerDir = $PSScriptRoot

function Step([string]$label) {
    Write-Host ""
    Write-Host "==> $label" -ForegroundColor Cyan
}

function Fail([string]$msg) {
    Write-Host ""
    Write-Host "ERROR: $msg" -ForegroundColor Red
    exit 1
}

# ── Verify prerequisites ──────────────────────────────────────────────────────
Step "Checking prerequisites"

if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
    Fail "GitHub CLI (gh) not found. Install from https://cli.github.com and run 'gh auth login'."
}

$authStatus = gh auth status 2>&1
if ($LASTEXITCODE -ne 0) {
    Fail "Not authenticated with GitHub. Run 'gh auth login' first."
}
Write-Host "  gh CLI: OK"

# ── Verify git state ──────────────────────────────────────────────────────────
Step "Checking git state"

Push-Location $RepoRoot
try {
    $branch = git rev-parse --abbrev-ref HEAD
    if ($branch -ne "main") {
        Fail "Not on main branch (currently on '$branch'). Switch to main before releasing."
    }
    Write-Host "  Branch: main"

    $status = git status --porcelain
    if ($status) {
        Write-Host ""
        Write-Host "  Uncommitted changes:" -ForegroundColor Yellow
        $status | ForEach-Object { Write-Host "    $_" }
        Write-Host ""
        $proceed = Read-Host "There are uncommitted changes. Continue anyway? (y/N)"
        if ($proceed -notmatch '^[Yy]$') { Fail "Aborted." }
    } else {
        Write-Host "  Working tree: clean"
    }
} finally {
    Pop-Location
}

# ── Version ───────────────────────────────────────────────────────────────────
Write-Host ""
if (-not $Version) {
    $Version = Read-Host "Enter version number (e.g. 1.2.0)"
}
if (-not $Version) { Fail "Version is required." }
if ($Version -notmatch '^\d+\.\d+\.\d+$') { Fail "Version must be in x.y.z format." }

$Tag = "v$Version"

# Check tag doesn't already exist
$existingTag = git tag -l $Tag
if ($existingTag) {
    Fail "Tag $Tag already exists. Delete it first if you want to re-release."
}

Write-Host "  Version : $Version"
Write-Host "  Tag     : $Tag"

# ── Release notes ─────────────────────────────────────────────────────────────
Write-Host ""
Write-Host "Enter release notes (press Enter twice when done):" -ForegroundColor Yellow
$lines = [System.Collections.Generic.List[string]]::new()
while ($true) {
    $line = Read-Host
    if ($line -eq "" -and $lines.Count -gt 0 -and $lines[-1] -eq "") { break }
    $lines.Add($line)
}
# Trim trailing blank lines
while ($lines.Count -gt 0 -and $lines[-1] -eq "") { $lines.RemoveAt($lines.Count - 1) }
$ReleaseNotes = if ($lines.Count -gt 0) { $lines -join "`n" } else { "Release $Tag" }

# ── Build MSI ─────────────────────────────────────────────────────────────────
Step "Building MSI (version $Version)"

$buildScript = Join-Path $InstallerDir "build-installer.ps1"
& powershell.exe -NonInteractive -ExecutionPolicy Bypass -File $buildScript -Version $Version
if ($LASTEXITCODE -ne 0) { Fail "build-installer.ps1 failed." }

$MsiPath = Join-Path $InstallerDir "Output\DocuLink-Setup-$Version.msi"
if (-not (Test-Path $MsiPath)) { Fail "MSI not found at $MsiPath" }
Write-Host "  MSI: $MsiPath" -ForegroundColor Green

# ── Tag and push ──────────────────────────────────────────────────────────────
Step "Creating and pushing git tag $Tag"

Push-Location $RepoRoot
try {
    git tag $Tag
    if ($LASTEXITCODE -ne 0) { Fail "git tag failed." }

    git push origin $Tag
    if ($LASTEXITCODE -ne 0) { Fail "git push tag failed." }

    Write-Host "  Pushed $Tag to origin"
} finally {
    Pop-Location
}

# ── Create GitHub release and upload MSI ─────────────────────────────────────
Step "Creating GitHub release $Tag"

$releaseUrl = gh release create $Tag $MsiPath `
    --title "DocuLink $Tag" `
    --notes $ReleaseNotes
if ($LASTEXITCODE -ne 0) { Fail "gh release create failed." }

# ── Done ─────────────────────────────────────────────────────────────────────
Write-Host ""
Write-Host "Release published successfully!" -ForegroundColor Green
Write-Host "  $releaseUrl" -ForegroundColor Green
Write-Host ""
