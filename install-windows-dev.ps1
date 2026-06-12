<#
.SYNOPSIS
  Build this Pinta source as a self-contained Release and install it over the
  system Pinta, matching the official installer layout (icons/locale under
  share/). Re-run after code changes to update the installed copy.

.DESCRIPTION
  Mirrors the official Windows build (.github/workflows/build.yml): publishes
  self-contained, then moves icons/locale from bin into share and adds the
  hicolor index.theme. Then it replaces the bin/lib/share folders of the
  installed Pinta. The Program Files copy needs admin, so the script
  re-launches itself elevated for that phase only (one UAC prompt).

  Run from a normal PowerShell prompt:  .\install-windows-dev.ps1
#>
[CmdletBinding()]
param(
    # Installed Pinta location (default: system Pinta).
    [string] $InstallDir = (Join-Path $env:ProgramFiles 'Pinta'),
    # Runtime identifier to publish for.
    [string] $Rid = 'win-x64',
    # Optional: back up the current install here once, before the first replace.
    [string] $BackupDir = '',
    # Internal: the elevated copy phase re-invokes the script with this.
    [switch] $CopyPhase,
    [string] $ReleaseDir = ''
)

$ErrorActionPreference = 'Stop'

# --- Elevated copy phase: replace bin/lib/share in the install dir -----------
if ($CopyPhase) {
    if ($BackupDir -and -not (Test-Path $BackupDir) -and (Test-Path $InstallDir)) {
        Copy-Item $InstallDir $BackupDir -Recurse -Force
        Write-Host "Backed up $InstallDir -> $BackupDir"
    }
    foreach ($d in 'bin', 'lib', 'share') {
        $target = Join-Path $InstallDir $d
        if (Test-Path $target) { Remove-Item $target -Recurse -Force }
        Copy-Item (Join-Path $ReleaseDir $d) $target -Recurse -Force
    }
    Write-Host "Installed to $InstallDir"
    return
}

# --- Build phase (no admin needed) ------------------------------------------
$repo    = $PSScriptRoot
$release = Join-Path $repo 'release'

Write-Host "Closing any running Pinta..."
Get-Process Pinta -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

Write-Host "Publishing self-contained Release ($Rid)..."
if (Test-Path $release) { Remove-Item $release -Recurse -Force }
& dotnet publish (Join-Path $repo 'Pinta\Pinta.csproj') `
    -c Release -r $Rid --self-contained true -p:PublishDir=../release/bin
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit $LASTEXITCODE)" }

# Match the official layout: icons/locale live under share/, not bin/.
foreach ($d in 'icons', 'locale') {
    $from = Join-Path $release "bin\$d"
    if (Test-Path $from) {
        Copy-Item $from (Join-Path $release 'share') -Recurse -Force
        Remove-Item $from -Recurse -Force
    }
}
# index.theme so GTK recognizes the hicolor theme directory.
$indexTheme = Join-Path $repo 'installer\macos\hicolor.index.theme'
$hicolorDir = Join-Path $release 'share\icons\hicolor'
if ((Test-Path $indexTheme) -and (Test-Path $hicolorDir)) {
    Copy-Item $indexTheme (Join-Path $hicolorDir 'index.theme') -Force
}

# --- Install phase (elevated) -----------------------------------------------
Write-Host "Requesting elevation to copy into $InstallDir ..."
$elevArgs = @(
    '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"",
    '-CopyPhase',
    '-InstallDir', "`"$InstallDir`"",
    '-ReleaseDir', "`"$release`""
)
if ($BackupDir) { $elevArgs += @('-BackupDir', "`"$BackupDir`"") }
$p = Start-Process powershell -Verb RunAs -Wait -PassThru -ArgumentList $elevArgs
if ($p.ExitCode -ne 0) { throw "Elevated install failed (exit $($p.ExitCode))." }

Write-Host "Done. Launch Pinta from its usual shortcut." -ForegroundColor Green
