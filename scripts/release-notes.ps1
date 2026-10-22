<#
.SYNOPSIS
    Writes the release notes for one version from its section of CHANGELOG.md.
.DESCRIPTION
    Finds the heading "## [<version>] - <date>" (optionally followed by "(API x.y.z)") and copies everything up to the
    next version heading. The release workflow uses the result as the GitHub release body. Fails when the version has
    no section or the section is empty, so a tag cannot be released before its changelog is written.
.EXAMPLE
    scripts\release-notes.ps1 -Version 0.2.0-beta.1 -OutFile dist\release-notes.md
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$OutFile
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$lines = [System.IO.File]::ReadAllLines((Join-Path $repo 'CHANGELOG.md'))

$heading = '^## \[' + [regex]::Escape($Version) + '\](?<rest>.*)$'
$start = -1
for ($i = 0; $i -lt $lines.Count; $i++) {
    if ($lines[$i] -match $heading) { $start = $i; $rest = $Matches.rest; break }
}
if ($start -lt 0) { throw "CHANGELOG.md has no section for $Version. Move the [Unreleased] entries under ## [$Version] - <date>." }

$end = $lines.Count
for ($i = $start + 1; $i -lt $lines.Count; $i++) {
    if ($lines[$i] -match '^## \[') { $end = $i; break }
}
$body = @($lines[($start + 1)..($end - 1)])
$text = ($body -join "`n").Trim()
if (-not $text) { throw "The CHANGELOG.md section for $Version is empty." }

$notes = @()
if ($rest -match '\(API (?<api>[0-9][0-9.]*)\)') {
    $notes += "Host API version $($Matches.api)."
    $notes += ''
}
$notes += $text
$notes += ''
$notes += '---'
$notes += ''
$notes += 'Install the host with `HyperHarbor-Host-' + $Version + '.exe` and the client with the `-setup.exe` installer below; see the [install steps](https://github.com/faulkner-engineering/hyperharbor#install). Installed copies update themselves. HyperHarbor comes with no warranty; see [WARRANTY.md](https://github.com/faulkner-engineering/hyperharbor/blob/main/WARRANTY.md).'

$folder = Split-Path -Parent $OutFile
if ($folder) { New-Item -ItemType Directory -Force $folder | Out-Null }
[System.IO.File]::WriteAllText($OutFile, ($notes -join "`n") + "`n", (New-Object System.Text.UTF8Encoding $false))
Write-Host "Wrote the release notes for $Version to $OutFile"
