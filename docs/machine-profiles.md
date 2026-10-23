# Machine profiles (phase 26)

Status: design for review (2026-10-23). Stage 26a (catalog discovery) is done; this document covers 26b to 26d.
Decisions already made with the user are in [history.md](history.md), entry 26.

A machine profile describes a whole VM in one document: who signs in, how it looks and connects, its firmware and
hardware, its GPU share, network, backups, and its software. The setup profile editor becomes the machine profile
editor; each section of the wireframe machine is a section of the document.

## Goals and limits
- One self-contained document per profile. It names nothing secret and holds no password.
- Portable between hosts: anything that exists only on one host (a switch, a GPU, a backup target) is named, not
  referenced by id, and resolved when the profile is applied. What does not resolve is reported in the preview.
- Applies when a VM is created (26c) and to an existing VM (26d). Applying never deletes data: no disk is shrunk or
  replaced, a TPM is never removed, software is only added or removed as listed (as setup profiles do today).
- Every field is optional except `name` and `os`. A missing section means "leave as it is" when applied to an existing
  VM, and the host defaults when creating one.

## The document
YAML on disk and in export, JSON on the wire (`MachineProfile` in api.yaml). `schemaVersion: 1`, with a JSON schema in
schemas/machine-profile.v1.schema.json like the setup profile's.

```yaml
schemaVersion: 1
name: Gaming rig
description: Steam, Discord, and a GPU share
os: windows                      # windows | linux

account:                         # today's unattended install profile
  adminAccountName: hhadmin
  timeZone: null                 # Windows time zone ID or IANA ID; null = the host's
  locale: en-US
  computerName: null             # Windows; null = derived from the VM name
  windows:
    defaultEdition: Windows 11 Pro
    bypassHardwareChecks: false
    disableTelemetry: true
    disableAdvertisingId: true
    disableLocation: true
    disableConsumerFeatures: true
  linux:                         # when os is linux
    sshAuthorizedKeys: []
    packages: []
    installDesktop: true

display:
  guest:
    theme: dark                  # dark | light | null (leave as installed)
    consoleResolution: 1920x1080 # the Hyper-V console only; Remote Desktop follows the client window
  connection:                    # defaults a client may override per device
    monitors: one                # one | all
    window: fullScreen           # fullScreen | windowed
    redirect: { clipboard: true, drives: false, printers: false, microphone: true, camera: true }

firmware:
  tpm: true
  secureBoot: true               # the template follows os (Microsoft Windows, or the UEFI CA for Linux)

compute:
  processors: 4
  memory: { startupMb: 8192, maximumMb: 16384, dynamic: true }
  nestedVirtualization: false
  diskSizeGb: 128                # used when creating; on an existing VM only a larger size is offered

gpu:                             # Performance mode
  enabled: true
  adapter: null                  # a name to match on the host; null = its first partitionable GPU
  share: { vramPercent: 50, encodePercent: 50, decodePercent: 50, computePercent: 50 }
  hardwareEncoding: false

network:
  switch: Default Switch         # by name; null = the host's default switch
  macAddressSpoofing: false

backup:
  enabled: true
  target: NAS                    # a backup target's name on the host; the schedule belongs to the target
  triggers: { schedule: true, onShutdown: false, debounceMinutes: 10 }
  retention: null                # null = the target's retention
  conditions: null               # null = the target's conditions
  gpuMode: requireOff

install: [Valve.Steam, Discord.Discord]   # the software sections, unchanged from setup profiles
remove: { appx: [Microsoft.BingNews], capabilities: [], features: [] }
tweaks: [{ id: explorer.showFileExtensions }]
browser: { app: { id: Brave.Brave }, extensions: [], policies: {} }
```

### Where each field comes from
| Section | Existing contract it reuses | Notes |
|---|---|---|
| account | UnattendProfileRequest, UnattendedInstallRequest.computerName | Same names and limits. The admin password stays a one-time password rotated after setup. |
| display.guest | new | theme writes the same values as the appearance.darkMode tweak; consoleResolution sets the VM's video resolution. |
| display.connection | the client's monitor choice (MonitorsDialog) and rdp_file | New: window and redirect. Stored in the profile, read by the client when it connects; a device's own choice wins. |
| firmware | CreateVmRequest.enableTpm | secureBoot is new on the wire; today Secure Boot is always on, with the template chosen from the ISO's OS (VmCreationService). |
| compute | CreateVmRequest, UpdateVmComputeRequest | Same rules as the compute dialog. |
| gpu | PerformanceSettings, GpuPartitionShare | With gpu.enabled, memory must be fixed (dynamic false), as Performance mode requires today. |
| network | CreateVmRequest.switchId, macAddressSpoofing | The switch by name. |
| backup | BackupPolicy | The target by name. Schedules stay on targets, so a profile cannot carry another host's schedule. |
| install, remove, tweaks, browser | SetupProfile | Unchanged, including aliases and the validator. |

