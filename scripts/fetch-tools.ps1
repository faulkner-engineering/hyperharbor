<#
.SYNOPSIS
    Downloads the pinned third-party tools (Kopia) into .tools and verifies their SHA-256 hashes.
.DESCRIPTION
    tools\tools.json pins each tool's download URL and the hashes of the archive and the executable. A tool is
    downloaded only when missing or when its hash differs. Prints the path of kopia.exe on the last line.
    The binaries are not committed; package.ps1 embeds the result in HyperHarbor.Host.exe.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$cache = Join-Path $repo '.tools'
$kopia = (Get-Content (Join-Path $repo 'tools\tools.json') -Raw | ConvertFrom-Json).kopia
$folder = Join-Path $cache "kopia-$($kopia.version)"
$exe = Join-Path $folder 'kopia.exe'

function Get-Hash([string]$Path) { (Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash.ToLowerInvariant() }

if (-not ((Test-Path $exe) -and (Get-Hash $exe) -eq $kopia.exeSha256)) {
    New-Item -ItemType Directory -Force $folder | Out-Null
    $zip = Join-Path $folder 'kopia.zip'
    Write-Host "Downloading Kopia $($kopia.version)" -ForegroundColor Cyan
    Invoke-WebRequest -Uri $kopia.url -OutFile $zip -UseBasicParsing
    if ((Get-Hash $zip) -ne $kopia.zipSha256) {
        Remove-Item $zip -Force
        throw "The Kopia download does not match the pinned SHA-256 in tools\tools.json."
    }
    $extract = Join-Path $folder 'extract'
    if (Test-Path $extract) { Remove-Item $extract -Recurse -Force }
    Expand-Archive -LiteralPath $zip -DestinationPath $extract
    Copy-Item (Get-ChildItem $extract -Recurse -Filter kopia.exe | Select-Object -First 1).FullName $exe -Force
    Remove-Item $extract -Recurse -Force
    if ((Get-Hash $exe) -ne $kopia.exeSha256) {
        Remove-Item $exe -Force
        throw "kopia.exe does not match the pinned SHA-256 in tools\tools.json."
    }
}

$exe
