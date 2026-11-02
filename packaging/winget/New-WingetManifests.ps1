<#
.SYNOPSIS
    Writes the winget manifests for one HyperHarbor release: FaulknerEngineering.HyperHarbor.Host and
    FaulknerEngineering.HyperHarbor.Client, three files each, in the microsoft/winget-pkgs layout.

.DESCRIPTION
    Downloads the two installers from the published GitHub release (or reads local copies given with -HostInstaller
    and -ClientInstaller), computes their SHA-256, and writes:

        <OutputDirectory>\manifests\f\FaulknerEngineering\HyperHarbor\Host\<version>\
            FaulknerEngineering.HyperHarbor.Host.yaml                  (version)
            FaulknerEngineering.HyperHarbor.Host.installer.yaml        (installer)
            FaulknerEngineering.HyperHarbor.Host.locale.en-US.yaml     (defaultLocale)
        <OutputDirectory>\manifests\f\FaulknerEngineering\HyperHarbor\Client\<version>\  (the same three for the client)

    The installer URLs always point at the GitHub release, so manifests made from local files are for validation and
    tests only unless those files are the published ones. Prereleases never go to winget; -AllowPrerelease exists for
    tests. packaging\winget\README.md explains the packages and how to test them.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File packaging\winget\New-WingetManifests.ps1 -Version 0.2.5
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Version,
    # Default: packaging\winget\out (git-ignored).
    [string]$OutputDirectory,
    [string]$Repository = 'faulkner-engineering/hyperharbor',
    # Local copies of the release's installers; without them the installers are downloaded from the release.
    [string]$HostInstaller,
    [string]$ClientInstaller,
    # yyyy-MM-dd; by default the release's publication date from GitHub, or none when the installers are local files.
    [string]$ReleaseDate,
    # Live tests only: where the installers are served instead of the release (an http server on loopback).
    [string]$InstallerBaseUrl,
    [switch]$AllowPrerelease
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
# Windows PowerShell does not set $PSScriptRoot while it binds parameter defaults.
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $PSScriptRoot 'out' }

if ($Version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.]+)?$') { throw "Not a release version: $Version" }
if ($Version.Contains('-') -and -not $AllowPrerelease) { throw "$Version is a prerelease. Prereleases never go to winget." }
if ($ReleaseDate -and $ReleaseDate -notmatch '^\d{4}-\d{2}-\d{2}$') { throw "-ReleaseDate must be yyyy-MM-dd: $ReleaseDate" }

$manifestVersion = '1.12.0'
$tag = "v$Version"
$releaseUrl = "https://github.com/$Repository/releases"
$repositoryUrl = "https://github.com/$Repository"
$publisher = 'Faulkner Engineering LLC'

# The ProductCode is the name of the Uninstall key each installer writes, which winget uses to find the installed
# package: the host's UninstallEntry (HKLM, the service name) and Tauri's NSIS installer (HKCU, the product name).
$packages = @(
    [ordered]@{
        Name = 'Host'
        Identifier = 'FaulknerEngineering.HyperHarbor.Host'
        PackageName = 'HyperHarbor Host'
        Asset = "HyperHarbor-Host-$Version.exe"
        LocalFile = $HostInstaller
        ProductCode = 'HyperHarborHost'
        DisplayName = 'HyperHarbor Host'
        ShortDescription = 'Manages the Hyper-V virtual machines on this PC for paired HyperHarbor clients.'
        Description = @(
            'The HyperHarbor host runs as a Windows service on the PC that runs Hyper-V. Paired devices running the'
            'HyperHarbor client list, start, create, and back up its virtual machines and connect to them with Remote'
            'Desktop in one click, with a one-time password for each connection. It needs Windows 10 or 11 Pro,'
            'Enterprise, or Education with Hyper-V turned on.'
        ) -join ' '
        InstallationNotes = @(
            'HyperHarbor Host runs as the HyperHarborHost service and updates itself. Open HyperHarbor Host from the'
            'Start menu to set an admin passphrase and to pair devices. Uninstalling keeps pairings and settings in'
            '%ProgramData%\HyperHarbor; delete that folder to remove them too.'
        ) -join ' '
        Tags = @('hyper-v', 'virtual-machine', 'vm', 'remote-desktop', 'rdp', 'homelab', 'wake-on-lan', 'windows-service')
        Moniker = $null
    }
    [ordered]@{
        Name = 'Client'
        Identifier = 'FaulknerEngineering.HyperHarbor.Client'
        PackageName = 'HyperHarbor Client'
        Asset = "HyperHarbor_$($Version)_x64-setup.exe"
        LocalFile = $ClientInstaller
        ProductCode = 'HyperHarbor'
        DisplayName = 'HyperHarbor'
        ShortDescription = 'Finds HyperHarbor hosts, pairs with them, and connects to their Hyper-V virtual machines in one click.'
        Description = @(
            'The HyperHarbor client finds HyperHarbor hosts on the network, pairs with them using a PIN, and manages'
            'their Hyper-V virtual machines: start and stop, create from an ISO, change settings, back up, and connect'
            'with Remote Desktop in one click. The host is a separate package, FaulknerEngineering.HyperHarbor.Host.'
        ) -join ' '
        InstallationNotes = $null
        Tags = @('hyper-v', 'virtual-machine', 'vm', 'remote-desktop', 'rdp', 'homelab', 'wake-on-lan')
        Moniker = 'hyperharbor'
    }
)

