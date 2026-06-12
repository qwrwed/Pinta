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
    # Skip launching the installed Pinta after install.
    [switch] $NoLaunch,
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
        $source = Join-Path $ReleaseDir $d

        # In bin, preserve user-added files (e.g. addin DLLs copied next to
        # Pinta.exe) that aren't part of the published build, so reinstalling
        # doesn't wipe them. Stash them, then restore after the bin is replaced.
        $stash = $null
        if ($d -eq 'bin' -and (Test-Path $target)) {
            $buildNames = (Get-ChildItem $source -File -ErrorAction SilentlyContinue).Name
            $extras = Get-ChildItem $target -File -ErrorAction SilentlyContinue |
                Where-Object { $buildNames -notcontains $_.Name }
            if ($extras) {
                $stash = Join-Path $env:TEMP ("pinta-extras-" + [guid]::NewGuid().ToString('N'))
                New-Item -ItemType Directory -Path $stash | Out-Null
                $extras | ForEach-Object { Copy-Item $_.FullName $stash -Force }
                Write-Host ("Preserving user files in bin: " + (($extras.Name) -join ', '))
            }
        }

        if (Test-Path $target) { Remove-Item $target -Recurse -Force }
        Copy-Item $source $target -Recurse -Force

        if ($stash) {
            Get-ChildItem $stash -File | ForEach-Object { Copy-Item $_.FullName (Join-Path $target $_.Name) -Force }
            Remove-Item $stash -Recurse -Force
        }
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

Write-Host "Done." -ForegroundColor Green

# Launch the freshly installed Pinta (non-elevated, so it runs as a normal
# user rather than as admin like the copy phase).
if (-not $NoLaunch) {
    $exe = Join-Path $InstallDir 'bin\Pinta.exe'
    if (Test-Path $exe) {
        Write-Host "Launching $exe ..."
        Start-Process $exe
    } else {
        Write-Warning "Installed Pinta.exe not found at $exe; launch it from its shortcut."
    }
}
