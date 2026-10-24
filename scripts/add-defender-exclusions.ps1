<#
.SYNOPSIS
    Excludes the repository and the build caches from Microsoft Defender's real-time scanning.

.DESCRIPTION
    Builds write and read thousands of small files (Rust objects, .NET outputs, node_modules), and Defender
    scans each one. This adds folder exclusions only, not process exclusions: the repository, the Rust
    toolchain and crate cache (.cargo, .rustup), the NuGet package cache, and the npm cache. It is a machine
    setting for development PCs, not part of any build; run it once, elevated:
        powershell -ExecutionPolicy Bypass -File scripts\add-defender-exclusions.ps1
    Undo with -Remove. Get-MpPreference | Select-Object -ExpandProperty ExclusionPath lists the exclusions.
#>
[CmdletBinding()]
param(
    [switch]$Remove
)

$ErrorActionPreference = 'Stop'

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this script from an elevated PowerShell: Defender exclusions are a machine setting.'
}

$repo = Split-Path -Parent $PSScriptRoot
$paths = @(
    $repo,
    (Join-Path $env:USERPROFILE '.cargo'),
    (Join-Path $env:USERPROFILE '.rustup'),
    (Join-Path $env:USERPROFILE '.nuget\packages'),
    (Join-Path $env:LOCALAPPDATA 'npm-cache')
) | Where-Object { Test-Path $_ }

if ($Remove) {
    Remove-MpPreference -ExclusionPath $paths
    Write-Host 'Removed Defender exclusions:' -ForegroundColor Green
}
else {
    Add-MpPreference -ExclusionPath $paths
    Write-Host 'Added Defender exclusions:' -ForegroundColor Green
}
$paths | ForEach-Object { Write-Host "  $_" }
