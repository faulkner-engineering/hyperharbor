<#
.SYNOPSIS
    Downloads a pinned third-party tool into .tools and verifies its SHA-256 hashes.
.DESCRIPTION
    tools\tools.json pins the tools HyperHarbor ships (Kopia), and tools\test-tools.json the ones only tests use
    (rclone). Each entry has the download URL and the hashes of the archive and the executable. A tool is downloaded
    only when missing or when its hash differs. Prints the path of the executable on the last line.
    The binaries are not committed; package.ps1 embeds Kopia in HyperHarbor.Host.exe.
.EXAMPLE
    scripts\fetch-tools.ps1                 # kopia.exe
    scripts\fetch-tools.ps1 -Tool rclone    # rclone.exe, for the end-to-end tests
#>
[CmdletBinding()]
param(
    [ValidateSet('kopia', 'rclone')][string]$Tool = 'kopia'
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$cache = Join-Path $repo '.tools'
$pins = if ($Tool -eq 'kopia') { 'tools.json' } else { 'test-tools.json' }
$pin = (Get-Content (Join-Path $repo "tools\$pins") -Raw | ConvertFrom-Json).$Tool
$folder = Join-Path $cache "$Tool-$($pin.version)"
$exe = Join-Path $folder "$Tool.exe"

function Get-Hash([string]$Path) { (Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash.ToLowerInvariant() }

if (-not ((Test-Path $exe) -and (Get-Hash $exe) -eq $pin.exeSha256)) {
    New-Item -ItemType Directory -Force $folder | Out-Null
    $zip = Join-Path $folder "$Tool.zip"
    Write-Host "Downloading $Tool $($pin.version)" -ForegroundColor Cyan
    Invoke-WebRequest -Uri $pin.url -OutFile $zip -UseBasicParsing
    if ((Get-Hash $zip) -ne $pin.zipSha256) {
        Remove-Item $zip -Force
        throw "The $Tool download does not match the pinned SHA-256 in tools\$pins."
    }
    $extract = Join-Path $folder 'extract'
    if (Test-Path $extract) { Remove-Item $extract -Recurse -Force }
    Expand-Archive -LiteralPath $zip -DestinationPath $extract
    Copy-Item (Get-ChildItem $extract -Recurse -Filter "$Tool.exe" | Select-Object -First 1).FullName $exe -Force
    Remove-Item $extract -Recurse -Force
    Remove-Item $zip -Force
    if ((Get-Hash $exe) -ne $pin.exeSha256) {
        Remove-Item $exe -Force
        throw "$Tool.exe does not match the pinned SHA-256 in tools\$pins."
    }
}

$exe
