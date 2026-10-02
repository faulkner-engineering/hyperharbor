<#
.SYNOPSIS
    Builds test packages into dist/: a portable host zip and the client installer.

.DESCRIPTION
    1. Runs the host and client test suites (skip with -SkipTests).
    2. Publishes Host.Service and Host.Tray as self-contained single-file executables and zips
       them with the start and stop scripts from packaging/host.
    3. Builds the Tauri client and copies its NSIS installer and portable executable.

    -Fast builds the client with thin LTO and parallel code generation instead of the full
    release profile (fat LTO, one codegen unit). The link step drops from minutes to seconds and
    the binary is slightly larger. Use it for test builds; omit it for releases. Switching
    between fast and full builds recompiles the client crates once.

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

$version = ([xml](Get-Content (Join-Path $repo 'Directory.Build.props'))).Project.PropertyGroup.Version
if (-not $version) {
    $version = (Get-Content (Join-Path $repo 'client\src-tauri\tauri.conf.json') -Raw | ConvertFrom-Json).version
}

New-Item -ItemType Directory -Force $dist | Out-Null

if (-not $SkipTests) {
    if (-not $ClientOnly) {
        Invoke-Step 'Host tests' { dotnet test (Join-Path $repo 'HyperHarbor.sln') -c Release }
    }
    if (-not $HostOnly) {
        Push-Location (Join-Path $repo 'client')
        try {
            Invoke-Step 'Frontend type check' { npm run check }
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
    if (Test-Path $staging) {
        Remove-Item -Recurse -Force $staging
    }

    $publishArgs = @(
        '-c', 'Release',
        '-r', 'win-x64',
        '--self-contained', 'true',
        '-p:PublishSingleFile=true',
        '-p:IncludeNativeLibrariesForSelfExtract=true',
        '-p:EnableCompressionInSingleFile=true',
        '-p:DebugType=None',
        '-o', $staging
    )
    Invoke-Step 'Publish host service' { dotnet publish (Join-Path $repo 'host\src\Host.Service\Host.Service.csproj') @publishArgs }
    Invoke-Step 'Publish tray app' { dotnet publish (Join-Path $repo 'host\src\Host.Tray\Host.Tray.csproj') @publishArgs }

    # Development settings must not ship, and web.config is an IIS file the Web SDK adds.
    Remove-Item (Join-Path $staging 'appsettings.Development.json'), (Join-Path $staging 'web.config') -ErrorAction SilentlyContinue
    Copy-Item (Join-Path $repo 'packaging\host\*') $staging

    $zip = Join-Path $dist "HyperHarbor-Host-$version-portable.zip"
    Remove-Item $zip -ErrorAction SilentlyContinue
    Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $zip
    Write-Host "Host package: $zip" -ForegroundColor Green
}

if (-not $HostOnly) {
    if ($Fast) {
        # Overrides [profile.release] in client/src-tauri/Cargo.toml for this build only.
        $env:CARGO_PROFILE_RELEASE_LTO = 'thin'
        $env:CARGO_PROFILE_RELEASE_CODEGEN_UNITS = '16'
    }

    Push-Location (Join-Path $repo 'client')
    try {
        $mode = if ($Fast) { 'fast' } else { 'full release' }
        Invoke-Step "Build client ($mode)" { npm run tauri build -- --bundles nsis }
    }
    finally {
        Pop-Location
        Remove-Item Env:\CARGO_PROFILE_RELEASE_LTO, Env:\CARGO_PROFILE_RELEASE_CODEGEN_UNITS -ErrorAction SilentlyContinue
    }

    $release = Join-Path $repo 'client\src-tauri\target\release'
    $installer = Get-ChildItem (Join-Path $release 'bundle\nsis\*.exe') | Sort-Object LastWriteTime | Select-Object -Last 1
    Copy-Item $installer.FullName $dist -Force
    Copy-Item (Join-Path $release 'hyperharbor-client.exe') (Join-Path $dist "HyperHarbor-Client-$version-portable.exe") -Force
    Write-Host "Client installer: $(Join-Path $dist $installer.Name)" -ForegroundColor Green
    Write-Host "Client portable:  $(Join-Path $dist "HyperHarbor-Client-$version-portable.exe")" -ForegroundColor Green
}