### Validation (the host's MachineProfileValidator, issues as today)
- name 1 to 60 characters; os windows or linux; account.windows only with windows, account.linux only with linux.
- compute: processors and memory within what the host has (a warning, not an error, since the profile may be meant for
  another host); maximumMb at least startupMb; with gpu.enabled, dynamic must be false.
- gpu only for windows; secureBoot and tpm as Create VM allows for the os.
- display.guest.consoleResolution one of the resolutions Hyper-V accepts.
- The software sections go through the setup profile validator unchanged.
- Names (switch, gpu.adapter, backup.target) are checked only when applying, against the target host.

## On the machine model
| Section | Node | Reaction |
|---|---|---|
| account | Screen_Login | |
| display | Screen | |
| firmware | TPM_Chip, TPM_LED | blink while tpm is true |
| compute | CPU, CPU_Cooler, RAM_1..4 | |
| gpu | GPU, GPU_Fan_1..2 | spin while gpu.enabled |
| network | Ethernet, RearIO | |
| backup | Backup_Disk, Backup_LED, cable | pulse while backup.enabled and the target has a schedule |
| install, remove, tweaks, browser | as today | perItem for install |

targets.json and highlights.json already have every one of these sections.

## Storage and API (26b)
- Host: MachineProfileStore, one YAML file per profile in &lt;data&gt;\machine-profiles\&lt;user id&gt;\&lt;id&gt;.yaml with the
  ProtectedFile ACL, the same shape as SetupProfileStore (files are the source of truth; a broken file is listed with
  its problem). A new folder, so DataFormat.Current does not change.
- Routes, mirroring /setup-profiles: GET and POST /machine-profiles, GET, PUT, DELETE /machine-profiles/{id},
  GET /machine-profiles/{id}/export (YAML), POST /machine-profiles/import, GET /machine-profiles/schema. Writes are
  .Audited() and need elevation like setup profiles. API 1.27.0.
- Client: the editor gains the seven new sections. The section list groups them: Identity (account, display),
  Hardware (firmware, compute, gpu, network), Care (backup), Software (install, remove, tweaks, browser).

## Moving from the two profile types
Setup profiles and unattended install profiles keep working, and their routes stay for older clients. The new host
does not change them. To bring them over:
- Each machine profile section has "Fill from…", which copies an existing setup profile into the software sections or
  an unattended install profile into account.
- "Convert" on a setup profile makes a machine profile from it (software only, os windows), and the setup profile
  list gets a note that machine profiles replace it.
- Once machine profiles can create VMs (26c), Create VM offers machine profiles first and the two older choices under
  "Older profiles". Removing the old types is a later release, after a version that warns.

## Applying (26c and 26d)
- Create (26c): Create VM takes a machine profile and an ISO. One job runs the existing steps in order: create the VM
  (firmware, compute, network, disk), unattended install (account), wait for ready, setup (software and display.guest),
  Performance mode with guest setup (gpu), backup policy. A step that fails stops the chain and says which step; what
  was done stays, as with today's jobs.
- Existing VM (26d): GET /vms/{vmId}/machine-profile-preview?profileId= lists each change with what it needs (the VM off,
  a restart, the guest running with a credential) and what does not resolve on this host. The apply job runs only the
  parts the user ticks. Firmware: a TPM can be added, never removed; Secure Boot is left as it is.

## Open points for review
1. Migration: "Fill from…" and "Convert" as above, rather than converting every old profile on upgrade. Converting
   automatically would make one machine profile per setup profile and per unattended profile, which then need merging
   by hand.
2. display.guest is small (theme and console resolution) because Remote Desktop sets the guest resolution and scaling
   from the client. Anything else you expect there, for example wallpaper or accent color?
3. display.connection redirect toggles add clipboard, drives, printers, microphone, and camera choices to the client.
   Today the .rdp file sets audio, microphone, camera, and WebAuthn, and leaves clipboard, drives, and printers to
   mstsc's defaults. Keep that list?
4. backup.target by name: a profile applied on a host without that target gets a preview warning and no policy.
