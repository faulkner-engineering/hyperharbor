# Changelog

All notable changes to HyperHarbor (host and client) are recorded here, newest first. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/). Host and client share one version number
(Directory.Build.props and client/src-tauri/tauri.conf.json); the API version (docs/api.yaml info.version)
is noted where it changes.

Sections: Added, Changed, Fixed, Removed, Security. Write entries for the people who use HyperHarbor, and
note anything that needs action, such as updating the host, a new elevation requirement, or a data migration.

## [Unreleased]

## [0.1.5] - 2026-10-05 (API 1.15.0)

### Added
- Update progress: while the host downloads and tests a new version, the client's Updates panel and the host
  window show how far the download is (MB and percent), then checking the package and testing it on a copy of the
  host's data. API: GET /host/update has progress (step, bytesDone, bytesTotal) while preparing; update the host.

### Changed
- A new HyperHarbor Host window on the host PC (double-click the tray icon). It matches the client app, follows
  Windows' light and dark theme, and sizes itself for the display it opens on. Sections are in a sidebar (Overview,
  Devices, Storage, Updates, Logs). Everything you start shows its progress where you started it: setting the
  passphrase, setting up console access or package search, changing a folder, removing a device, and update
  checks (with Check, Download and test, Ready, and Install steps). Confirmations and results appear in the
  window instead of message boxes. The pairing PIN has its own window with a countdown. The tray needs the
  Microsoft Edge WebView2 Runtime, which Windows 11 includes; without it, the tray offers the download and shows
  pairing PINs as notifications.

### Fixed
- Check now could do nothing at all once the host service had run for a while: each quiet minute left a wait
  behind that took the next request. Fixed in the host; update the host to get it.
- Check now (client Updates panel) gave no feedback: the host answers before the check runs, so the button
  went straight back and the old status stayed. The panel now shows Checking… until the host reports the check,
  then says what it found (up to date, a new version, or why the check failed), or that the host did not finish
  within 90 seconds. This works with hosts from before this fix too. The host now reports a requested check as
  checking at once, and the tray's Check now shows Checking… as soon as it is clicked.

## [0.1.4] - 2026-10-05 (API 1.14.0)

### Added
- Apply setup profile (VM menu, running Windows VMs): applies one of your setup profiles to an existing VM, the same
  way as after a new install. Your account's settings change too when you have signed in to the VM before (some
  appear only after you sign out and back in). Choose whether HyperHarbor may restart the VM when a change needs
  it; the dialog lists what was applied and anything that could not be. Needs a host at API 1.14.0.
- API: POST /vms/{vmId}/setup-profile (elevated, audited) starts an applySetupProfile job, whose setupResult says
  what it did; setupResult.restartPending reports a restart left to you.
- Choose a setup profile when creating a Windows VM with an automatic install (Create VM, after the computer name).
  Once Windows is installed and your account is set up, the host installs the profile's apps with winget, removes
  the apps, capabilities, and features it lists, writes its tweaks (your account's settings go to the Default user
  profile, so they apply at your first sign-in) and browser policies, and installs its extensions (you can turn
  them off, not remove them). It restarts the VM if something needs it. The VM list shows "Applying the setup
  profile…", and the ready notice lists anything that could not be applied. The profile is kept as it was when
  the VM was created. Needs a host at API 1.13.0; the host's data format changes to 2 (older records stay as they are).
- API: install.setupProfileId on POST /vms, install state applyingProfile, and setupProfileName and setupResult on
  GET /vms/{vmId}/install. The host audits applySetupProfile with counts only.
- Setup profiles: YAML files on the host that list what to install (winget ids, Microsoft Store ids, or aliases),
  what to remove (provisioned Appx packages, capabilities, features), registry tweaks, and a browser with its
  extensions and policies. Each id is written with its friendly name as a comment, and files reference a JSON Schema
  (schemas/profile.v1.schema.json) so editors can check them. New Setup profiles tab in the client: build profiles
  with a package picker (Popular, and winget search), an Appx checklist read from a running VM with ratings and a
  debloat preset, a tweak list, and an extension picker that takes a store link; import and export YAML files.
  Applying a profile to a VM comes later.
- Capture setup profile (VM menu, running Windows VMs): reads what the VM has that a clean install does not, and
  shows it in three columns to keep or drop before saving a profile. Capture now also lists winget packages in a
  fresh VM whose administrator never signed in (App Installer is registered for it first). Removed packages are found by comparing with a
  clean baseline, recorded automatically after an unattended Windows install or on request.
