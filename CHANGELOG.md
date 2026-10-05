# Changelog

All notable changes to HyperHarbor (host and client) are recorded here, newest first. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/). Host and client share one version number
(Directory.Build.props and client/src-tauri/tauri.conf.json); the API version (docs/api.yaml info.version)
is noted where it changes.

Sections: Added, Changed, Fixed, Removed, Security. Write entries for the people who use HyperHarbor, and
note anything that needs action, such as updating the host, a new elevation requirement, or a data migration.

## [Unreleased] (API 1.12.0)

### Added
- Setup profiles: YAML files on the host that list what to install (winget ids, Microsoft Store ids, or aliases),
  what to remove (provisioned Appx packages, capabilities, features), registry tweaks, and a browser with its
  extensions and policies. Each id is written with its friendly name as a comment, and files reference a JSON Schema
  (schemas/profile.v1.schema.json) so editors can check them. New Setup profiles tab in the client: build profiles
  with a package picker (Popular, and winget search), an Appx checklist read from a running VM with ratings and a
  debloat preset, a tweak list, and an extension picker that takes a store link; import and export YAML files.
  Applying a profile to a VM comes later.
- Capture setup profile (VM menu, running Windows VMs): reads what the VM has that a clean install does not, and
  shows it in three columns to keep or drop before saving a profile. Removed packages are found by comparing with a
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
