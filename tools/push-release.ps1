<#
.SYNOPSIS
    Publishes a game build to an itch.io wharf channel, which is what makes delta updates possible.

.DESCRIPTION
    The launcher can only patch a game that has a wharf channel with at least one build. Right now
    none of the six games on itch.io/profile/birdbox774 has one, which is why nothing was ever
    updatable: no channel means no builds, no builds means no patch chain, and butler falls back to
    a full download.

    This script wraps `butler push` and then reports the channel state so you can see the build
    land. Push the same channel again after changing the game and butler uploads only the changed
    blocks -- that second push is what creates the first real patch.

.PARAMETER Folder
    The unpacked build folder. Push the folder, never a zip: butler diffs file contents, and a zip
    is one opaque blob that changes entirely on every rebuild.

.PARAMETER Target
    Either a full target ("birdbox774/logic-rift:windows") or just the game slug, in which case
    -Owner and -Channel fill in the rest.

.PARAMETER Version
    Optional user-facing version, e.g. "1.2.0". Shown in the launcher when set; otherwise the
    launcher falls back to the numeric build id.

.EXAMPLE
    .\push-release.ps1 -Folder ..\builds\LogicRift-Win64 -Target logic-rift -Version 1.0.0

.EXAMPLE
    .\push-release.ps1 -Folder D:\builds\pong -Target falcon-eye/what-can-possibly-go-pong:windows
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Folder,

    [Parameter(Mandatory = $true)]
    [string]$Target,

    [string]$Owner = 'birdbox774',
    [string]$Channel = 'windows',
    [string]$Version,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'

$butler = Join-Path $PSScriptRoot 'butler\butler.exe'
if (-not (Test-Path $butler)) {
    throw "butler.exe not found at $butler"
}

if (-not (Test-Path $Folder)) {
    throw "Build folder not found: $Folder"
}

$Folder = (Resolve-Path $Folder).Path

if (Test-Path $Folder -PathType Leaf) {
    throw "Push an unpacked folder, not a file. butler diffs file contents; a zip changes wholesale every rebuild, so patches would be as large as the game."
}

# Accept a full target, "owner/slug", or a bare slug.
if ($Target -match ':') {
    $fullTarget = $Target
} elseif ($Target -match '/') {
    $fullTarget = "${Target}:${Channel}"
} else {
    $fullTarget = "${Owner}/${Target}:${Channel}"
}

$files = Get-ChildItem -Path $Folder -Recurse -File
if ($files.Count -eq 0) {
    throw "Build folder is empty: $Folder"
}

$totalBytes = ($files | Measure-Object -Property Length -Sum).Sum
$exes = $files | Where-Object { $_.Extension -eq '.exe' } |
    Where-Object { $_.BaseName -notmatch 'UnityCrashHandler|crashpad|vcredist|unins' }

Write-Host ''
Write-Host "  target   $fullTarget"
Write-Host "  folder   $Folder"
Write-Host ("  contents {0} files, {1:N1} MB" -f $files.Count, ($totalBytes / 1MB))
if ($Version) { Write-Host "  version  $Version" }

if ($exes.Count -eq 0) {
    Write-Warning "No .exe found in the folder. The launcher auto-detects the executable after install and will have nothing to run."
} else {
    Write-Host "  exe      $(($exes | Sort-Object Length -Descending | Select-Object -First 1).Name)"
}
Write-Host ''

if ($DryRun) {
    Write-Host 'Dry run: nothing pushed.' -ForegroundColor Yellow
    return
}

$pushArgs = @('push', $Folder, $fullTarget)
if ($Version) { $pushArgs += @('--userversion', $Version) }

& $butler @pushArgs
if ($LASTEXITCODE -ne 0) {
    throw "butler push failed with exit code $LASTEXITCODE. If this is a collaboration hosted under another account, you need upload rights on that game."
}

Write-Host ''
Write-Host 'Channel state after push:' -ForegroundColor Green

# Strip the channel to ask about the game as a whole, so every channel shows up.
$statusTarget = $fullTarget.Split(':')[0]
& $butler status $statusTarget

Write-Host ''
Write-Host 'Next: the launcher will show this build as installable. Push this same channel again' -ForegroundColor Cyan
Write-Host 'after a code change to create the first real patch, then the launcher offers an' -ForegroundColor Cyan
Write-Host 'Update that downloads only the changed blocks instead of the whole game.' -ForegroundColor Cyan