- Package search needs PowerShell 7 and the WinGet PowerShell module on the host; the tray's Set up package search
  installs both (one administrator prompt).
- API: setup profile routes (list, get, create, update, delete, import, export, schema, catalog), GET /packages/search
  and /packages/catalog, GET /extensions/resolve and /extensions/catalog, GET /vms/{vmId}/appx, POST
  /vms/{vmId}/appx-baseline, POST /vms/{vmId}/profile-capture; problem code wingetUnavailable.
- The host stays awake while a paired device uses it, so a host woken with Wake-on-LAN no longer goes back to
  sleep after about 2 minutes (Windows' unattended sleep timeout). It stays awake for 10 minutes after the
  client's last contact (an open client checks in every few seconds), while work such as a console session or
  a job is in progress, and while someone is signed in to the host over Remote Desktop; then Windows' own sleep
  settings apply again. The client keeps checking in once a minute while Remote Desktop or console windows it
  opened stay open, as long as the client app is running. Running VMs alone do not keep the host awake.
  Update both the host and the client.
- Wake diagnostics also report the unattended sleep timeout and the active power requests.

## [0.1.3] - 2026-10-05 (API 1.11.0)

### Added
- "Install <version> now" in the client's Updates panel installs a downloaded and tested update right away,
  whatever the update mode. It needs the admin passphrase and is audited; work in progress on the host still
  finishes first. Hosts before API 1.11.0 install only from their tray, and the client shows no button for them.
- API: `POST /host/update/install` (elevated), problem code `updateNotReady`.

### Fixed
- The HyperHarbor Host window (double-click the tray icon) no longer runs past the bottom of scaled or
  high-resolution screens. Its sections are on tabs (Overview, Storage, Updates, Logs) that scroll, the window
  can be resized, it opens within the screen's work area, and it refits when moved to a monitor with other
  scaling. The Updates tab reads "Updates (ready)" while a version waits to install. The pairing PIN and
  admin passphrase windows are also kept on screen.

## [0.1.2] - 2026-10-05 (API 1.10.0)

### Added
- Remote Desktop to the host itself for maintenance, from the client's Host menu. mstsc asks for the host's
  Windows account; HyperHarbor stores and sends no host credentials. Home editions are reported as unsupported.
- The host can turn on its own Remote Desktop (and the Windows Firewall "Remote Desktop" rule) when a client
  asks. This needs the admin passphrase and is audited. A host run without administrator rights (a development
  console run) asks the user to turn it on in Windows Settings instead.
- API: `GET /host/remote-desktop` and `POST /host/remote-desktop/enable` (elevated), problem codes
  `remoteDesktopUnsupported` and `requiresInstalledService`.
- Per-VM monitor choice for Connect (one monitor, all, or selected ones), from the VM's Monitors… menu item.
  The choice is kept on each device and names monitors by device path, so it survives rearranging displays.

### Changed
- The host header shows only "+ New VM" (and the elevation countdown); Wake-on-LAN, Updates, Refresh, Unpair,
  and Remote Desktop to host moved into a "Host ▾" menu. The VM row menu uses the same menu component.
- A feature the host does not have yet (an older host) now says to update the host, instead of "Not Found".
  Features from a newer API version are greyed out for hosts that report an older version over mDNS.

### Fixed
- The tray pipe server waits for tray connections to finish their message when it stops.

## [0.1.1] - 2026-10-04 (API 1.9.0)

Version bump used to verify the self-update path (0.1.0 to 0.1.1, and the rollback of a broken build).

## [0.1.0] - 2026-10-04 (API 1.9.0)

First packaged version, covering roadmap phases 1 to 11.7 (see CLAUDE.md):
- Hyper-V VM inventory, power actions, and a Kestrel API with mutual TLS.
- mDNS discovery, PIN pairing (SPAKE2) with certificate pinning, Wake-on-LAN with readiness checks and fixes.
- One-click Remote Desktop to VMs with a rotated per-User account (Windows via PowerShell Direct, Linux via SSH
  and xrdp).
- VM lifecycle: create from an ISO (Gen 2, Secure Boot, vTPM), compute settings, delete; ISO library; audit
  log and admin passphrase elevation.
- VM console through the host, unattended Windows and Linux installs, Performance mode (GPU-P and Remote
  Desktop tuning).
- A single host executable that installs itself as a LocalSystem service, with side-by-side versions,
  self-test, rollback, and update checks against GitHub releases.
