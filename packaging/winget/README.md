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

These install the real packages, so run them on a test PC with the HyperHarborHost service not installed.

### Installs, upgrades, self-update, and uninstalls (Test-WingetLive.ps1)

Installing from a local manifest needs an administrator setting. Under over-the-shoulder elevation (a standard user
approving with an administrator's credentials) `winget settings --enable LocalManifestFiles` turns it on for the
administrator's account only, so set the machine policy instead, once, elevated:

```powershell
reg add HKLM\SOFTWARE\Policies\Microsoft\Windows\AppInstaller /v EnableLocalManifestFiles /t REG_DWORD /d 1 /f
```

Then make manifests for every version the test names (New-WingetManifests.ps1; `-InstallerBaseUrl` and
`-AllowPrerelease` for test builds served by any http server on loopback) and run, as the user who will use
HyperHarbor, not elevated:

```powershell
powershell -ExecutionPolicy Bypass -File packaging\winget\Test-WingetLive.ps1 -ManifestRoot <folder> `
    -NewVersion 0.2.6 -PreviousVersion 0.2.5 -ClientVersion 0.2.5 -PreviousClientVersion 0.2.4 `
    -SelfUpdateVersion 0.2.5-winget.1 -SelfUpdateTarget 0.2.5 [-Steps 1,2,3,4]
```

Step 1 installs both packages; step 2 uninstalls them; step 3 installs the previous releases and upgrades them; step 4
installs a host older than the newest stable release, waits for it to update itself (about 15 minutes), and installs
the new version over it. After each step it checks the exit codes, the service, the uninstall entries, the versions
folder and current junction, the scheduled tasks and their folder, the data folder, `winget list`, and that the client
starts. Each host install, upgrade, and uninstall asks for one UAC prompt; the client asks for none. The same by hand:

```powershell
winget install --manifest <Host folder> --accept-package-agreements --disable-interactivity
winget install --manifest <Client folder> --accept-package-agreements --disable-interactivity
winget list --name HyperHarbor
winget upgrade --manifest <newer Host folder> --accept-package-agreements --disable-interactivity
winget uninstall --name "HyperHarbor Host" --exact --disable-interactivity
winget uninstall --name HyperHarbor --exact --disable-interactivity
```

Until a package is in winget-pkgs, `winget list` shows a package installed from a local manifest by its uninstall
entry (`ARP\Machine\X64\HyperHarborHost`, `ARP\User\X64\HyperHarbor`) rather than by its identifier, and `winget
upgrade` upgrades it only with `--manifest`.

### Windows Sandbox (Test-WingetSandbox.ps1)

The winget-pkgs validation installs each package in a clean VM; Windows Sandbox does the same on this PC. Once,
elevated, then restart (in a VM, the VM needs nested virtualization):

```powershell
Enable-WindowsOptionalFeature -Online -FeatureName Containers-DisposableClientVM -All
```

`Tools\SandboxTest.ps1` from winget-pkgs needs PowerShell 7 (`winget install Microsoft.PowerShell`), and a mapped
folder inside the winget-pkgs clone (otherwise it asks before mapping and fails without a console). Lay out a folder
there with Test-WingetSandbox.ps1, `manifests\Host\<version>` and `manifests\Client\<version>` (one each; nested, so
SandboxTest does not install them itself), and `serve\` with a test build its manifest points at on
http://127.0.0.1:8765/ (the script serves it inside the sandbox), then:

```powershell
git clone --depth 1 --filter=blob:none --sparse https://github.com/microsoft/winget-pkgs; cd winget-pkgs
git sparse-checkout set Tools
pwsh -File Tools\SandboxTest.ps1 -MapFolder .\hh-sandbox `
    -Script { & 'C:\Users\WDAGUtilityAccount\Desktop\hh-sandbox\Test-WingetSandbox.ps1' }
```

It writes `result.txt` into the folder: each winget command's exit code and output, the service, the uninstall
entries, `winget list`, the windows open after each install (a dialog would show here), and what is left after each
uninstall. SandboxTest.ps1 waits until the Sandbox window is closed. For a published release, a manifest folder
directly under the mapped folder is the standard run: `pwsh -File Tools\SandboxTest.ps1 <manifest folder>`.

### Results (2026-11-02, the development VM, Windows 11 build 26300, winget 1.29.380)

0.2.6 test builds of this phase's host, with the published 0.2.4 and 0.2.5:

- Fresh install (standard user): host 0.2.6 in 44 s with one UAC prompt, client 0.2.5 in 7 s with none; `winget list`
  showed both, the service ran, and the client started. Uninstalling both removed the service, the update task, the
  entries, and the install folder, and kept `%ProgramData%\HyperHarbor`.
- Upgrade: published 0.2.5 installed with `winget install`, then `winget upgrade` to 0.2.6 in 49 s: `versions` held
  0.2.5 and 0.2.6 with `current` on 0.2.6, as after a self-update. Client 0.2.4 to 0.2.5 likewise. The uninstall
  afterwards also removed the task folder (all 32 checks passed).
- Self-update: host 0.2.5-winget.1 from winget updated itself to the published 0.2.5 after its idle wait; the entry
  and `winget list` then showed 0.2.5. `winget upgrade` to 0.2.6 over it worked, and so did its uninstall.
- Windows Sandbox (Windows 11 Enterprise 26100, a fresh winget, elevated so no prompts): host 0.2.6 installed in 12 s
  with no window but the test's own, uninstalled in 5 s leaving only the data folder; client 0.2.5 installed in 6 s,
  started, and uninstalled.
- The published 0.2.5 host is not winget-ready (it predates this phase): its uninstall returned 0 after 1 s with
  nothing removed. Submit the first release that carries this phase's installer changes.