# A YAML scalar, single-quoted unless it is plainly safe (letters, digits, and punctuation that YAML does not read).
function Format-Scalar([string]$Value) {
    if ($Value -match '^[A-Za-z0-9][A-Za-z0-9 .,/()_+-]*$' -and $Value -notmatch '\s$') { return $Value }
    if ($Value -match '^https?://[^\s#]+$') { return $Value }
    return "'" + $Value.Replace("'", "''") + "'"
}

function Write-Manifest([string]$Path, [string]$Type, [string[]]$Lines) {
    $text = @(
        "# yaml-language-server: `$schema=https://aka.ms/winget-manifest.$Type.$manifestVersion.schema.json"
        ''
    ) + $Lines + @(
        "ManifestType: $Type"
        "ManifestVersion: $manifestVersion"
    )
    # UTF-8 without a byte order mark and LF line endings, as winget-pkgs has them.
    [IO.File]::WriteAllText($Path, (($text -join "`n") + "`n"), (New-Object Text.UTF8Encoding $false))
}

if (-not $ReleaseDate -and -not ($HostInstaller -and $ClientInstaller)) {
    $release = Invoke-RestMethod -Uri "https://api.github.com/repos/$Repository/releases/tags/$tag" -Headers @{ 'User-Agent' = 'HyperHarbor-winget' }
    if ($release.draft) { throw "$tag is still a draft; publish it first." }
    if ($release.prerelease -and -not $AllowPrerelease) { throw "$tag is a prerelease. Prereleases never go to winget." }
    $ReleaseDate = ([datetime]$release.published_at).ToUniversalTime().ToString('yyyy-MM-dd')
}

