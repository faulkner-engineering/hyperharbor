# HyperHarbor

Host/client app that manages Hyper-V VMs on a home PC and connects to them in one click from any device. Think "Moonlight for Hyper-V."

## Architecture
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
  and the client removes it from the OS credential store after launching RDP.
- Wake-on-LAN: readiness check with auto-fix, Test Wake, relay mode later
- VM lifecycle: create from an ISO in the host's library, change compute settings, delete; long operations
  are jobs the client polls. Power actions, lifecycle jobs, and settings changes take a per-VM lock.
- Audit and elevation: every state-changing request is audited (audit.log in the data directory). Delete,
  create, compute changes, and turnOff need a 5-minute elevation token from the admin passphrase set in the tray.

## Roadmap (one phase per session, read-only first)
1. Solution skeleton + OpenAPI contract (done)
2. List VMs and state (read-only) (done)
3. Start/stop + Kestrel API on localhost (done)
4. mDNS discovery + client VM list (done)
5. PIN pairing + mTLS (done)
6. Wake-on-LAN (done)
7. One-click RDP with a per-User VM account (Windows guests via PowerShell Direct; Linux guests via SSH
   and xrdp, added at the user's request)
8. VM lifecycle, added at the user's request (done 2026-10-03; awaiting the user's test of the packaged client):
   audit log and admin passphrase elevation; delete VM (off only, optional disks and checkpoints, refuses
   shared parent disks); host resources, ISO library, switches; create Gen 2 VM from ISO (Secure Boot, vTPM)
   as a job; compute settings (vCPU, memory, nested virtualization, MAC spoofing) with shut down and apply;
   client power controls and dialogs for all of it.
9. Console access and unattended provisioning, added at the user's request (done 2026-10-04): VM console through a
   tunnel to the host's port 2179 with a per-User standard host account (hhc-<username>); autounattend and
   cloud-init seed ISOs on create; KVP readiness watcher that provisions automatically once RDP answers.
10. Performance mode (GPU-P and Remote Desktop tuning) for Windows VMs, added at the user's request (done
   2026-10-04; host side verified live on a throwaway VM, guest setup not yet): apply to an off VM (fixed memory, vCPU, GPU partition share, MMIO gaps,
   no checkpoints, TurnOff stop action, optional storage move); guest setup over PowerShell Direct (driver
   copy, ADMX-verified RDP policy, DWMFRAMEINTERVAL); LAN .rdp tuning; driver drift and re-sync; disk export
   before changes; pre-shutdown of GPU VMs; GPU driver error warnings.
11. Host self-update with side-by-side versions, added at the user's request (in progress). Releases go to
   https://github.com/faulkner-engineering/hyperharbor (latest.json manifest: version, URL, SHA-256). Decided:
   health.json plus a TLS check instead of an anonymous health route; "install now" in the tray and (since
   2026-10-05, the user's decision, API 1.11.0) from a client with elevation; stable
   channel by default; Authenticode and Sigstore only as a marked hook. Steps: 11.1 installer (done 2026-10-04:
   single executable, LocalSystem service, verified live including an update from 0.1.0 to 0.1.1), 11.2 data
   format marker, backup, and --self-test (done 2026-10-04; self-test passed on a copy of this host's real
   data), 11.3 manifest and download (done 2026-10-04; not yet against a real GitHub release), 11.4 hh-update helper (junction flip,
   rollback after two failed starts, recovery at boot; done 2026-10-04, verified live: 0.1.0 to 0.1.1, and a
   broken 0.1.2 rolled back to 0.1.1 in 10 s), 11.5 idle gate and maintenance window (done 2026-10-04; unit
   and API tests only until a real release exists), 11.6 API, tray, and client (done 2026-10-04: GET
   /host/update, POST /host/update/check, PUT /host/update/settings elevated, API 1.9.0; tray Updates and
   Update channel rows with Check now and Install now; client Updates panel), 11.7 release packaging
   (done 2026-10-04; the workflow has not run on GitHub yet), 11.8 live test against a published release.
12a. Setup profiles: capture and pickers, added at the user's request (done 2026-10-05, API 1.12.0). YAML profiles per
   User (install: winget/Store ids or aliases; remove: Appx, capabilities, features; tweaks; browser with extensions
   and policies), JSON Schema, curated catalogs, Appx inventory with clean baselines, winget search, extension
   resolver, capture from a VM, and the client's editor, pickers, and capture review. Golden images are later (the
   Appx route takes ?source=vm so another source fits).
12b. Apply setup profiles, added at the user's request. Part 1 (done 2026-10-05, API 1.13.0): a setup profile chosen in
   the create dialog (install.setupProfileId, Windows only, snapshotted into the install record, data format 2) is
   applied by the install watcher after the account is set up (state applyingProfile; baseline recorded first;
   SetupProfilePlanner, SetupProfileApplication, PowerShellDirectProfileApplier: packages, removals, settings), with
   one restart if needed, then Ready with setupResult. Verified live 2026-10-05: the applier on HyperHarbor-Test, and
   create to Ready on HyperHarbor-Test2 (17 min, 7 items, no problems). Part 2 (done 2026-10-05, API 1.14.0): POST
   /vms/{vmId}/setup-profile starts an applySetupProfile job (SetupProfileJobs) on a running Windows VM; HKCU values also
   go to the User's account hive when it has a profile in the VM; restartIfNeeded uses GuestRestart (shared with the
   watcher); the job's setupResult carries restartPending. Client: VM menu Apply setup profile… (ApplySetupProfileDialog). Verified live 2026-10-05 (HH_PROFILE_EXISTING_LIVE): 7 items, the account hive written. No
   removal in the live runs needed a restart (MathRecognizer, SmbDirect), so the restart path is unit tested only.

13. Lean host, added at the user's request (2026-10-07; API 1.17.0; code complete and unit tested, handlers
   partly verified on this PC, never applied to it): the setup profile engine targets the host itself (profile
   `target: host`; sections services, startup, power, remove.programs, registry type absent) and the tray's Lean host
   page applies the shipped profiles/host-gaming.yaml: dry run required, restore point and registry export before
   the first apply, guards and a startup allowlist, an undo profile from the diff, idle memory and process count
   before and after, a monthly re-apply. See "Lean host" under Gotchas.

