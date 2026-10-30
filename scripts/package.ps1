<#
.SYNOPSIS
    Builds test packages into dist/: the host executable and the client installer.

.DESCRIPTION
    1. Runs the host and client test suites (skip with -SkipTests).
    2. Publishes the host (service, tray, and installer) as one self-contained executable,
       dist\HyperHarbor-Host-<version>.exe, and its update manifest dist\latest.json (a prerelease
       version is on the beta channel). dist\host holds the same executable with the portable
       start and stop scripts from packaging/host, for development runs.
    3. Builds the Tauri client and copies its NSIS installer and portable executable.

    -Fast is for test builds; omit it for releases. It builds the client without LTO, at
    optimization level 1, incrementally with parallel code generation (a change rebuilds in
    seconds instead of minutes), only as the portable executable (no installer), and publishes
    the host executables without single-file compression (about 20 seconds faster; they are
    larger). Switching between fast and full builds recompiles the client crates once.

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
# @() keeps one match an array; a lone string would be joined onto PATH without a separator.
$env:Path = (@($toolPaths | Where-Object { Test-Path $_ }) + $env:Path) -join ';'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'

# Code signing with Azure Artifact Signing (scripts\sign.ps1), for full builds where HH_SIGNING_* is set (the release
# workflow sets it, with HH_SIGNING_REQUIRED=1 so a missing setting fails the release).
$sign = Join-Path $PSScriptRoot 'sign.ps1'
$codeSigning = -not $Fast -and ([bool]$env:HH_SIGNING_ENDPOINT -or $env:HH_SIGNING_REQUIRED -eq '1')

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
                Invoke-Step 'Client tests' { cargo test --workspace }
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

    # Kopia is embedded in the single executable (hash pinned in tools\tools.json) and extracted beside the
    # version's executable by the installer.
    $kopiaExe = & (Join-Path $PSScriptRoot 'fetch-tools.ps1') | Select-Object -Last 1

    $publishArgs = @(
        "-p:KopiaExe=$kopiaExe",
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

    # Code signing (scripts\sign.ps1; it signs only when HH_SIGNING_* is set, as in the release workflow) comes before
    # latest.json records the executable's SHA-256, which hosts check before they update. Fast builds are not signed.
    if (-not $Fast) { Invoke-Step 'Sign host' { & $sign (Join-Path $staging 'HyperHarbor.Host.exe') } }

    # dist\host keeps the portable start and stop scripts for development runs; the executable alone is what ships.
    Copy-Item (Join-Path $repo 'packaging\host\*') $staging
    $hostExe = Join-Path $dist "HyperHarbor-Host-$version.exe"
    Copy-Item (Join-Path $staging 'HyperHarbor.Host.exe') $hostExe -Force
    Remove-Item (Join-Path $dist "HyperHarbor-Host-$version-portable.zip") -ErrorAction SilentlyContinue
    Write-Host "Host: $hostExe (double-click to install, or HyperHarbor.Host.exe --help)" -ForegroundColor Green

    # The update manifest for this executable, written (and checked) by the code that reads it.
    $manifest = Join-Path $dist 'latest.json'
    Invoke-Step 'Write update manifest' { & (Join-Path $staging 'HyperHarbor.Host.exe') write-update-manifest $manifest | Out-String | Write-Host }
}