$download = Join-Path ([IO.Path]::GetTempPath()) "hyperharbor-winget-$([guid]::NewGuid().ToString('N'))"
try {
    foreach ($package in $packages) {
        $installerUrl = if ($InstallerBaseUrl) { "$($InstallerBaseUrl.TrimEnd('/'))/$($package.Asset)" } else { "$releaseUrl/download/$tag/$($package.Asset)" }
        $file = $package.LocalFile
        if (-not $file) {
            New-Item -ItemType Directory -Force $download | Out-Null
            $file = Join-Path $download $package.Asset
            Write-Host "Downloading $installerUrl"
            Invoke-WebRequest -Uri $installerUrl -OutFile $file -UseBasicParsing
        }
        elseif (-not (Test-Path -LiteralPath $file)) {
            throw "Not found: $file"
        }

        # .NET rather than Get-FileHash: Windows PowerShell started from PowerShell 7 inherits its module path and
        # cannot load Microsoft.PowerShell.Utility, where Get-FileHash lives (CI's end-to-end job).
        $stream = [IO.File]::OpenRead((Resolve-Path -LiteralPath $file).Path)
        try { $sha256 = [BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash($stream)).Replace('-', '') }
        finally { $stream.Dispose() }
        $folder = Join-Path $OutputDirectory "manifests\f\FaulknerEngineering\HyperHarbor\$($package.Name)\$Version"
        New-Item -ItemType Directory -Force $folder | Out-Null
        $id = $package.Identifier

        Write-Manifest (Join-Path $folder "$id.yaml") 'version' @(
            "PackageIdentifier: $id"
            "PackageVersion: $Version"
            'DefaultLocale: en-US'
        )

        $installer = @(
            "PackageIdentifier: $id"
            "PackageVersion: $Version"
            'Platform:'
            '- Windows.Desktop'
        )
        if ($package.Name -eq 'Host') {
            # install --quiet installs, or updates in place when an older version is installed. The executable asks
            # for elevation itself (one UAC prompt when winget is not elevated). Exit code 2: elevation was refused.
            $installer += @(
                'InstallerType: exe'
                'Scope: machine'
                'InstallModes:'
                '- silent'
                '- silentWithProgress'
                'InstallerSwitches:'
                '  Silent: install --quiet'
                '  SilentWithProgress: install --quiet'
                'ExpectedReturnCodes:'
                '- InstallerReturnCode: 2'
                '  ReturnResponse: cancelledByUser'
                'UpgradeBehavior: install'
                "ProductCode: $($package.ProductCode)"
                'AppsAndFeaturesEntries:'
                "- DisplayName: $(Format-Scalar $package.DisplayName)"
                "  Publisher: $(Format-Scalar $publisher)"
                "  ProductCode: $($package.ProductCode)"
                'ElevationRequirement: elevatesSelf'
                'InstallationMetadata:'
                "  DefaultInstallLocation: $(Format-Scalar '%ProgramFiles%\HyperHarbor')"
            )
        }
        else {
            # Tauri's NSIS installer installs for the current user under %LOCALAPPDATA%\HyperHarbor, with no UAC prompt.
            $installer += @(
                'InstallerType: nullsoft'
                'Scope: user'
                'InstallModes:'
                '- interactive'
                '- silent'
                '- silentWithProgress'
                'InstallerSwitches:'
                '  Silent: /S'
                '  SilentWithProgress: /S'
                'UpgradeBehavior: install'
                "ProductCode: $($package.ProductCode)"
                'AppsAndFeaturesEntries:'
                "- DisplayName: $(Format-Scalar $package.DisplayName)"
                "  Publisher: $(Format-Scalar $publisher)"
                "  ProductCode: $($package.ProductCode)"
                'InstallationMetadata:'
                "  DefaultInstallLocation: $(Format-Scalar '%LocalAppData%\HyperHarbor')"
            )
        }
        if ($ReleaseDate) { $installer += "ReleaseDate: $ReleaseDate" }
        $installer += @(
            'Installers:'
            '- Architecture: x64'
            "  InstallerUrl: $(Format-Scalar $installerUrl)"
            "  InstallerSha256: $sha256"
        )
        Write-Manifest (Join-Path $folder "$id.installer.yaml") 'installer' $installer

        $locale = @(
            "PackageIdentifier: $id"
            "PackageVersion: $Version"
            'PackageLocale: en-US'
            "Publisher: $(Format-Scalar $publisher)"
            'PublisherUrl: https://faulkner.engineering'
            "PublisherSupportUrl: $repositoryUrl/issues"
            "Author: $(Format-Scalar $publisher)"
            "PackageName: $(Format-Scalar $package.PackageName)"
            'PackageUrl: https://faulkner.engineering/hyperharbor'
            'License: Apache-2.0'
            "LicenseUrl: $repositoryUrl/blob/main/LICENSE"
            "Copyright: $(Format-Scalar "Copyright (c) 2026 $publisher")"
            "ShortDescription: $(Format-Scalar $package.ShortDescription)"
            "Description: $(Format-Scalar $package.Description)"
        )
        if ($package.Moniker) { $locale += "Moniker: $($package.Moniker)" }
        $locale += 'Tags:'
        $locale += @($package.Tags | ForEach-Object { "- $_" })
        $locale += "ReleaseNotesUrl: $releaseUrl/tag/$tag"
        if ($package.InstallationNotes) { $locale += "InstallationNotes: $(Format-Scalar $package.InstallationNotes)" }
        Write-Manifest (Join-Path $folder "$id.locale.en-US.yaml") 'defaultLocale' $locale

        Write-Host "$id $Version -> $folder"
    }
}
finally {
    if (Test-Path $download) { Remove-Item -Recurse -Force $download }
}
