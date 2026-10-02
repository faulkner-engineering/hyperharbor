# HyperHarbor

Host/client app that manages Hyper-V VMs on a home PC and connects to them in one click from any device. Think "Moonlight for Hyper-V."

## Architecture
- host/ (.NET 8): Worker Service + Kestrel API (mTLS), WinForms tray app, Core library
  - Hyper-V via CIM/WMI (root\virtualization\v2) and PowerShell SDK (PowerShell Direct)
- client/ (Tauri v2, Rust + TypeScript): discovery, pairing, VM dashboard, RDP launcher
- Discovery: mDNS _hyperharbor._tcp on LAN; manual add by IP/hostname otherwise
- Pairing: PIN shown in tray, certificate exchange and pinning, mTLS afterward
- Credentials: per-device local VM account, password rotated via PowerShell Direct on each connect, pushed to client cred store, removed after connect
- Wake-on-LAN: readiness check with auto-fix, Test Wake, relay mode later

## Roadmap (one phase per session, read-only first)
1. Solution skeleton + OpenAPI contract (done)
2. List VMs and state (read-only) (done)
3. Start/stop + Kestrel API on localhost (done)
4. mDNS discovery + client VM list (done)
5. PIN pairing + mTLS
6. Wake-on-LAN
7. One-click RDP with ephemeral credentials

## Rules
- MVP scope only. Do not implement later phases early.
- Professional code and comments. No slang. No em dashes.
- Small commits with clear messages. Run tests before committing.
- Never store VM passwords in plaintext. Never bind the API to 0.0.0.0 without mTLS.
- Ask before any destructive Hyper-V operation.

## Layout
- docs/api.yaml: OpenAPI 3.1 contract, the source of truth for host and client
- host/src/Shared.Contracts: DTOs mirroring api.yaml; ContractJson holds the wire JSON options
- host/src/Host.Core: HyperV/ (CIM reader, VmMapper), Power/ (actions), Discovery/ (DNS-SD), Identity/
- host/src/Host.Service: Kestrel API (Api/), mDNS advertisement (Discovery/), --list-vms diagnostic
- host/tests/Host.Tests: xUnit; Api tests use WebApplicationFactory with fakes from Fakes.cs
- client/src-tauri/src: hosts.rs (registry), discovery.rs (mdns-sd), api.rs (reqwest); client/src: SvelteKit SPA

## Commands
Toolchains are not on Git Bash PATH. Prefix: export PATH="/c/Program Files/dotnet:/c/Program Files/nodejs:$HOME/.cargo/bin:$PATH"
- Host build/test: dotnet build HyperHarbor.sln -warnaserror && dotnet test HyperHarbor.sln
- Run host API (http://127.0.0.1:48443): dotnet run --project host/src/Host.Service
- Print VM inventory JSON: dotnet run --project host/src/Host.Service -- --list-vms
- Lint contract: npx @redocly/cli lint docs/api.yaml
- Client (from client/): npm run check | npm run gen:api | npm run tauri dev
- Rust (from client/src-tauri, in PowerShell): cargo fmt; cargo clippy --all-targets -- -D warnings; cargo test
- Live mDNS browse (host service running): cargo test live_browse -- --ignored --nocapture

## Contract changes
Edit docs/api.yaml, then update Shared.Contracts (and ContractInfo.ApiVersion if info.version changes),
run npm run gen:api, and lint. OpenApiEnumTests fail if C# enums or the API version drift from api.yaml.
Mark required request properties [JsonRequired] (an empty body must not default to action=start).

## Gotchas
- Build Rust from PowerShell, not Git Bash: Git Bash's /usr/bin/link shadows MSVC link.exe.
- Do not run cargo fetch; it downloads every target platform's dependencies (Android, iOS, macOS).
- The API is loopback-only until Phase 5. The client calls only hosts where isLocal is true; others return pairingRequired.
- mDNS on the host goes through DnsServiceRegister (dnsapi.dll). Do not bind UDP 5353 in the host.
- Hyper-V (verified on a live host):
  - GetSummaryInformation fills only the requested fields; always request Name (code 0).
  - Saving reports EnabledState 1 with OtherEnabledState "Saving". Starting reports OperationalStatus 11 with RequestedState 2.
  - A guest shutdown shows no "stopping" state, and a guest reboot does not reset uptime.
  - The shutdown component reports OperationalStatus 12 (No Contact) when no guest OS is running; the API returns 409.
- ASP.NET Core 8 logs every handled exception; that category is off in appsettings.json and ApiExceptionHandler logs instead.
- Svelte: run the svelte-autofixer MCP tool on every .svelte file you change.

## Live testing
- Live tests skip themselves when Hyper-V is unreachable ([HyperVFact]); the account must be in Hyper-V Administrators.
- Test VM HyperHarbor-Linux: Ubuntu 24.04, user hhadmin, SSH key ~/.ssh/hyperharbor_linux_ed25519.
- For throwaway VMs, create HyperHarbor-Test (Gen 2, no disk) and delete it afterwards. Ask before changing any other VM.
- VM console automation: Msvm_Keyboard.TypeText can drop characters, so send TypeKey one key at a time.
  Read the screen with GetVirtualSystemThumbnailImage (RGB565) and confirm a prompt is gone before moving on.