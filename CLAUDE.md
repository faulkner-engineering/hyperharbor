# HyperHarbor

Host/client app that manages Hyper-V VMs on a home PC and connects to them in one click from any device. Think "Moonlight for Hyper-V."

## Architecture
- host/ (.NET 8): Worker Service + Kestrel API (mTLS), WinForms tray app, Core library
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

v2 (paid tier, not in MVP): per-user accounts with roles and SSO mapping. Also out of MVP scope:
per-device VM accounts and a user management UI.

## Rules
- MVP scope only. Do not implement later phases early.
- Professional code and comments. No slang. No em dashes.
- Small commits with clear messages. Run tests before committing.
- Never store VM passwords in plaintext. Never bind the API to 0.0.0.0 without mTLS.
- Rotated VM passwords live in host memory only for the reuse window and are never logged or persisted.
  VM admin credentials for provisioning are stored only with DPAPI through ProtectedFile.
- Ask before any destructive Hyper-V operation.
- After any change, without asking (the user's standing instruction): stop everything running from dist/
  (HyperHarbor.Host.Service, HyperHarbor.Host.Tray, the portable client), rebuild with
  scripts\package.ps1 -Fast, then relaunch: dist\host\Start-HyperHarbor.ps1 (service and tray) and
  dist\HyperHarbor-Client-<ver>-portable.exe. Report it if the rebuild fails, and leave things stopped.

## Layout
- docs/api.yaml: OpenAPI 3.1 contract, the source of truth for host and client
- host/src/Shared.Contracts: DTOs mirroring api.yaml; ContractJson holds the wire JSON options
- host/src/Host.Core: HyperV/ (CIM reader, VmMapper), Power/ (actions), Discovery/ (DNS-SD), Identity/,
  Pairing/ (Spake2, PairingService), Security/ (host certificate, paired devices, ProtectedFile),
  Audit/ (FileAuditLog), Elevation/ (AdminPassphraseStore, ElevationService), Lifecycle/ (create, delete,
  compute, jobs, locks, ISO library), HyperV/HyperVCim, CimXml, CimVmSettings (shared CIM helpers)
- host/src/Host.Service: Kestrel API (Api/, including AuthEndpoints and JobEndpoints), device auth and
  elevation filter (Security/), audit filter and job audit (Audit/), tray pipe server (Tray/), mDNS (Discovery/)
- host/src/Host.Tray: WinForms tray. HostForm (double-click the icon) shows service status, the admin passphrase
  (set or change), paired devices, and opens the logs; PinForm, DevicesForm, AdminPassphraseForm
- host/tests/Host.Tests: xUnit; Api tests use TestHost (WebApplicationFactory, fakes, client cert via header)
- client/src-tauri/src: hosts.rs, discovery.rs (mdns-sd), api.rs (reqwest), spake2.rs, tls.rs (pinning),
  identity.rs (key in Credential Manager), paired.rs; client/src: SvelteKit SPA. Lifecycle UI:
  lib/lifecycle.svelte.ts (elevation prompt, job polling) and lib/components/*Dialog.svelte on a shared Dialog
- docs/pairing.md: the SPAKE2 pairing protocol; both implementations must match it and Spake2Vectors.json

## Commands
Toolchains are not on Git Bash PATH. Prefix: export PATH="/c/Program Files/dotnet:/c/Program Files/nodejs:$HOME/.cargo/bin:$PATH"
- Everything CI runs (build, tests, lint, type drift, audits): powershell -ExecutionPolicy Bypass -File scripts\test-all.ps1
  [-HostOnly|-ClientOnly] [-Coverage] [-SkipAudit]. CI: .github/workflows/ci.yml (windows-latest).
- Host build/test: dotnet build HyperHarbor.sln -warnaserror && dotnet test HyperHarbor.sln
- Run host API (https://*:48443, mTLS): dotnet run --project host/src/Host.Service; pairing needs Host.Tray running
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

## Packaging (multi-machine testing)
- powershell -ExecutionPolicy Bypass -File scripts\package.ps1 [-Fast] [-SkipTests] [-HostOnly|-ClientOnly]
- Output in dist/ (git-ignored): HyperHarbor-Host-<ver>-portable.zip, client NSIS setup exe, portable client exe.
- -Fast uses thin LTO for test builds (about 3 min total vs about 10). Switching between fast and full recompiles once.
- Version comes from Directory.Build.props (host) and client/src-tauri/tauri.conf.json (client); keep them equal.
- The script refuses to run while anything is running from dist/ (Windows locks the exe).
- The host zip's Start-HyperHarbor.ps1 does one elevated setup (Private-profile firewall rule for TCP 48443,
  Hyper-V Administrators membership), then starts the service console and tray. Builds are unsigned.
- Verified on 2026-10-02: packaged host plus client paired and listed VMs across machines.

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
- Do not run cargo fetch; it downloads every target platform's dependencies (Android, iOS, macOS).
- All endpoints except pairing require a paired client certificate (PairedDeviceAuthenticationHandler).
  Kestrel accepts any client cert in the handshake; the fingerprint check is in the handler.
- Pairing secrets (PIN, w, x, y, K) must never be logged or persisted. Change the protocol only via docs/pairing.md,
  regenerate vectors with HH_WRITE_SPAKE2_VECTORS=1, and confirm the Rust tests still pass.
- Files under %ProgramData%\HyperHarbor that grant access (certificate, paired devices) go through ProtectedFile.
- Other LAN devices need an inbound firewall rule for TCP 48443. The packaged host's start script creates it;
  a dev run from source does not.
- Tray (WinForms) windows must set AutoScaleDimensions = 96x96 with AutoScaleMode.Dpi and size from content;
  the tray runs PerMonitorV2. Fixed pixel layouts were unreadable at higher display scaling.
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
- packaging/host/Diagnose-Wake.ps1 collects what the checks cannot see; -Listen proves packet delivery.

## Open issues (not yet scheduled)
- Tray pipe squatting: a local process started before the service could claim HyperHarbor.Host.Tray. The tray
  should verify the pipe server process. (The pipe ACL now admits only SYSTEM, Administrators, and the
  service account; TrayPipeServerTests check it.)
- Anyone on the LAN can repeatedly start pairing requests (PIN window spam). Consider rate limiting.
- No real installer yet: the service runs as a console app, not a Windows service. An MSI (service as
  LocalSystem, tray at logon) needs the data-file ACLs and pipe ACL retested under LocalSystem; the pipe
  must then grant the logged-on user explicitly, since the service account is no longer that user.
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
- VM console automation: Msvm_Keyboard.TypeText can drop characters, so send TypeKey one key at a time.
  Read the screen with GetVirtualSystemThumbnailImage (RGB565) and confirm a prompt is gone before moving on.
