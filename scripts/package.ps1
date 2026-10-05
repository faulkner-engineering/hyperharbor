<#
.SYNOPSIS
    Builds test packages into dist/: the host executable and the client installer.

.DESCRIPTION
    1. Runs the host and client test suites (skip with -SkipTests).
    2. Publishes the host (service, tray, and installer) as one self-contained executable,
       dist\HyperHarbor-Host-<version>.exe. dist\host holds the same executable with the portable
       start and stop scripts from packaging/host, for development runs.
    3. Builds the Tauri client and copies its NSIS installer and portable executable.

    -Fast is for test builds; omit it for releases. It builds the client without LTO, at
    optimization level 1, incrementally (a one-line change rebuilds in seconds instead of about
    three minutes), publishes the host executables without single-file compression (about 20
    seconds faster; they are larger), and zips with the fastest compression. Switching between
    fast and full builds recompiles the client crates once.

    Run from any directory in Windows PowerShell or PowerShell 7:
        powershell -ExecutionPolicy Bypass -File scripts\package.ps1 [-Fast] [-SkipTests]
#>
[CmdletBinding()]
param(
    [switch]$SkipTests,
    [switch]$HostOnly,
    [switch]$ClientOnly,
    [switch]$Fast
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$dist = Join-Path $repo 'dist'

# Toolchains are often missing from PATH in non-interactive shells.
$toolPaths = @(
    "$env:ProgramFiles\dotnet",
    "$env:ProgramFiles\nodejs",
    "$env:USERPROFILE\.cargo\bin"
)
$env:Path = (($toolPaths | Where-Object { Test-Path $_ }) + $env:Path) -join ';'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'

function Invoke-Step([string]$Title, [scriptblock]$Command) {
    Write-Host "==> $Title" -ForegroundColor Cyan
    & $Command
    if ($LASTEXITCODE -ne 0) {
        throw "$Title failed with exit code $LASTEXITCODE."
    }
}

# Directory.Build.props has several PropertyGroups; take the one Version that is set.
$version = ([xml](Get-Content (Join-Path $repo 'Directory.Build.props'))).Project.PropertyGroup.Version |
    Where-Object { $_ } | Select-Object -First 1
if (-not $version) {
    $version = (Get-Content (Join-Path $repo 'client\src-tauri\tauri.conf.json') -Raw | ConvertFrom-Json).version
}

New-Item -ItemType Directory -Force $dist | Out-Null

# Files in dist/ cannot be replaced while running. Check now rather than after a long build. Only the
# side being rebuilt matters: the host lives in dist\host, the client executables directly in dist.
$hostDist = Join-Path $dist 'host'
$running = Get-Process | Where-Object { $_.Path -and $_.Path.StartsWith($dist, [StringComparison]::OrdinalIgnoreCase) } |
    Where-Object {
        $isHost = $_.Path.StartsWith($hostDist, [StringComparison]::OrdinalIgnoreCase)
        ($isHost -and -not $ClientOnly) -or (-not $isHost -and -not $HostOnly)
    }
if ($running) {
    $names = ($running | ForEach-Object { Split-Path -Leaf $_.Path } | Sort-Object -Unique) -join ', '
    throw "Close these programs running from dist\ first: $names"
}

if (-not $SkipTests) {
    if (-not $ClientOnly) {
        Invoke-Step 'Host tests' { dotnet test (Join-Path $repo 'HyperHarbor.sln') -c Release }
    }
    if (-not $HostOnly) {
        Push-Location (Join-Path $repo 'client')
        try {
            Invoke-Step 'Generated API types match api.yaml' {
                npm run gen:api
                if ($LASTEXITCODE -eq 0) { git diff --exit-code -- src/lib/api/types.ts }
            }
            Invoke-Step 'Frontend type check' { npm run check }
            Invoke-Step 'Frontend tests' { npm test }
            Push-Location 'src-tauri'
            try {
                Invoke-Step 'Client tests' { cargo test }
            }
            finally {
                Pop-Location
            }
        }
        finally {
            Pop-Location
        }
    }
}

if (-not $ClientOnly) {
    $staging = Join-Path $dist 'host'
    # Empty the folder rather than delete it: a terminal opened in dist\host (to run Start-HyperHarbor.ps1)
    # keeps the folder itself from being removed.
    if (Test-Path $staging) {
        Get-ChildItem -Force $staging | Remove-Item -Recurse -Force
    }

    $publishArgs = @(
        '-c', 'Release',
        '-r', 'win-x64',
        '--self-contained', 'true',
        '-p:PublishSingleFile=true',
        '-p:IncludeNativeLibrariesForSelfExtract=true',
        "-p:EnableCompressionInSingleFile=$(if ($Fast) { 'false' } else { 'true' })",
        '-p:DebugType=None',
        '-o', $staging
    )
    # One executable: the service, the tray, the installer, and the helpers (the tray is a library inside it).
    Invoke-Step 'Publish host' { dotnet publish (Join-Path $repo 'host\src\Host.Service\Host.Service.csproj') @publishArgs }

    # The settings are embedded in the executable; web.config is an IIS file the Web SDK adds.
    Remove-Item (Join-Path $staging 'web.config') -ErrorAction SilentlyContinue
    $extra = Get-ChildItem $staging | Where-Object Name -ne 'HyperHarbor.Host.exe'
    if ($extra) {
        throw "The host publish produced more than one file: $($extra.Name -join ', ')"
    }

    # dist\host keeps the portable start and stop scripts for development runs; the executable alone is what ships.
    Copy-Item (Join-Path $repo 'packaging\host\*') $staging
    $hostExe = Join-Path $dist "HyperHarbor-Host-$version.exe"
    Copy-Item (Join-Path $staging 'HyperHarbor.Host.exe') $hostExe -Force
    Remove-Item (Join-Path $dist "HyperHarbor-Host-$version-portable.zip") -ErrorAction SilentlyContinue
    Write-Host "Host: $hostExe (double-click to install, or HyperHarbor.Host.exe --help)" -ForegroundColor Green
}

if (-not $HostOnly) {
    if ($Fast) {
        # Overrides [profile.release] in client/src-tauri/Cargo.toml for this build only. Measured on an
        # 8-thread PC after a one-line change: thin LTO at opt 3 took 170 s, this takes under 10 s.
        $env:CARGO_PROFILE_RELEASE_LTO = 'off'
        $env:CARGO_PROFILE_RELEASE_OPT_LEVEL = '1'
        $env:CARGO_PROFILE_RELEASE_INCREMENTAL = 'true'
    }

    Push-Location (Join-Path $repo 'client')
    try {
        $mode = if ($Fast) { 'fast' } else { 'full release' }
        Invoke-Step "Build client ($mode)" { npm run tauri build -- --bundles nsis }
    }
    finally {
        Pop-Location
        Remove-Item Env:\CARGO_PROFILE_RELEASE_LTO, Env:\CARGO_PROFILE_RELEASE_OPT_LEVEL, Env:\CARGO_PROFILE_RELEASE_INCREMENTAL, Env:\CARGO_PROFILE_RELEASE_CODEGEN_UNITS -ErrorAction SilentlyContinue
    }

    $release = Join-Path $repo 'client\src-tauri\target\release'
    $installer = Get-ChildItem (Join-Path $release 'bundle\nsis\*.exe') | Sort-Object LastWriteTime | Select-Object -Last 1
    Copy-Item $installer.FullName $dist -Force
    Copy-Item (Join-Path $release 'hyperharbor-client.exe') (Join-Path $dist "HyperHarbor-Client-$version-portable.exe") -Force
    Write-Host "Client installer: $(Join-Path $dist $installer.Name)" -ForegroundColor Green
    Write-Host "Client portable:  $(Join-Path $dist "HyperHarbor-Client-$version-portable.exe")" -ForegroundColor Green
}