v2 (paid tier, not in MVP): per-user accounts with roles and SSO mapping. Also out of MVP scope:
per-device VM accounts and a user management UI.

## Rules
- MVP scope only. Do not implement later phases early.
- Professional code and comments. No slang. No em dashes.
- Small commits with clear messages. Run tests before committing.
- Always update CHANGELOG.md with every user-visible change (features, behavior changes, fixes, API version
  changes, anything a user must act on), in the same commit. Add entries under [Unreleased]; when the version
  is bumped, move them under the new version with its date.
- Never store VM passwords in plaintext. Never bind the API to 0.0.0.0 without mTLS.
- Rotated VM passwords live in host memory only for the reuse window and are never logged or persisted.
  VM admin credentials for provisioning are stored only with DPAPI through ProtectedFile.
- Ask before any destructive Hyper-V operation, except on throwaway VMs created for tests (deleting those is allowed).
- After any change, without asking (the user's standing instruction): stop everything running from dist/
  (HyperHarbor.Host processes: host and tray; the portable client), rebuild with
  scripts\package.ps1 -Fast (add -SkipTests once the change's tests have passed, and -HostOnly or -ClientOnly
  when only one side changed), then relaunch: dist\host\Start-HyperHarbor.ps1 (service and tray) and
  dist\HyperHarbor-Client-<ver>-portable.exe. Report it if the rebuild fails, and leave things stopped.
  Start-HyperHarbor.ps1 refuses while the HyperHarborHost service is installed; uninstall it first.

## Layout
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
  scripts), PackageSearch (pwsh), ExtensionResolver, ProfileCaptureService)
