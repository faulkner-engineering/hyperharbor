<#
.SYNOPSIS
    Downloads a pinned third-party tool into .tools and verifies its SHA-256 hashes.
.DESCRIPTION
    tools\tools.json pins the tools HyperHarbor ships (Kopia), tools\test-tools.json the ones only tests use (rclone,
    and the previous release's host for compatibility tests), and tools\build-tools.json the ones only release builds
    use (the Artifact Signing plug-in for signtool). Each entry has the download URL and the hashes of the archive (when
    it is a zip or a NuGet package) and of the file the caller needs. A tool is downloaded only when missing or when its
    hash differs. Prints the path of that file on the last line.
    The binaries are not committed; package.ps1 embeds Kopia in HyperHarbor.Host.exe.
.EXAMPLE
    scripts\fetch-tools.ps1                            # kopia.exe
    scripts\fetch-tools.ps1 -Tool rclone               # rclone.exe, for the end-to-end tests
    scripts\fetch-tools.ps1 -Tool previousRelease      # the last stable release's HyperHarbor-Host-<version>.exe
    scripts\fetch-tools.ps1 -Tool artifactSigning      # Azure.CodeSigning.Dlib.dll with its dependencies, for sign.ps1
#>
[CmdletBinding()]
param(
    [ValidateSet('kopia', 'rclone', 'previousRelease', 'artifactSigning')][string]$Tool = 'kopia'
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$cache = Join-Path $repo '.tools'
$pins = switch ($Tool) {
    'kopia' { 'tools.json' }
    'artifactSigning' { 'build-tools.json' }
    default { 'test-tools.json' }
}
$pin = (Get-Content (Join-Path $repo "tools\$pins") -Raw | ConvertFrom-Json).$Tool
$folder = Join-Path $cache "$Tool-$($pin.version)"
# An archive holds <tool>.exe, or a folder whose files are all kept (pin.folder, pin.file); a plain executable keeps
# the name it is published under.
$fileName = if ($pin.file) { $pin.file } elseif ($pin.zipSha256) { "$Tool.exe" } else { Split-Path -Leaf ([Uri]$pin.url).AbsolutePath }
$file = Join-Path $folder $fileName

function Get-Hash([string]$Path) { (Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash.ToLowerInvariant() }

if (-not ((Test-Path $file) -and (Get-Hash $file) -eq $pin.exeSha256)) {
    New-Item -ItemType Directory -Force $folder | Out-Null
    Write-Host "Downloading $Tool $($pin.version)" -ForegroundColor Cyan
    if ($pin.zipSha256) {
        # Expand-Archive takes only the .zip extension, which a NuGet package (.nupkg) lacks.
        $zip = Join-Path $folder "$Tool.zip"
        Invoke-WebRequest -Uri $pin.url -OutFile $zip -UseBasicParsing
        if ((Get-Hash $zip) -ne $pin.zipSha256) {
            Remove-Item $zip -Force
            throw "The $Tool download does not match the pinned SHA-256 in tools\$pins."
        }
        $extract = Join-Path $folder 'extract'
        if (Test-Path $extract) { Remove-Item $extract -Recurse -Force }
        Expand-Archive -LiteralPath $zip -DestinationPath $extract
        if ($pin.folder) {
            Copy-Item (Join-Path $extract "$($pin.folder)\*") $folder -Recurse -Force
        }
        else {
            Copy-Item (Get-ChildItem $extract -Recurse -Filter "$Tool.exe" | Select-Object -First 1).FullName $file -Force
        }
        Remove-Item $extract -Recurse -Force
        Remove-Item $zip -Force
    }
    else {
        Invoke-WebRequest -Uri $pin.url -OutFile $file -UseBasicParsing
    }
    if ((Get-Hash $file) -ne $pin.exeSha256) {
        Remove-Item $file -Force
        throw "$fileName does not match the pinned SHA-256 in tools\$pins."
    }
}

$file
