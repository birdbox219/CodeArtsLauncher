<#
.SYNOPSIS
    Starts the local publishing panel: pick a game, choose the build folder, push.

.DESCRIPTION
    A browser UI over `butler push`, so releasing a build does not mean remembering a command line.
    It does exactly what tools\push-release.ps1 does -- same folder checks, same target rules -- and
    additionally shows every game's current channel and build, and streams butler's progress and its
    "Re-used ...% of old" line as the push runs.

    The panel is bound to 127.0.0.1 and is a tool for you, not part of what players get: it runs
    butler with your local itch.io key and browses your filesystem when you ask it to. Never expose
    it on a network interface and never ship it.

.PARAMETER Port
    Defaults to 5099.

.PARAMETER NoBrowser
    Do not open a browser; just print the URL.

.EXAMPLE
    .\publish-panel.ps1
#>
[CmdletBinding()]
param(
    [int]$Port = 5099,
    [switch]$NoBrowser
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src\Launcher.Publisher\Launcher.Publisher.csproj'

if (-not (Test-Path $project)) {
    throw "Publisher project not found at $project"
}

if (-not (Test-Path (Join-Path $PSScriptRoot 'butler\butler.exe'))) {
    throw "butler.exe not found at $(Join-Path $PSScriptRoot 'butler\butler.exe'). Nothing is installed system-wide here; butler is vendored under tools\butler\."
}

$env:PUBLISHER_PORT = $Port

$dotnetArgs = @('run', '--project', $project)
if ($NoBrowser) { $dotnetArgs += @('--', '--no-browser') }

Write-Host ''
Write-Host "Starting the publishing panel on http://localhost:$Port" -ForegroundColor Cyan
Write-Host 'Ctrl+C to stop.'
Write-Host ''

& dotnet @dotnetArgs