- catalogs/*.yaml (packages, extensions, appx, tweaks, browsers) and schemas/profile.v1.schema.json at the repository
  root, embedded in Host.Core; they are data people read and edit, checked by CatalogTests and ProfileFormatTests
- host/src/Host.Service: Kestrel API (Api/, including AuthEndpoints and JobEndpoints), device auth and
  elevation filter (Security/), audit filter and job audit (Audit/), tray pipe server (Tray/), mDNS (Discovery/),
  ConsoleEndpoints (console session and upgraded tunnel), VmConsole/ConsoleSetupCommand (elevated --setup-console).
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
  menu, the pipe client, and ShutdownGuard are WinForms; the windows are HTML in WebView2 (since 2026-10-05, the
  user's choice). TrayApplicationContext owns both windows (the host window, double-click the icon; the pairing PIN
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
  tunnels for the VM console); client/src: SvelteKit SPA. Lifecycle UI:
  lib/lifecycle.svelte.ts (elevation prompt, job polling) and lib/components/*Dialog.svelte on a shared Dialog
- docs/pairing.md: the SPAKE2 pairing protocol; both implementations must match it and Spake2Vectors.json

## Commands
Toolchains are not on Git Bash PATH. Prefix: export PATH="/c/Program Files/dotnet:/c/Program Files/nodejs:$HOME/.cargo/bin:$PATH"
- Everything CI runs (build, tests, lint, type drift, audits): powershell -ExecutionPolicy Bypass -File scripts\test-all.ps1
  [-HostOnly|-ClientOnly] [-Coverage] [-SkipAudit]. CI: .github/workflows/ci.yml (windows-latest).
- Host build/test: dotnet build HyperHarbor.sln -warnaserror && dotnet test HyperHarbor.sln
- Run host API (https://*:48443, mTLS): dotnet run --project host/src/Host.Service (opens its own console
  window); pairing needs the tray: dotnet run --project host/src/Host.Service -- --tray
- Install, update, remove (one UAC prompt each): HyperHarbor.Host.exe install [--port N] | uninstall [--remove-data];
  double-clicking the exe does the same with dialogs. HyperHarbor.Host.exe --help lists every mode.
- Host logs: the console window and %ProgramData%\HyperHarbor\logs\host-yyyyMMdd.log (14 days); audit trail in
  %ProgramData%\HyperHarbor\audit.log. Both have the ProtectedFile ACL (Administrators, SYSTEM, service account).
- Print VM inventory JSON: dotnet run --project host/src/Host.Service -- --list-vms
- Lint contract: npx @redocly/cli lint docs/api.yaml
- Client (from client/): npm run check | npm test (Vitest) | npm run gen:api | npm run tauri dev
- Rust (from client/src-tauri, in PowerShell): cargo fmt; cargo clippy --all-targets -- -D warnings; cargo test
- Live mDNS browse (host service running): cargo test live_browse -- --ignored --nocapture
- Live pairing over LAN (service running, something writing the PIN to the file):
  HH_LIVE_HOST=<lan ip> HH_PIN_FILE=pin.txt cargo test live_pairing -- --ignored --nocapture
- End to end on loopback (no LAN, no setup): powershell -ExecutionPolicy Bypass -File scripts\e2e.ps1
  It starts the real service on a free loopback port, so it can run while a packaged host is running.
- Live lifecycle (creates, changes, and deletes HyperHarbor-Test; ask first):
  HH_LIFECYCLE_LIVE=1 dotnet test HyperHarbor.sln --filter LifecycleLiveTests
- Live Performance mode (same VM; GPU partition, storage move, export; ask first):
  HH_PERFORMANCE_LIVE=1 dotnet test HyperHarbor.sln --filter PerformanceLiveTests

## Packaging (multi-machine testing)
- powershell -ExecutionPolicy Bypass -File scripts\package.ps1 [-Fast] [-SkipTests] [-HostOnly|-ClientOnly]
- Output in dist/ (git-ignored): HyperHarbor-Host-<ver>.exe (the whole host; about 187 MB uncompressed with
  -Fast), dist\host (the same exe plus portable Start/Stop-HyperHarbor.ps1 for development), client NSIS setup
  exe, portable client exe. The script fails if the host publish yields more than the one exe.
- -Fast is for test builds: the client without LTO at opt 1, incremental (a small client change rebuilds in
  seconds), an uncompressed host executable. Switching between fast and full recompiles once
  (about 6 min). Measured 2026-10-04: thin LTO took 170 s for a one-line client change, -Fast about 7 s.
- Upload throughput on loopback (2026-10-04): about 320 MB/s into the host and 351 MB/s from the Rust client
  (IsoUpload_Throughput with HH_BENCHMARK=1; upload_throughput with HH_E2E_SERVICE_EXE).
- Version comes from Directory.Build.props (host) and client/src-tauri/tauri.conf.json (client); keep them equal.
- The script refuses to run while anything is running from dist/ (Windows locks the exe).
- dist\host\Start-HyperHarbor.ps1 does one elevated setup (Private-profile firewall rule for TCP 48443,
  Hyper-V Administrators membership), then starts "HyperHarbor.Host.exe run" and "--tray". Builds are unsigned.
- Verified on 2026-10-02: packaged host plus client paired and listed VMs across machines.
- package.ps1 also writes dist\latest.json with "HyperHarbor.Host.exe write-update-manifest" (the code that
  reads manifests writes it; a prerelease version goes to the beta channel).
- Releases (.github/workflows/release.yml): push tag vX.Y.Z (must equal both versions); the workflow tests,
  runs package.ps1, and creates a DRAFT release with the host exe, latest.json, and the client installer.
  Publishing the draft is the owner's step: stable hosts read releases/latest/download/latest.json (newest
  published non-prerelease); publishing a prerelease copies its latest.json to the fixed "channel-beta"
  release that beta hosts read.

## Contract changes
Edit docs/api.yaml, then update Shared.Contracts (and ContractInfo.ApiVersion if info.version changes),
run npm run gen:api, and lint. OpenApiEnumTests fail if C# enums or the API version drift from api.yaml.
EndpointSecurityTests fail if mapped routes differ from api.yaml or a route other than the pairing handshake
is anonymous. ContractFixtureTests check one sample per DTO against api.yaml; regenerate the shared
ContractFixtures.json with HH_WRITE_CONTRACT_FIXTURES=1 (the Rust tests parse it). Required nullable
properties need [JsonIgnore(Condition = Never)], because ContractJson omits nulls.
Mark required request properties [JsonRequired] (an empty body must not default to action=start).
A nullable object property is `oneOf: [$ref, type: "null"]` (the fixture checker understands that form).
In api.yaml, quote or rephrase plain scalars containing ": " (YamlDotNet in the tests rejects them even when
Redocly does not). The tests read api.yaml from the build output, so rebuild before running them.

## Gotchas
- Build Rust from PowerShell, not Git Bash: Git Bash's /usr/bin/link shadows MSVC link.exe.
- Do not use sed or perl substitutions to edit text with backslashes (Windows paths, C# verbatim strings):
  escapes like \l, \b, and \F were silently turned into other characters. Use a file editor instead.
- Do not run cargo fetch; it downloads every target platform's dependencies (Android, iOS, macOS).
- HyperHarbor.Host.exe is a GUI-subsystem executable: from PowerShell, pipe or redirect its output
  (`| Out-String`) so the shell waits. Anything it starts must not inherit handles (shell execute), or a
  caller reading its output waits until that child exits (the tray did this). MSBuild XML comments cannot
  contain "--".
- Installer and service changes need UAC prompts, so live install tests need the user at the PC.
- Copying an ACL: a FileSecurity read from one file is not written to another (only sections marked changed
  are persisted); go through Get/SetSecurityDescriptorBinaryForm. DataBackupTests check protected files.
- When a release changes how a store writes its file, raise DataFormat.Current and add the migration.
- schtasks /Create without /XML makes tasks that start only on AC power (they sit "Queued" on battery).
- System.Text.Json rejects a UTF-8 BOM in raw bytes; read hand-editable JSON through Utf8Json.ReadFile.
  PowerShell 5's Set-Content -Encoding utf8 writes one.
- All endpoints except pairing require a paired client certificate (PairedDeviceAuthenticationHandler).
  Kestrel accepts any client cert in the handshake; the fingerprint check is in the handler.
- Pairing secrets (PIN, w, x, y, K) must never be logged or persisted. Change the protocol only via docs/pairing.md,
  regenerate vectors with HH_WRITE_SPAKE2_VECTORS=1, and confirm the Rust tests still pass.
- Files under %ProgramData%\HyperHarbor that grant access (certificate, paired devices) go through ProtectedFile.
- Other LAN devices need an inbound firewall rule for TCP 48443. The packaged host's start script creates it;
  a dev run from source does not.
- Tray windows (WebView2, 2026-10-05):
  - WebWindow.PlaceOn sizes the window for its monitor's DPI (GetDpiForMonitor) within the work area (WindowFit)
    with AutoScaleMode None; WinForms' own DPI scaling would scale it twice. The page scales itself.
  - The page must send "ready" right after its first render, not from requestAnimationFrame: the window is hidden
    until then, and a hidden WebView2 runs no animation frames (the first build never showed its window).
  - Folder names differ from C# folders only by case at your peril: Windows paths ignore case, so a "ui" npm folder
    and a "Ui" C# folder are the same folder (hence webui/ and Window/).
  - The WebView2 package also references its WPF control, which breaks WinForms builds (MSB3277 on WindowsBase);
    Directory.Build.targets drops that reference for every project. Host.Service sets
    PublishReferencesDocumentationFiles=false, or WebView2's XML docs land beside the single executable.
  - Messages to the page use ContractJson.Options (enums as camelCase strings); nullable TrayViewState fields need
    [JsonIgnore(Condition = Never)] because those options leave nulls out, and the page checks === null.
  - The CSP is strict (default-src 'none'; scripts and styles from 'self' only): no inline scripts or style tags.
    Svelte's style: directives and transitions set styles through the CSSOM, which the CSP allows.
  - UI automation: InvokePattern on WebView2 elements works once the accessibility tree exists; retry the first
    FindFirst for a few seconds.
- mDNS on the host goes through DnsServiceRegister (dnsapi.dll). Do not bind UDP 5353 in the host.
- Hyper-V (verified on a live host):
  - GetSummaryInformation fills only the requested fields; always request Name (code 0).
  - Saving reports EnabledState 1 with OtherEnabledState "Saving". Starting reports OperationalStatus 11 with RequestedState 2.
  - A guest shutdown shows no "stopping" state, and a guest reboot does not reset uptime.
  - The shutdown component reports OperationalStatus 12 (No Contact) when no guest OS is running; the API returns 409.
  - ModifySystemSettings ignores an empty Notes array; one empty string clears the notes.
  - Each new device is cloned from the single "Microsoft:Definition\<guid>\Default" instance of its class and
    ResourceSubType. A drive's boot entry is its InstanceID plus "\B" in BootSourceOrder (a string array).
  - Secure Boot template IDs are not queryable through CIM: MicrosoftWindows is 1734c6e8-3154-4dda-ba5f-a874cc483422,
    MicrosoftUEFICertificateAuthority is 272e7447-90a4-4563-a4b9-8e4ab00526ce (read back in the live test).
  - vTPM: MSFT_HgsGuardian "UntrustedGuardian" (NewByGenerateCertificates if missing), then
    MSFT_HgsKeyProtector.NewByGuardians, Msvm_SecurityService.SetKeyProtector, and TpmEnabled.
  - The Default Switch's Name is C08CB7B8-9B3C-408E-8E30-5E16A3AEB444. This host's default VHD folder is
    C:\ProgramData\Microsoft\Windows\Virtual Hard Disks; HyperHarbor-Linux lives in Public Documents.
  - A running VM holds its disk files open, so a delete-access check only means something once it is off.
- ASP.NET Core 8 logs every handled exception; that category is off in appsettings.json and ApiExceptionHandler logs instead.
- Svelte: run the svelte-autofixer MCP tool on every .svelte file you change.
- Remote Desktop (Phase 7):
  - PowerShell Direct needs a running Windows guest; Linux guests fail fast with GuestUnavailable (503).
    Provision and connect take tens of seconds (Linux package installs take minutes), so the client uses
    25 min and 90 s request timeouts.
  - The client only retries another host address on a connection error, never after a timeout, so a
    provision or rotation is not repeated.
  - TERMSRV credentials the client writes are Generic, session-scoped, and tagged "HyperHarbor temporary
    credential"; startup cleanup removes only tagged entries (users may have their own TERMSRV entries).
  - Secrets: ProvisionVmRequest, VmConnection, GuestCredential, RotatedPassword, and the Rust VmConnection
    override ToString or Debug to hide passwords. Keep it that way for any new type that holds one.
  - Guest error text reaches logs and problem details. ProvisioningService and PasswordRotator pass it
    through GuestErrors.Sanitize (secrets replaced, control characters removed, length capped).
  - The client validates VmConnection (IP address, plain account name) before writing the .rdp file.
  - The host sends the bare account name (hh-owner), not .\hh-owner: mstsc adds ".\" itself, and a doubled
    ".\.\hh-owner" fails as an unknown user (guest event 4625, substatus 0xc0000064) and prompts for a password.
  - Add-LocalGroupMember takes the group by -SID 'S-1-5-32-555' and the member as the LocalUser object
    (-Member $user); passing $user.SID as the member fails to bind.
- Linux guests (SshAccountManager, SSH.NET):
  - Guest OS comes from KVP (Msvm_KvpExchangeComponent.GuestIntrinsicExchangeItems). Ubuntu reports
    OSName "Ubuntu", OSMajorVersion "24.04", OSPlatformId 129; Windows reports OSPlatformId 2.
  - The admin account needs SSH password authentication and sudo. Sudo is checked first with
    `sudo -S -k true`; the script then reads the account password from the stdin line starting "HH:".
  - The bash script lives in a C# raw string; it is sent with LF line endings (ReplaceLineEndings).
    Check syntax in the guest with `bash -n` after editing it.
  - The SSH host key is pinned at setup (ProvisionedAccount.SshHostKey). Setting up again keeps the pin;
    a changed key is a 409 unless the request sets trustNewHostKey (the dialog offers it for set-up VMs).
  - xrdp has no NLA, so Linux .rdp files set enablecredsspsupport:i:0 and mstsc sends the stored
    credential in the TLS logon packet. Remote Desktop needs a desktop session; setup can install Xfce.
- VM console (Phase 9; verified on this host 2026-10-04 with mstsc against TestWindows):
  - VMMS on TCP 2179 reads an RDP_PRECONNECTION_PDU_V2 whose blob is the VM GUID, then starts TLS directly
    (no X.224 negotiation). A bare GUID or "<GUID>;EnhancedMode=0" gives a basic session; ";EnhancedMode=1"
    takes a different handshake; an unknown GUID or a malformed blob gets a reset.
  - The .rdp file needs pcb:s:<VM GUID> and negotiate security layer:i:0. Without the latter mstsc hangs at
    "Configuring remote session". The certificate is VMMS's self-signed CN=<host name>.
  - A standard local user (no Hyper-V Administrators membership) can open the console after
    Msvm_TerminalService.GrantInteractiveSessionAccess (Trustees "<HOST>\name"); the grant works unelevated
    from a Hyper-V Administrators member and returns 0 synchronously. RevokeInteractiveSessionAccess undoes it.
  - mstsc works through a loopback relay (127.0.0.1:<random> to 2179) with a Generic TERMSRV/127.0.0.1
    credential "<HOST>\name", so the client tunnel needs nothing else.
  - NetUserChangePassword(old, new) works unelevated, but only with the computer name as the domain; null
    gives 2221 (NERR_UserNotFound) for a Microsoft-account caller. Rotating mid-session keeps the session.
  - Logging on creates a host profile (C:\Users\<name>); removing the account does not remove it.
  - mstsc shows an "unknown publisher" prompt for the unsigned .rdp file the first time.
  - Implementation (9.1): the console account hhc-<user> is created by the elevated helper
    `HyperHarbor.Host.exe --setup-console [data dir] [result file]` (tray: Set up console access; the installer
    and Start-HyperHarbor.ps1 run it when console-accounts.json.protected is missing) and removed with
    --remove-console. It denies interactive, Remote Desktop, batch, and service logon; network logon stays.
  - TestServer cannot upgrade connections (IsUpgradableRequest is always false), so the API tests stop at 426
    and ConsoleTunnelTests run the tunnel handler on real Kestrel with an echo server for VMMS.
  - reqwest's request timeout does not end an upgraded stream (console_tunnel_outlives_the_request_timeout,
    ignored because it is slow).
  - Concurrent consoles share the TERMSRV/127.0.0.1 credential until each session opens.
- Unattended installs (Phase 9.2 to 9.4):
  - Host.Core/Unattend: profiles (three read-only built-ins plus each User's own), AutounattendBuilder,
    CloudInitBuilder (SHA-512 crypt via Sha512Crypt), SeedIso, IsoInspector, UnattendedSetup (create job
    steps), UnattendedInstallStore, UnattendedInstallWatcher (run by InstallWatcherService every
    Install:PollSeconds).
  - Microsoft's Windows ISOs are UDF only (their ISO 9660 part holds a README), so IsoInspector tries
    DiscUtils.Udf first. Editions come from the XML resource in sources\install.wim or install.esd.
  - DiscUtils pads the Joliet volume label with ASCII spaces (U+2020 in UCS-2); SeedIso rewrites it, because
    cloud-init finds the seed only by the label CIDATA. Extensionless Joliet names read back from DiscUtils
    with a trailing period (user-data.); Linux strips it. Not yet verified with a real Ubuntu install.
  - Linux installers need the Microsoft UEFI CA Secure Boot template (also for console installs). Ubuntu asks
    "Continue with autoinstall?" at the console, since the seed cannot change the kernel command line.
  - The create job presses Space (Msvm_Keyboard.TypeKey) twelve times on the first boot only, to pass
    Windows media's "Press any key to boot from CD or DVD".
  - The watcher counts only time the VM runs toward Install:TimeoutMinutes, waits for the guest's OS and
    address in data exchange, then for a real RDP Connection Confirm (Windows) or SSH identification line
    (Linux), and marks the VM provisioned only after Remote Desktop answers again after setup. It then
    rotates the one-time administrator password and ejects and deletes the seed. Failed setups back off
    1, 2, 4, 8 minutes, then fail. A Linux profile without a desktop ends after SSH (no Remote Desktop).
  - Verified end to end on 2026-10-04 with a Windows 11 Pro install (TESTWINDOWSINSTALL), including Connect.
    Not yet with a real Ubuntu install.
- Performance mode (Phase 10; Host.Core/Performance):
  - Only Intel GPU-P was available here (UHD iGPU, VEN_8086, 32 partitions). NVIDIA and AMD rules are unit
    tested with recorded file lists only. Verified live on 2026-10-04 (PerformanceLiveTests on HyperHarbor-Test,
    never started): apply with a storage move, export, read-back, and remove. Guest setup and the shutdown
    guard have not run live yet.
  - The GPU partition's HostResource is the WMI object path of the Msvm_PartitionableGpu
    (GpuIdentity.PartitionableGpuPath), not its Name; the Name fails with only "failed to add device 'GPU
    Partition'". MigrateVirtualSystemToHost for a storage move needs DestinationHost unset; the machine name or
    "localhost" returns 32773. Get-VMHardDiskDrive can show a stale path right after a move.
  - Msvm_GpuPartitionSettingData: ResourceType 32770, ResourceSubType "Microsoft:Hyper-V:GPU Partition".
    Msvm_PartitionableGpu reports relative units: VRAM, decode, and compute max 1e9, encode max UInt64.MaxValue,
    so shares are computed in decimal. VSSD: GuestControlledCacheTypes, LowMmioGapSize and HighMmioGapSize
    (MB), AutomaticShutdownAction 2 (TurnOff), UserSnapshotType 2 (Disabled). Removing restores MMIO 128/512
    and the Save stop action. Storage move: MigrateVirtualSystemToHost with MigrationType 32769.
  - Driver discovery: Win32_VideoController.InstalledDisplayDrivers gives the DriverStore folder.
    Win32_PNPSignedDriverCIMDataFile must be enumerated in full and filtered by the Antecedent DeviceID;
    association queries fail with "Invalid parameter". FileRepository folders go to the guest's
    System32\HostDriverStore\FileRepository; System32 and SysWOW64 files go to the same place, staged and
    replaced at reboot (MoveFileEx) when in use (rebootRequired). NVIDIA adds nv*.dll and
    drivers\NVIDIA Corporation.
  - RdpPerformancePolicy holds every registry write with its TerminalServer.admx policy; RdpPerformancePolicyTests
    check them against the local ADMX. DWMFRAMEINTERVAL (15) is from KB 2885213, not an ADMX. Hardware H.264
    encoding (AVCHardwareEncodePreferred) is experimental and off by default.
  - Each PowerShell Direct feature has its own script through PowerShellDirectRunner, to stay under the 32 KB
    command line. Guest setup needs HyperHarbor's administrator credential (409 credentialRequired).
  - Drift: the guest record keeps the copied driver version; GET performance compares it with the host's.
  - Disk export (VmDiskExportService): off VMs only, copies the whole checkpoint chain, never ISOs, removes an
    incomplete export. The default folder is the tray's backup folder (BackupFolder in host-settings.json,
    default Public Documents\HyperHarbor Backups).
  - Pre-shutdown: GpuVmShutdownCoordinator shuts guests down and never turns a VM off. As a Windows service,
    PreshutdownServiceLifetime sets ServiceBase._acceptedCommands (PreshutdownServiceLifetimeTests fail if
    .NET renames it); not yet tried through a real Windows shutdown. In console mode the tray's ShutdownGuard (a
    hidden top-level window) refuses WM_QUERYENDSESSION with a block reason while GPU VMs run, except on
    sign-out; it stands aside while the installed service runs. Not yet tried by hand.
  - GpuEventReader reads the System log (nvlddmkm, amdkmdag, amdwddmg, igfx*, and Display 4101) for the last
    7 days, cached 5 minutes, into HostGpu.warnings.
- Lean host (2026-10-07):
  - Verified here, unelevated: the Inspect script (services, startup entries, power plans and wake devices, programs,
    printers, registry reads), the registry and startup writes on throwaway HKCU keys (round trip through Inspect),
    the Programs script's exit code handling and its run-as-user scheduled task. NOT yet run, because they need an
    elevated shell or change the PC: restore point (Checkpoint-Computer as SYSTEM), Appx removal, service changes
    with sc.exe, powercfg plan creation (`/duplicatescheme <guid> <guid>` into the same GUID, so the plan stays
    idempotent) and wake changes, the Default user hive load, HKLM writes, a real uninstall, the monthly tick in the
    service. HH_HOSTLEAN_LIVE=1 (elevated) prints a read-only dry run of this PC.
  - Never run a command line a non-administrator could have written as SYSTEM: HKCU Uninstall keys are user-writable,
    so a per-user program is uninstalled by a scheduled task that runs as that user (HostScripts.Programs).
  - PowerShell 5.1 gotchas found by running the scripts: `@($request.missing)` is a one-element array holding $null
    (filter with Where-Object { $null -ne $_ }); a function returning one item gives a scalar, not an array (wrap the
    call in @()); Get-ChildItem on HKEY_USERS fails without admin (use Registry.Users.GetSubKeyNames()); `2>&1` in a
    pipeline makes ErrorRecords (stringify with ForEach-Object { "$_" }).
  - The dry run fingerprint is the hash of the change lines; an apply recomputes it and refuses on a mismatch, then
    stores the new dry run. Startup entries and services decide "in place" by startup type only. Wake: with
    `wake: nicOnly` or `nicAndInput` (NICs plus keyboards and mice) and no wired adapter that powercfg can program, nothing is disarmed (Wake-on-LAN would break).
  - Undo covers services, startup entries, registry values, plan, and disarmed wake devices (the NIC armed by the
    apply is not disarmed again). Registry "before" comes from the first hive (a signed-in user's, else Default).
    Removed apps and uninstalled programs are not restored. The undo keeps the earliest value across applies.
  - Not covered: install on host profiles (winget as SYSTEM fails, see Applying setup profiles), unloaded user
    profiles (only signed-in hives and the Default user hive change), restart advice after uninstalls.
  - The guard for Gaming Services and the Xbox Identity Provider is keyed on Steam appids (catalogs/host-guards.yaml
    games); the list is curated, not exhaustive, and the appids were written from memory: check them when editing.
- Host log download (API 1.16.0, added at the user's request 2026-10-07): POST /host/logs/bundle (elevated, audited) returns
  a zip from Host.Core/Diagnostics/LogBundle (logs\*.log newest first, 100 MB cap, summary.txt; not the audit log). It is a
  POST because EndpointSecurityTests require elevated routes to be audited and forbid auditing GETs. The Rust client
  (download_host_logs) saves it through a file dialog; Host menu > Download host logs…, gated on hostSupports 1.16.0.
- Host Remote Desktop (maintenance sessions on the host itself; API 1.10.0, added at the user's request 2026-10-05):
  - GET /host/remote-desktop reads fDenyTSConnections, RDP-Tcp PortNumber, EditionID (Home editions start with
    "Core" and are unsupported), and the firewall group "@FirewallAPI.dll,-28752" through HNetCfg.FwPolicy2.
  - POST /host/remote-desktop/enable (elevated, audited) calls Win32_TerminalServiceSetting.SetAllowTSConnections(1, 1)
    in root\cimv2\TerminalServices, which also opens the firewall group; NLA is left alone. An unelevated host
    answers 409 requiresInstalledService.
  - The client's connect_host writes an .rdp file with no user name and prompt for credentials:i:1; mstsc asks
    for the host's Windows account. No host credential is stored or sent (the user's decision).
  - Host controls other than + New VM live in the header's Host menu (components/Menu.svelte, shared with the
    VM row's menu). Menu closes the <details> element directly: jsdom fires no toggle event, so bind:open fails in tests.
- Mixed versions: every 404 the host sends on purpose has a `detail`; a bare 404 (status code pages) means an older
  host without the route, and the client's api.rs check() turns it into problem code hostOutdated with an
  update hint. Gate UI for features added in a newer API version with hostSupports(host, "x.y.z") in client.ts
  (mDNS hosts report apiVersion; unknown versions pass and rely on hostOutdated).
- Setup profiles (Phase 12a):
  - Formats: profiles are <data>\profiles\<user id>\<id>.yaml (ids from the name; catalog, import, and schema are
    reserved route words). ProfileYamlWriter writes each id with its friendly name as a trailing comment and the
    $schema modeline; ProfileYamlReader reads the comments back (YamlDotNet Scanner with skipComments: false), so files
    round-trip. The schema is authored by hand; TheSchema_AndTheModel_HaveTheSameProperties keeps it equal to the DTOs.
    Appx baselines: <data>\appx-baselines.json, keyed "10.0.<CurrentBuild>/<EditionID>", recorded by the install
    watcher after an unattended Windows install (only if missing) or by POST /vms/{id}/appx-baseline.
  - Package search: Microsoft.WinGet.Client refuses Windows PowerShell 5.1 as SYSTEM ("not supported in Windows
    PowerShell") but works in PowerShell 7 (verified 2026-10-05: pwsh 7.6.6 as SYSTEM, about 2 s). winget installs
    PowerShell 7.6 as a per-user MSIX by default, which SYSTEM cannot run; the setup helper (--setup-package-search,
    tray "Set up package search") uses --installer-type wix --scope machine. Without it, search answers 409
    wingetUnavailable.
  - Extensions: the Chrome Web Store no longer lists uBlock Origin or ClearURLs (Manifest V2); uBlock Origin is an
    Edge-only catalog entry (store: edge). HH_EXTENSION_LIVE=1 checks every catalog id against its store; run it when
    editing catalogs/extensions.yaml. Browsers install some extensions themselves (browsers.yaml builtInExtensions);
    capture lists them unticked.
  - Capture runs three PowerShell Direct reads as the VM's stored administrator (about 18 s on TESTWINDOWSINSTALL).
    HKCU values come from the account's loaded hive or NTUSER.DAT loaded under HKU\HyperHarborCapture; read it with
    .NET keys closed at once and retry the unload, because a hive left loaded gives the account a temporary profile at
    its next sign-in (the first live run failed on exactly that unload). reg.exe writes errors to stderr, which ends a
    script under ErrorActionPreference Stop; switch to Continue around native commands whose exit code is checked.
  - When winget export yields nothing, capture falls back to the Uninstall keys' program names with a warning.
  - Scripted edits with Node: in String.replace, the replacement string treats $' $& $` as patterns, which mangled
    a PowerShell regex ending in +$'. Pass a function (s.replace(a, () => b)) when the text can contain $.
- Applying setup profiles (Phase 12b; spike and live runs on HyperHarbor-Test, Windows 11 Pro 24H2 media, 2026-10-05):
  - winget.exe from the App Installer folder fails with "Access is denied" in a PowerShell Direct session as an
    administrator that never signed in interactively. Running it as SYSTEM from a scheduled task fails with
    0xC0000135 (DLL not found). What works: Add-AppxPackage -DisableDevelopmentMode -Register <App Installer
    folder>\AppxManifest.xml for the account, then winget in the session (the packages and capture scripts both do it).
  - The fresh image's winget (v1.11) then failed installs with 0x8A15000F "Data required by the source is missing";
    winget source reset --force plus source update fixed it there. If not, the packages script adds
    https://cdn.winget.microsoft.com/cache/source.msix. Codes treated as done: 0, 0x8A15002B, 0x8A150061; 0x8A150109
    means done after a restart; 0x8A150010 (no machine-scope installer) retries without --scope machine.
  - Invoke-Command -ArgumentList @($array) with one argument unrolls the array into separate arguments, so the
    script block's single parameter got only the first package and the first registry write. Wrap it:
    -ArgumentList (,@($array)). With several arguments, pass @($array) as is: (,@($array)), $other wraps it once too
    often, and the script saw one item whose name was the whole list.
  - Remove-WindowsCapability for MathRecognizer reported no restart. HKCU tweaks go to the Default user hive
    (C:\Users\Default\NTUSER.DAT under HKU\HyperHarborDefault), since the User's account has not signed in yet.
    ExtensionSettings is one REG_SZ JSON value under the browser's policy key.
  - After a restart, GuestRestart counts Remote Desktop only after one check found it down (Windows answers for a few
    seconds after the request, and a guest reboot does not reset uptime): 60 checks of 15 seconds.
  - The User's account hive: loaded under HKU\<SID> while it is signed in, otherwise its NTUSER.DAT loaded under
    HKU\HyperHarborUser. An account that never signed in has no profile, so only the Default hive changes. The live
    test gives the account a profile with userenv CreateProfile, which needs no sign-in.
- UI automation of the client: WebView2 inputs ignore SendKeys when the window is not foreground; set
  values with UI Automation ValuePattern instead.
- Audit and elevation (Phase 8):
  - Every non-GET route needs .Audited(), or .Audited<TRequest>(describe) to summarize the request; the
    summary must never contain a secret. A "requested" entry is written before the handler and the request
    fails with 503 if it cannot be. EndpointSecurityTests enforce this, and SecretLeakTests read audit.log.
  - Elevated routes call .RequireElevation() after .Audited() and list the `elevation` security scheme in
    api.yaml. Elevation that depends on the body (performVmAction with turnOff) uses a predicate, is
    described in the operation, and is listed in ConditionallyElevatedEndpoints_AreTheDocumentedOnes.
  - The admin passphrase is set only from the tray. The tray hashes it (PBKDF2-SHA256, 600,000 iterations)
    and sends only salt and hash over the pipe, so a process squatting the pipe never sees the passphrase.
    Tokens are random, kept as SHA-256 in memory, bound to device and User, and end on passphrase change,
    unpair, or restart. ElevateRequest, ElevationGrant, and SetAdminPassphraseMessage hide secrets in ToString.
  - Tests set the passphrase with AdminPassphrase.CreateHash(..., ElevationServiceTests.TestIterations).
  - Wake-on-LAN fixes need elevation too (API 1.8.0, the user's decision on 2026-10-04): the installed
    service runs as LocalSystem and applies them directly, with no tray approval. An unelevated console
    host still asks the tray as well. TestHost pins the unelevated WakeFixCoordinator path.
- VM lifecycle (Phase 8):
  - Host.Core/Lifecycle: VmDeletionService, VmCreationService, VmComputeService, VmJobStore (in-memory jobs,
    each holding its VM's VmOperationLocks lock), IsoLibrary. CIM goes through HyperVCim (waits on
    Msvm_ConcreteJob), CimXml (embedded instances), and CimVmSettings; new devices are cloned from the
    "...\Default" template instances. Interfaces (IHyperVStorage, IHyperVBuilder, IHyperVCompute, IDiskFiles,
    IHyperVHost, IHostCapacityReader) have fakes in Host.Tests/Lifecycle/LifecycleFakes.cs.
  - Delete follows checkpoint .avhdx parents to the base disk and never deletes a parent of a differencing
    disk; anything another VM, a checkpoint, or a stray differencing disk depends on blocks it. Only
    ResourceSubType "Virtual Hard Disk" attachments are disks: ISOs are ResourceType 31 too.
  - ISO library: one flat folder of .iso files managed from clients (upload, rename, delete; all elevated).
    Uploads stream to a hidden .partial file and are moved into place only when complete; images a VM
    attaches (usedBy) cannot be renamed or deleted. The folder is a host setting: the tray's HyperHarbor Host
    window sends SetIsoFolderMessage, saved in host-settings.json (HostSettingsStore), which overrides
    Lifecycle:IsoFolder. Images in an old folder are not moved.
  - VM storage works the same way: the host window sends SetVmFolderMessage, saved as VmFolder in
    host-settings.json, which overrides Lifecycle:VmRootFolder. VmStorageLocation decides the folders (root\name
    with a "Virtual Hard Disks" subfolder, or Hyper-V defaults when nothing is chosen). Existing VMs never move.
  - Client uploads: the Rust side shows the file picker (tauri-plugin-dialog) and keeps the path; the webview
    gets a pick ID and progress events (iso-upload-progress). The client checks elevation before sending,
    because the host refuses an unelevated upload before reading its body.
  - Validation errors are 400 with `errors`; resource warnings are 409 code resourceWarnings with `warnings`,
    sent again with acknowledgeWarnings. Settings that need the VM off return 409 code requiresShutdown.
  - Configuration: Lifecycle:VmRootFolder (unset: Hyper-V's default folders), Lifecycle:IsoFolder (unset:
    Public Documents\HyperHarbor ISOs), Lifecycle:HostMemoryReserveMb (4096), Lifecycle:ShutdownTimeoutSeconds
    (300), Elevation:TokenLifetimeSeconds (300).
  - Verified live on 2026-10-03: CIM reads (CimHyperVStorageLiveTests), and create, developer preset, a
    checkpoint, and delete with merging (LifecycleLiveTests). Not yet run live: shut down and apply on a running VM.
  - Client: the elevation token stays in the Rust ApiClient (never in the webview); ClientError carries the
    host's problemCode and issues. withElevation (lifecycle.svelte.ts) prompts once and retries.
  - Vitest: write `beforeEach(() => { invoke.mockReset(); })` with braces. mockReset returns the mock, and a
    function returned from beforeEach runs as a teardown, calling invoke() with no arguments.
  - Number inputs for memory use step="any": a step mismatch (0.75 GB with step 0.5) silently blocks submit.

## Wake-on-LAN lessons (from real hosts)
- Readiness checks can all pass while waking fails. Seen on TC-PC (2026-10-02): BIOS "PCI-E wake" was
  disabled. Firmware settings are not readable from Windows; the client shows a firmware checklist instead.
- A Hyper-V external switch bound to the wired NIC is a known Wake-on-LAN breaker, not yet a readiness check.
- Group Policy can force "connectivity in standby" off, and NICs missing from powercfg wake_programmable
  cannot be armed; both are reported as not fixable.
- Windows sends 255.255.255.255 out of the lowest-metric interface (often a virtual adapter); the client
  binds each magic packet to the local address on the host's subnet.
- Diagnose-Wake.ps1 (host/src/Host.Service/Wake, embedded; HyperHarbor.Host.exe save-wake-diagnostics [folder]
  writes it out) collects what the checks cannot see; -Listen proves packet delivery.
- After a Wake-on-LAN wake nobody is at the PC, so Windows' "System unattended sleep timeout" (UNATTENDSLEEP,
  about 2 minutes) puts it back to sleep. Host.Core/Power: KeepAwakeController (ticked by KeepAwakeService)
  holds a PowerRequestSystemRequired request (WindowsPowerRequest) while a paired device made a request in the
  last KeepAwake:ClientWindowMinutes (10; RemoteUseMiddleware after UseAuthorization marks RemoteUseTracker,
  reads included), while HostActivity.InProgress (requests, console tunnels, jobs, pairing), or while a Remote
  Desktop session is signed in to the host (WindowsRemoteSessions), and releases it otherwise. Running VMs do not
  count (the user's decision): a Remote Desktop session to a VM never passes through the host, so the client
  checks in with GET /host every minute (sessions.rs) while mstsc windows it launched stay open, as long as the
  client app runs. Check with `powercfg /requests` (elevated): "HyperHarbor: ..." under SYSTEM.

## Open issues (not yet scheduled)
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

## Live testing
- Live tests skip themselves when Hyper-V is unreachable ([HyperVFact]); the account must be in Hyper-V Administrators.
  [EnvironmentFact("VAR", ...)] skips unless the variables are set; [LocalHardwareFact] skips when CI is set.
- TestHost (in-memory API) captures logs at every level (Logs.AssertNoneContain) and uses a unique
  Tray:PipeName. RealTlsTests starts the service exe on loopback via Api:ListenAddress.
- Test VM HyperHarbor-Linux: Ubuntu 24.04 server (no desktop), user hhadmin, SSH key
  ~/.ssh/hyperharbor_linux_ed25519; sudo needs hhadmin's password. Dynamic memory 768 MB startup, because
  this PC often has little free RAM.
- For throwaway VMs, create HyperHarbor-Test and delete it afterwards (LifecycleLiveTests does both, with a
  1 GB disk in a temporary folder). Ask before changing any other VM.
- Setup profiles: HH_PROFILE_LIVE=1 with HH_LIVE_GUEST_VM=<VM id> runs a read-only capture against a running Windows VM
  with a stored credential (reads the host's data folder, so run elevated; HH_PROFILE_LIVE_OUT=<file> saves the draft).
  HH_PACKAGE_SEARCH_LIVE=1 searches winget through pwsh; HH_EXTENSION_LIVE=1 checks catalog ids against the stores.
- Live host harness (Host.Tests/Live/LiveHostHarness): the real host exe from the test build on a loopback port with
  its own data folder (%LOCALAPPDATA%\HyperHarborHarness or HH_HARNESS_DATA), a seeded pairing, and a test passphrase,
  so live tests drive the API without the user and without touching the installed host. HH_HARNESS_ISO_FOLDER and
  HH_HARNESS_VM_FOLDER point it at images and VM storage (this PC: D:\ISOs, D:\VMs). It runs from Host.Tests\bin, so
  do not rebuild Host.Tests while one runs (build to another folder with -o; ExtensionResolverTests then fail because
  they look for catalogs/ above the output folder).
  - HH_SPIKE_LIVE=1 HH_SPIKE_ISO=<image>: creates HyperHarbor-Test with an unattended install if missing (about 20
    minutes) and runs the 12b spike checks. HH_PROFILE_APPLY_LIVE=1 HH_LIVE_GUEST_VM=<id>: applies a small profile
    with the real applier and reads it back (about 30 s). HH_PROFILE_E2E_LIVE=1 HH_SPIKE_ISO=<image>: creates
    HyperHarbor-Test2 with a setup profile and waits for Ready (about 25 minutes). HH_PROFILE_EXISTING_LIVE=1
    HH_SPIKE_ISO=<image>: creates HyperHarbor-Test, applies a profile through the API with a restart, checks the
    account's hive, and deletes the VM. Throwaway VMs created for tests may be deleted without asking (the user's
    decision, 2026-10-05); ask before changing any other VM.
- VM console automation: Msvm_Keyboard.TypeText can drop characters, so send TypeKey one key at a time.
  Read the screen with GetVirtualSystemThumbnailImage (RGB565) and confirm a prompt is gone before moving on.