if (-not $HostOnly) {
    if ($Fast) {
        # Overrides [profile.release] in client/src-tauri/Cargo.toml for this build only. Measured on an
        # 8-thread PC after a one-line change: thin LTO at opt 3 took 170 s, this takes under 10 s.
        # The release profile's single codegen unit must be overridden too: with one unit, incremental
        # compilation reuses nothing, and the client crate took 100 s on every build (12 s with 256).
        $env:CARGO_PROFILE_RELEASE_LTO = 'off'
        $env:CARGO_PROFILE_RELEASE_OPT_LEVEL = '1'
        $env:CARGO_PROFILE_RELEASE_INCREMENTAL = 'true'
        $env:CARGO_PROFILE_RELEASE_CODEGEN_UNITS = '256'
    }

    Push-Location (Join-Path $repo 'client')
    try {
        $mode = if ($Fast) { 'fast' } else { 'full release' }
        # The camera source DLL the Windows Frame Server loads. The installer ships it as a resource
        # (tauri.camera.conf.json adds it only here, so ordinary builds and tests do not need it); the
        # client's "Set up camera sharing" copies it under Program Files and registers it.
        Invoke-Step 'Build camera source DLL' {
            Push-Location 'src-tauri'
            try { cargo build -p hyperharbor-vcam --release } finally { Pop-Location }
        }
        $resources = Join-Path $repo 'client\src-tauri\resources'
        New-Item -ItemType Directory -Force $resources | Out-Null
        Copy-Item (Join-Path $repo 'client\src-tauri\target\release\hyperharbor_vcam.dll') $resources -Force
        # Updater artifacts (the installer's .sig) are made only when the signing key is available:
        # the release workflow passes it from the repository secrets; a test build needs none.
        # A fast build makes only the portable executable: test runs use it, and the installer takes longer.
        $signing = [bool]$env:TAURI_SIGNING_PRIVATE_KEY -and -not $Fast
        $configs = @('--config', 'src-tauri/tauri.camera.conf.json')
        if ($signing) { $configs += @('--config', 'src-tauri/tauri.updater.conf.json') }
        elseif (-not $Fast) { Write-Host 'TAURI_SIGNING_PRIVATE_KEY is not set: building without updater artifacts.' -ForegroundColor Yellow }
        # Code signing: while it bundles, Tauri runs sign.ps1 on every file it ships, in place (the client executable,
        # the camera DLL in resources, NSIS's plug-ins and uninstaller), then on the installer, and only then makes the
        # updater's .sig over the installer (signing afterwards would invalidate that signature).
        if ($codeSigning) {
            $signConfig = Join-Path ([IO.Path]::GetTempPath()) "hyperharbor-tauri-sign-$PID.json"
            $signCommand = @{ cmd = 'powershell'; args = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $sign, '%1') }
            @{ bundle = @{ windows = @{ signCommand = $signCommand } } } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $signConfig -Encoding ASCII
            $configs += @('--config', $signConfig)
        }
        # Not splatted: npm's PowerShell shim turns a splatted --no-bundle into "-" and "no-bundle".
        if ($Fast) { Invoke-Step "Build client ($mode)" { npm run tauri build -- --no-bundle @configs } }
        else { Invoke-Step "Build client ($mode)" { npm run tauri build -- --bundles nsis @configs } }
    }
    finally {
        Pop-Location
        Remove-Item Env:\CARGO_PROFILE_RELEASE_LTO, Env:\CARGO_PROFILE_RELEASE_OPT_LEVEL, Env:\CARGO_PROFILE_RELEASE_INCREMENTAL, Env:\CARGO_PROFILE_RELEASE_CODEGEN_UNITS -ErrorAction SilentlyContinue
        if ($signConfig) { Remove-Item -LiteralPath $signConfig -ErrorAction SilentlyContinue }
    }

    $release = Join-Path $repo 'client\src-tauri\target\release'
    Remove-Item (Join-Path $dist 'client-latest.json') -ErrorAction SilentlyContinue
    $installerName = "HyperHarbor_$($version)_x64-setup.exe"
    if ($Fast) {
        # An installer of this version left from an earlier build would not match the new portable executable.
        Remove-Item (Join-Path $dist $installerName), (Join-Path $dist "$installerName.sig") -ErrorAction SilentlyContinue
    }
    else {
        $installer = Get-Item (Join-Path $release "bundle\nsis\$installerName")
        Copy-Item $installer.FullName $dist -Force
    }
    $signature = "$($installer.FullName).sig"
    if ($signing -and (Test-Path $signature)) {
        Copy-Item $signature $dist -Force
        # The manifest the client's updater reads (Tauri format). The notes are this version's CHANGELOG
        # section when it has one, otherwise a pointer to the release page.
        $changelog = Get-Content (Join-Path $repo 'CHANGELOG.md') -Raw
        $section = [regex]::Match($changelog, "(?ms)^## \[$([regex]::Escape($version))\][^\r\n]*\r?\n(.*?)(?=^## \[|\z)")
        $notes = if ($section.Success) { $section.Groups[1].Value.Trim() } else { "See https://github.com/faulkner-engineering/hyperharbor/releases/tag/v$version" }
        $clientManifest = [ordered]@{
            version  = $version
            notes    = $notes
            pub_date = [DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ')
            platforms = [ordered]@{
                'windows-x86_64' = [ordered]@{
                    signature = (Get-Content $signature -Raw).Trim()
                    url       = "https://github.com/faulkner-engineering/hyperharbor/releases/download/v$version/$($installer.Name)"
                }
            }
        }
        # Windows PowerShell 5.1 has no utf8NoBOM encoding, and the updater rejects a byte order mark.
        [System.IO.File]::WriteAllText((Join-Path $dist 'client-latest.json'), ($clientManifest | ConvertTo-Json -Depth 5), (New-Object System.Text.UTF8Encoding($false)))
        Write-Host "Client update manifest: $(Join-Path $dist 'client-latest.json')" -ForegroundColor Green
    }
    $portable = Join-Path $dist "HyperHarbor-Client-$version-portable.exe"
    Copy-Item (Join-Path $release 'hyperharbor-client.exe') $portable -Force
    # Tauri signs the executable it bundles; the portable copy is signed here if it was not.
    if ($codeSigning -and (Get-AuthenticodeSignature -LiteralPath $portable).Status -ne 'Valid') {
        Invoke-Step 'Sign portable client' { & $sign $portable }
    }
    # The portable client looks for the camera source next to itself.
    Copy-Item (Join-Path $resources 'hyperharbor_vcam.dll') $dist -Force
    if (-not $Fast) { Write-Host "Client installer: $(Join-Path $dist $installer.Name)" -ForegroundColor Green }
    Write-Host "Client portable:  $portable" -ForegroundColor Green
}
