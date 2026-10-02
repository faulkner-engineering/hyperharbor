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

## Roadmap (one phase per session, read-only first)
1. Solution skeleton + OpenAPI contract (done)
2. List VMs and state (read-only) (done)
3. Start/stop + Kestrel API on localhost (done)
4. mDNS discovery + client VM list (done)
5. PIN pairing + mTLS (done)
6. Wake-on-LAN (done)
7. One-click RDP with a per-User VM account

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

## Layout
- docs/api.yaml: OpenAPI 3.1 contract, the source of truth for host and client
- host/src/Shared.Contracts: DTOs mirroring api.yaml; ContractJson holds the wire JSON options
- host/src/Host.Core: HyperV/ (CIM reader, VmMapper), Power/ (actions), Discovery/ (DNS-SD), Identity/,
  Pairing/ (Spake2, PairingService), Security/ (host certificate, paired devices, ProtectedFile)
- host/src/Host.Service: Kestrel API (Api/), device auth (Security/), tray pipe server (Tray/), mDNS (Discovery/)
- host/src/Host.Tray: WinForms tray; shows pairing PINs and paired devices over the named pipe
- host/tests/Host.Tests: xUnit; Api tests use TestHost (WebApplicationFactory, fakes, client cert via header)
- client/src-tauri/src: hosts.rs, discovery.rs (mdns-sd), api.rs (reqwest), spake2.rs, tls.rs (pinning),
  identity.rs (key in Credential Manager), paired.rs; client/src: SvelteKit SPA
- docs/pairing.md: the SPAKE2 pairing protocol; both implementations must match it and Spake2Vectors.json

## Commands
Toolchains are not on Git Bash PATH. Prefix: export PATH="/c/Program Files/dotnet:/c/Program Files/nodejs:$HOME/.cargo/bin:$PATH"
- Host build/test: dotnet build HyperHarbor.sln -warnaserror && dotnet test HyperHarbor.sln
- Run host API (https://*:48443, mTLS): dotnet run --project host/src/Host.Service; pairing needs Host.Tray running
- Print VM inventory JSON: dotnet run --project host/src/Host.Service -- --list-vms
- Lint contract: npx @redocly/cli lint docs/api.yaml
- Client (from client/): npm run check | npm run gen:api | npm run tauri dev
- Rust (from client/src-tauri, in PowerShell): cargo fmt; cargo clippy --all-targets -- -D warnings; cargo test
- Live mDNS browse (host service running): cargo test live_browse -- --ignored --nocapture
- Live pairing over LAN (service running, something writing the PIN to the file):
  HH_LIVE_HOST=<lan ip> HH_PIN_FILE=pin.txt cargo test live_pairing -- --ignored --nocapture

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
Mark required request properties [JsonRequired] (an empty body must not default to action=start).

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
- ASP.NET Core 8 logs every handled exception; that category is off in appsettings.json and ApiExceptionHandler logs instead.
- Svelte: run the svelte-autofixer MCP tool on every .svelte file you change.
- Remote Desktop (Phase 7):
  - PowerShell Direct needs a running Windows guest; Linux guests fail fast with GuestUnavailable (503).
    Provision and connect take tens of seconds, so the client uses 180 s and 90 s request timeouts.
  - The client only retries another host address on a connection error, never after a timeout, so a
    provision or rotation is not repeated.
  - TERMSRV credentials the client writes are Generic, session-scoped, and tagged "HyperHarbor temporary
    credential"; startup cleanup removes only tagged entries (users may have their own TERMSRV entries).
  - Secrets: ProvisionVmRequest, VmConnection, GuestCredential, RotatedPassword, and the Rust VmConnection
    override ToString or Debug to hide passwords. Keep it that way for any new type that holds one.
- UI automation of the client: WebView2 inputs ignore SendKeys when the window is not foreground; set
  values with UI Automation ValuePattern instead.

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
  should verify the pipe server process.
- Anyone on the LAN can repeatedly start pairing requests (PIN window spam). Consider rate limiting.
- No real installer yet: the service runs as a console app, not a Windows service. An MSI (service as
  LocalSystem, tray at logon) needs the data-file ACLs and pipe ACL retested under LocalSystem.

## Live testing
- Live tests skip themselves when Hyper-V is unreachable ([HyperVFact]); the account must be in Hyper-V Administrators.
- Test VM HyperHarbor-Linux: Ubuntu 24.04, user hhadmin, SSH key ~/.ssh/hyperharbor_linux_ed25519.
- For throwaway VMs, create HyperHarbor-Test (Gen 2, no disk) and delete it afterwards. Ask before changing any other VM.
- VM console automation: Msvm_Keyboard.TypeText can drop characters, so send TypeKey one key at a time.
  Read the screen with GetVirtualSystemThumbnailImage (RGB565) and confirm a prompt is gone before moving on.