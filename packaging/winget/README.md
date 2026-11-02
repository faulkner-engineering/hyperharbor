# winget packages

HyperHarbor is published to the Windows Package Manager community repository
([microsoft/winget-pkgs](https://github.com/microsoft/winget-pkgs)) as two packages, installed from the assets of each
published stable GitHub release:

| | Host | Client |
| --- | --- | --- |
| Identifier | `FaulknerEngineering.HyperHarbor.Host` | `FaulknerEngineering.HyperHarbor.Client` (moniker `hyperharbor`) |
| Installer | `HyperHarbor-Host-<version>.exe` | `HyperHarbor_<version>_x64-setup.exe` |
| Installer type | `exe` | `nullsoft` (Tauri's NSIS installer) |
| Scope | machine (`ElevationRequirement: elevatesSelf`) | user (no elevation) |
| Silent switches | `install --quiet` (also for silent with progress) | `/S` |
| Upgrade | the same command: `install` updates an installed host in place | the same installer |
| Uninstall key (ProductCode) | `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\HyperHarborHost` | `HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\HyperHarbor` |
| Uninstall | `QuietUninstallString`: `"<root>\current\HyperHarbor.Host.exe" uninstall --quiet` | `UninstallString` plus `/S`, which winget adds for nullsoft |

The bare identifier `FaulknerEngineering.HyperHarbor` is reserved and not used by either package.

`winget install FaulknerEngineering.HyperHarbor.Host` and `winget install FaulknerEngineering.HyperHarbor.Client`
work once the first submission of each package is merged into winget-pkgs.

## What the installers do

- Host: `install --quiet` shows no dialog and asks nothing, with or without a terminal. Without elevation it starts an
  elevated copy of itself (one UAC prompt), so winget run by a standard user shows one prompt for the host and none
  for the client. From an elevated console there is no prompt; the tray is then registered for the account that runs
  the console. It exits 0 on success, 1 on failure, and 2 when elevation was refused (the manifest maps 2 to
  `cancelledByUser`). With an older version installed it updates in place: the new version is copied to
  `versions\<version>`, `current` is pointed at it, and the previous version stays for rollback, as after a self-update.
- Host uninstall (`uninstall --quiet`) removes the service, the scheduled update task, the console account, the
  firewall rule, the Start menu shortcut, the install folder with its versions, and the uninstall entry. It keeps
  pairings, settings, and the audit trail in `%ProgramData%\HyperHarbor`, so `winget uninstall` keeps the data; delete
  that folder, or run `HyperHarbor.Host.exe uninstall --remove-data` instead, to remove it too. The installed
  executable waits for the removal and returns its exit code.
- The host's uninstall entry follows the host's own updates: the update helper sets `DisplayVersion` to the version
  `current` points at after every run, so `winget list` and `winget upgrade` see the version that is running, not the
  one winget installed.
- Client: Tauri's NSIS installer installs for the current user in `%LOCALAPPDATA%\HyperHarbor` and writes its uninstall
  entry under HKCU. It needs no elevation, and the client updates itself as before.
- Both are code signed with Azure Artifact Signing (signer `CN=Faulkner Engineering LLC`). The 1.12.0 manifest schema has
  no field for an executable's signer (`SignatureSha256` is for MSIX only), so the manifests do not record it.

## Making the manifests

```powershell
powershell -ExecutionPolicy Bypass -File packaging\winget\New-WingetManifests.ps1 -Version 0.2.5
```

This downloads both installers from the release, computes their SHA-256, and writes six files under
`packaging\winget\out` (git-ignored) in the winget-pkgs layout:

```
manifests\f\FaulknerEngineering\HyperHarbor\Host\<version>\FaulknerEngineering.HyperHarbor.Host.yaml
manifests\f\FaulknerEngineering\HyperHarbor\Host\<version>\FaulknerEngineering.HyperHarbor.Host.installer.yaml
manifests\f\FaulknerEngineering\HyperHarbor\Host\<version>\FaulknerEngineering.HyperHarbor.Host.locale.en-US.yaml
manifests\f\FaulknerEngineering\HyperHarbor\Client\<version>\...   (the same three for the client)
```

Options: `-OutputDirectory`; `-HostInstaller` and `-ClientInstaller` to hash local copies instead of downloading;
`-ReleaseDate yyyy-MM-dd` (otherwise the release's publication date from GitHub); `-InstallerBaseUrl` to point the
installer URLs at a test server (live tests only); `-AllowPrerelease` for tests (prereleases never go to winget).

The manifests use schema 1.12.0, the newest that winget-pkgs uses (PowerToys' manifests, 2026-10-27). `schemas\1.12.0`
holds the three JSON schemas from [microsoft/winget-cli](https://github.com/microsoft/winget-cli/tree/master/schemas/JSON/manifests/v1.12.0)
(commit efcb928, MIT license), which the end-to-end test validates against.

## Checks

- End to end (`scripts\e2e.ps1`, CI): `WingetManifestTests` runs the script with stand-in installers, validates the six
  files against the schemas, checks the scope, switches, ProductCode, URLs, and hashes, and runs `winget validate`
  where winget is installed. `ReleaseWorkflowTests` checks the release workflow's winget job, and
  `UninstallEntryTests` and `InstallFolderRemovalTests` the host's uninstall entry and removal.
- `winget validate --manifest <folder>` for each package folder.
- Live tests on a PC with Hyper-V: [Live tests](#live-tests).

## Live tests

These install the real packages, so run them on a test PC. Installing from a local manifest needs a setting that only
an administrator can turn on, once:

```powershell
winget settings --enable LocalManifestFiles     # elevated
```

Then, as the user who will use HyperHarbor (not elevated):

```powershell
$m = 'packaging\winget\out\manifests\f\FaulknerEngineering\HyperHarbor'
winget validate --manifest "$m\Host\0.2.5"
winget validate --manifest "$m\Client\0.2.5"
winget install --manifest "$m\Host\0.2.5" --accept-package-agreements --disable-interactivity
winget install --manifest "$m\Client\0.2.5" --accept-package-agreements --disable-interactivity
winget list --name HyperHarbor                  # both, with their versions
Get-Service HyperHarborHost                     # Running
winget uninstall --name "HyperHarbor Host" --disable-interactivity
winget uninstall --name HyperHarbor --exact --disable-interactivity
Get-Service HyperHarborHost                     # not found
schtasks /query /tn "HyperHarbor\Update"        # not found
```

Until a package is in winget-pkgs, `winget list` shows a package installed from a local manifest by its uninstall
entry (`ARP\Machine\X64\HyperHarborHost`, `ARP\User\X64\HyperHarbor`) rather than by its identifier, and `winget
upgrade` can only upgrade it with `--manifest`.

- Upgrade: install the previous release from its manifest, then `winget upgrade --manifest <newer folder>` (or
  `winget install --manifest`). Afterwards `%ProgramFiles%\HyperHarbor\versions` holds both versions and `current`
  points at the newer one, as after a self-update.
- Self-update: with a host installed by winget, let the host update itself (or choose Install now), then check that
  `winget list --name "HyperHarbor Host"` shows the new version.
- Windows Sandbox: in a clone of winget-pkgs, `.\Tools\SandboxTest.ps1 <manifest folder>` installs the package in a
  fresh Windows Sandbox, the way the winget-pkgs validation does. It needs the Windows Sandbox feature
  (Containers-DisposableClientVM) and, in a VM, nested virtualization.
