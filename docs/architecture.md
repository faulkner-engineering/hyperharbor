# Architecture

How HyperHarbor is put together: the parts, where each lives in the repository, and the limits known today.
Platform behavior found while building it is in [platform-notes.md](platform-notes.md); how each feature came about is in
[history.md](history.md).

## Overview
- host/ (.NET 8): one executable, HyperHarbor.Host.exe (Kestrel API with mTLS, WinForms tray, installer,
  elevated helpers), on a Core library. Installed as the LocalSystem service HyperHarborHost under
  %ProgramFiles%\HyperHarbor\versions\<version> with a current junction; data stays in %ProgramData%\HyperHarbor.
  - Hyper-V via CIM/WMI (root\virtualization\v2); PowerShell Direct through Windows PowerShell (powershell.exe)
    with an encoded script and secrets on stdin (PowerShellDirectAccountManager)
- client/ (Tauri v2, Rust + TypeScript): discovery, pairing, VM dashboard, RDP launcher
- Discovery: mDNS _hyperharbor._tcp on LAN; manual add by IP/hostname otherwise
- Pairing: PIN shown in tray, certificate exchange and pinning, mTLS afterward
- Users and devices: a User owns many Devices; pairing creates a Device under the current User. The MVP has
  exactly one User (default "owner"), but no schema or API may assume a single User.
- Credentials: each User has one local VM account hh-<username> (local, never a Microsoft account), created
  by a one-time Provision VM flow. On each connect the host rotates its password via PowerShell Direct
  (serialized per VM and User, short reuse window), returns it to the requesting paired Device over mTLS,
  and the client removes it from the OS credential store after launching RDP. For UAC prompts inside a Windows
  VM, each User also has a local administrator account hha-<username>, disabled except during a short
  administrator window (GuestAdminService; elevated to open, one-time passphrase in host memory only, a random
  password and disabled again when it ends). Design and the planned guest agent: [guest-elevation.md](guest-elevation.md).
- Wake-on-LAN: readiness check with auto-fix, Test Wake, relay mode later
- VM lifecycle: create from an ISO in the host's library, change compute settings, delete; long operations
  are jobs the client polls. Power actions, lifecycle jobs, and settings changes take a per-VM lock.
- Audit and elevation: every state-changing request is audited (audit.log in the data directory). Delete,
  create, compute changes, and turnOff need a 5-minute elevation token from the admin passphrase set in the tray.

## Repository layout
- models/machine: source for the profile builder's wireframe machine (build_model.py generates the glTF, the camera
  and highlight maps, and the exploded still with trimesh; viewer.html is the Three.js reference implementation; shots/
  are renders). The runtime assets it writes live in client/static/machine (hyperharbor-machine.glb, targets.json,
  highlights.json, exploded.svg).
- docs/api.yaml: OpenAPI 3.1 contract, the source of truth for host and client
- host/src/Shared.Contracts: DTOs mirroring api.yaml; ContractJson holds the wire JSON options
- host/src/Host.Core: HyperV/ (CIM reader, VmMapper), Power/ (actions), Discovery/ (DNS-SD), Identity/,
  Pairing/ (Spake2, PairingService), Security/ (host certificate, paired devices, ProtectedFile),
  Audit/ (FileAuditLog), Elevation/ (AdminPassphraseStore, ElevationService), Lifecycle/ (create, delete,
  compute, jobs, locks, ISO library), HyperV/HyperVCim, CimXml, CimVmSettings (shared CIM helpers),
  VmConsole/ (console account store and setup, password rotator, tickets, tunnel pump, CIM console grants),
  Power/ (VM power actions; KeepAwake: the power request that keeps the host awake during remote use),
  Profiles/ (setup profiles: Catalogs, ProfileYamlReader/Writer, ProfileValidator, PackageAliasResolver,
  ExtensionIdParser, SetupProfileStore, AppxBaselines, AppxInventoryService, GuestProfileReader (PowerShell Direct
  scripts), PackageSearch (pwsh fallback), WingetIndex and WingetPackageSearch (the downloaded winget index, read
  through WinSqlite over winsqlite3.dll), ExtensionResolver, ProfileCaptureService),
  Provisioning/ (ProvisioningService, ConnectService, PasswordRotator, VmCredentialStore, GuestAccounts with
  IGuestAccountManager, PowerShellDirectAccountManager (its Script and AdminScript), SshAccountManager; administrator
  windows: GuestAdminService, GuestAdminStore (guest-admin.json), PassphraseGenerator with PassphraseWords.txt (embedded))
- catalogs/*.yaml (packages, extensions, appx, tweaks, browsers) and schemas/profile.v1.schema.json at the repository
  root, embedded in Host.Core; they are data people read and edit, checked by CatalogTests and ProfileFormatTests
- host/src/Host.Service: Kestrel API (Api/, including AuthEndpoints and JobEndpoints), device auth and
  elevation filter (Security/), audit filter and job audit (Audit/), tray pipe server (Tray/), mDNS (Discovery/),
  ConsoleEndpoints (console session and upgraded tunnel), VmConsole/ConsoleSetupCommand (elevated --setup-console),
  GuestAdminEndpoints and GuestAdminMonitorService (the administrator window sweep).
  It builds HyperHarbor.Host.exe (WinExe); Program.cs picks the mode with Installation/HostCommandLine (service,
  console run, --tray, launcher, install, uninstall, helpers). Installation/: HostInstaller (elevated steps),
  InstallCommand (elevates one copy of itself, relays progress through a result file), ServiceRegistration (SCM),
  Launcher (double-click), TrayUser (tray SID from the service's Parameters key), ConsoleAttachment
- host/src/Host.Core/Installation: SemanticVersion, Junction (mount point reparse points), InstallLayout
  (versions folders, current junction, stage, activate, prune), DataFormat (data-format.json; migrations keyed
  by format; a newer format stops the host), DataBackup (copy and restore with ACLs, never logs or the audit
  trail), SelfTestGate (copies the data to <data>\update\selftest and runs the new exe's --self-test);
  Security/DataDirectoryAcl. Host.Service/Installation/SelfTestRun: the real host on the copy, loopback port,
  no mDNS or install watcher; checks dataFormat, stores, start, tls (pinned host certificate), inventory.
- Update helper: UpdateApplier (Host.Core/Installation) is the state machine; update\state.json (UpdateStateStore)
  saves each phase first, so a rerun resumes (Stopping/BackingUp/Flipping: keep From; Starting: commit if To is
  healthy, else roll back; RollingBack: finish). Healthy = update\health.json (HealthReporter, installed service
  only) from a live process started after the attempt, plus HostTlsCheck on its port. The scheduled task
  "HyperHarbor\Update" (UpdateTask, from XML so it runs on battery) runs root\hh-update.exe update-run as SYSTEM
  at startup and on demand; hh-update.exe is a copy of the installed version, refreshed after each update.
- Service side (installed service only): UpdateService ticks UpdateCoordinator every minute (and at once on a
  check or install request): check when due, prepare an available version, hand off when UpdateSchedule allows
  (Auto: idle for IdleMinutes, or inside the window after MaintenanceTime with nothing in progress) or on
  Install now. HostActivity is the gate: state-changing requests in flight, running jobs, a pending pairing;
  reads do not count. Handing off closes it, and UseUpdateGate then answers state-changing requests with 503
  code updating and Retry-After. A handoff the helper never picks up is taken back after 10 minutes.
- Update settings: UpdateSettings applies the owner's UpdatePreferences (host-settings.json, set by PUT
  /host/update/settings or the tray's channel menu) over UpdateOptions. UpdateEndpoints.Status builds the
  HostUpdateStatus for the API and the tray; a host run without installing reports supported=false and
  answers check and settings with 409 updatesUnsupported. The tray polls UpdateStatusQueryMessage while the
  host window is open (every second while something moves); Install now comes from the tray (InstallUpdateMessage) or from POST /host/update/install
  (elevated, audited; 409 updateNotReady unless a version is prepared). The endpoint calls RequestInstall in
  Response.OnCompleted, because its own in-flight request would otherwise keep HostActivity busy until the next tick.
- Update progress (API 1.15.0): UpdatePreparer reports UpdateProgress (downloading with bytes from UpdateDownloader,
  at most every 250 ms; verifying; testing) through InlineProgress (Progress<T> would post to the thread pool and
  reorder reports); UpdateCoordinator.Status carries it while preparing, and HostUpdateStatus.progress exposes it.
  The coordinator's WaitForNextTickAsync keeps one pending request wait across ticks (see UpdateCoordinatorTests).
- host/src/Host.Core/Updates: UpdateOptions (Update section: channel, channel manifest URLs, allowed hosts,
  package size cap), UpdateManifest (latest.json schema 1 and its rules), UpdatePolicy (never a downgrade,
  skips rolled-back versions, minimumUpdateFrom), UpdateDownloader (https and allowed hosts on every redirect
  hop, size and SHA-256 while streaming), PackageSignatures (UnsignedPackageVerifier holds the marked SIGNING
  HOOK), UpdatePreparer (manifest, decision, then download, hash, signatures, self-test; a failure deletes it).
- host/src/Host.Core/HostProfiles: the Lean host action. HostCatalogs (catalogs/host-startup.yaml allowlist,
  host-programs.yaml, host-guards.yaml), HostDiffer (pure: plan + HostSnapshot + Steam games to HostDiff, with the
  guards), HostDiff (changes, kept notes, fingerprint), UndoProfileBuilder, SteamLibrary, HostLeanService (dry run gate,
  backups, apply, undo, schedule tick), HostLeanStore (host-leanstate.json, undo.yaml, backup), IHostSystem with
  PowerShellHostSystem (scripts in HostScripts, run by HostPowerShell), WindowsHostMetrics. Host.Service:
  HostLeanSchedulerService (hourly check, installed service only), TrayPipeServer (Lean messages). Tray IPC types are
  in Shared.Contracts/Ipc/HostLeanMessages.cs. The shipped profile is profiles/host-gaming.yaml (embedded).
- host/src/Host.Tray: the tray (a library; TrayApp.Run is single-instance per session). The notification icon, its
  menu, the pipe client, and ShutdownGuard are WinForms; the windows are HTML in WebView2 (since 2026-10-05, a deliberate choice). TrayApplicationContext owns both windows (the host window, double-click the icon; the pairing PIN
  window), pushes one TrayViewState to them on every change, and implements ITrayActions for what the page asks.
  Window/: WebWindow (Form + WebView2; serves the embedded app from https://tray.hyperharbor.invalid/, blocks other
  navigation, opens only the project's GitHub links outside, shows itself when the page posts "ready", hides on
  close), TrayWebView (the shared environment, data in %LOCALAPPDATA%\HyperHarbor\TrayWebView), TrayUiResources,
  BridgeMessages (TrayViewState and the messages to the page), TrayCommands (the page's messages to ITrayActions).
  webui/: the app (Vite + Svelte 5 + TypeScript, no SvelteKit): store.svelte.ts, bridge.ts (mirrors BridgeMessages;
  bridge.fixtures.json from TrayWindowTests, HH_WRITE_TRAY_FIXTURES=1), text.ts (all status words), pages/, components/.
  The project's EmbedTrayUi target runs npm ci (when node_modules is missing) and npm run build, then embeds dist.
  Without the WebView2 Runtime the tray offers its download and shows the PIN in a notification.
- host/tests/Host.Tests: xUnit; Api tests use TestHost (WebApplicationFactory, fakes, client cert via header)
- client/src-tauri/src: hosts.rs, discovery.rs (mdns-sd), api.rs (reqwest), spake2.rs, tls.rs (pinning),
  identity.rs (key in Credential Manager), paired.rs, rdp.rs (mstsc launch), console.rs (loopback listener and
  tunnels for the VM console), store_search.rs (opens an extension store's
  search page), library.rs (the library on this device: ISOs and machine profiles with the hosts that
  have a copy; UI in lib/components/Library.svelte), camera/ (see below); client/src: SvelteKit SPA. The window has no Windows title bar
  (decorations off in tauri.conf.json); lib/components/TitleBar.svelte is the first row of the page grid, with its
  own window buttons (window permissions in capabilities/default.json), and overlays start below --titlebar-height
  (app.css). Lifecycle UI:
  lib/lifecycle.svelte.ts (elevation prompt, job polling) and lib/components/*Dialog.svelte on a shared Dialog
- Machine view (the setup profile editor's left pane; each tab is a section of targets.json): client/src/lib/machine:
  camera.ts (targetFor, rigFor, CameraRig: 900 ms eased fly, tilted rest orbit slowing with distance, cuts under reduced
  motion), highlights.ts (resolveHighlight for perItem, blink, pulse, spin; applyHighlight; reactionFrame), fallback.ts
  (webglAvailable, FrameRateWatch, reduced-motion query), assets.ts (loaders, parseExplodedSvg, the amber palette from the
  --hh-crt-* tokens in app.css), scene.ts (three r170, imported on demand: EdgesGeometry lines, UnrealBloomPass, CRT
  ShaderPass). UI: MachineView.svelte (3D or the still; motion setting; bindable status) and MachineStill.svelte
  (exploded.svg with the same highlights). ProfileWorkspace.svelte is the editors' shell (section list with groups,
  the machine with its HUD and DetailCard, the form panel, Motion beside Cancel and Save) in Dialog's terminal variant;
  SetupProfileEditor and MachineProfileEditor fill it. lib/softwareDraft.svelte.ts (SoftwareDraft) and
  SoftwarePanel.svelte are the software sections both editors share; lib/machineDraft.svelte.ts (MachineDraft) holds
  the machine sections, the reactions (ControlState), and the HUD text; AccountSection, DisplaySection,
  FirmwareSection, ComputeSection, GpuSection, NetworkSection, and BackupSection edit them, with Choice.svelte for
  "as it is / on / off" choices. MachineProfiles.svelte is the list tab; CreateVmFromProfileDialog.svelte makes a VM
  from a profile. Host side: Host.Core/Profiles MachineProfileYaml, MachineProfileValidator, MachineProfileStore,
  MachineProfileVmCreation (profile to CreateVmRequest plus CreationExtras for VmCreationService), MachineProfileApplier
  (preview and the applyMachineProfile job for an existing VM; client ApplyMachineProfileDialog.svelte),
  ProfileConsolidation (data format 4: older profiles to machine profiles), MachineProfileTemplates (the built-in
  install profiles as templates), ProfileImageStore (each User's wallpaper images, /profile-images) and GuestFileCopier (IGuestFileCopier: Copy-Item
  -ToSession over PowerShell Direct, used by the applier and the install watcher to put the image in the guest);
  Host.Service/Api/MachineProfileEndpoints and MachineBackupPolicies (IMachineBackupPolicies over the backup engine).
  Each software section is a component (InstallSection, RemoveSection,
  TweaksSection, BrowserSection) built on CatalogBrowser.svelte (filter and category chips pinned in the panel's one
  scroll area), ProfileChips.svelte (what the profile holds), and DetailCard.svelte (the selected entry's details on
  the machine stage); lib/catalogBrowser.ts has the filtering and the Detail type.
- Camera sharing (Rust workspace rooted at client/src-tauri, which lists ../vcam-protocol and ../vcam):
  - client/vcam-protocol: no dependencies; the pipe frame format (FrameHeader, 32 bytes then NV12), pipe_name,
    SOURCE_CLSID, and NV12 black and scale. Used by both sides.
  - client/vcam: cdylib hyperharbor_vcam.dll, the media source the Windows Frame Server loads (activator, source,
    pipe reader, DllRegisterServer). Release build is staged by package.ps1 into client/src-tauri/resources (git-ignored)
    and added to the installer only through tauri.camera.conf.json.
  - client/src-tauri/src/camera: naming, resolve (symbolic link lookup), policy (Live, Frozen, Blurred, Shuttered),
    fanout (policy per session, one blur for all), frame, failure (HRESULT classes), ledger and reconcile (orphans),
    support (Windows 11 check), mfapi (Media Foundation functions looked up at run time), winmf (virtual cameras,
    listing), capture (physical camera thread), pipe (PipeSink, the pipe server), backend (the Windows Backend),
    service (CameraService: prepare, attach_process, end, shutter, prefs, status; tested through a fake Backend),
    setup and setup_windows (elevated install and removal), live (ignored live tests). UI: CameraDialog.svelte.
- Client updates (client/src-tauri/src/update): settings (UpdateSettingsStore), install_kind (uninstall.exe beside the
  exe means installed, else portable), service (UpdateService over a Backend trait; phases idle, checking, upToDate,
  available, downloading, failed; sessions guard), plugin (PluginBackend wraps tauri-plugin-updater). Channel manifests:
  releases/latest/download/client-latest.json (stable) and releases/download/channel-beta/client-latest.json (beta), in
  Tauri's format, written by package.ps1 when TAURI_SIGNING_PRIVATE_KEY is set (it adds tauri.updater.conf.json, which
  turns on createUpdaterArtifacts, and copies the installer's .sig). The public key is plugins.updater.pubkey in
  tauri.conf.json; the private key and its password are the repository secrets TAURI_SIGNING_PRIVATE_KEY and
  TAURI_SIGNING_PRIVATE_KEY_PASSWORD (losing the key means installed clients cannot update; back it up). UI:
  ClientUpdate.svelte in the sidebar.
- docs/pairing.md: the SPAKE2 pairing protocol; both implementations must match it and Spake2Vectors.json

## Known limitations
- Tray pipe squatting: a local process started before the service could claim HyperHarbor.Host.Tray. The tray
  should verify the pipe server process. (The pipe ACL now admits only SYSTEM, Administrators, and the
  service account; TrayPipeServerTests check it.)
- Anyone on the LAN can repeatedly start pairing requests (PIN window spam). Consider rate limiting.
- Installed service, not yet verified live as LocalSystem: Connect (PowerShell Direct), console password
  rotation (NetUserChangePassword), Wake-on-LAN fixes from the tray, the double-click launcher dialogs, and a
  real Windows shutdown with a GPU VM running. Verified: inventory, mDNS, tray pipe, log access, update, ACLs.
- The client has no VM console, so installing an OS on a new VM needs the host's Hyper-V console.
- Jobs live in host memory: a service restart forgets them, and a VM whose creation was interrupted keeps its
  "creation in progress" note. Elevation tokens also end when the host service or the client restarts.
