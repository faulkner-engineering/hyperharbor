# HyperHarbor

Host/client app that manages Hyper-V VMs on a home PC and connects to them in one click from any device. Think "Moonlight for Hyper-V."
The host (host/, .NET 8) is a Windows service with an mTLS API and a WebView2 tray; the client (client/, Tauri v2, Rust and
SvelteKit) discovers, pairs, manages VMs, and launches Remote Desktop. docs/api.yaml is the contract between them.

## Read before working on an area
- docs/architecture.md: the parts, the repository layout (which file does what), and known limitations.
- docs/platform-notes.md: Windows, Hyper-V, Remote Desktop, camera, and library behavior found by running the code, by area.
  Read the section for the area you are changing; most entries record a failure.
- docs/development.md: commands, packaging, releases, contract changes, and live tests (environment variables and harness).
- docs/history.md: each phase's design decisions and what was verified live. Phase 28 (administrator access in guests) is the latest.
- docs/pairing.md: the SPAKE2 pairing protocol.
- docs/machine-profiles.md: the machine profile document, API, migration, and apply design (phase 26).
- docs/guest-elevation.md: administrator windows in guests (phase 28) and the planned guest agent.
- docs/ui-principles.md: how the client UI must behave so a failure never traps the user. Read before any UI change.

## Rules
- MVP scope only. Do not implement later phases early.
- Professional code and comments. No slang. No em dashes.
- Small commits with clear messages. Run tests before committing.
- Always update CHANGELOG.md with every user-visible change (features, behavior changes, fixes, API version
  changes, anything a user must act on), in the same commit. Add entries under [Unreleased]; when the version
  is bumped, move them under the new version with its date.
- When a phase adds or changes behavior worth knowing later, record it in the matching docs/ file (history.md for the
  phase, platform-notes.md for platform findings, architecture.md for new files and parts), not here.
- Never store VM passwords in plaintext. Never bind the API to 0.0.0.0 without mTLS.
- Rotated VM passwords live in host memory only for the reuse window and are never logged or persisted.
  VM admin credentials for provisioning are stored only with DPAPI through ProtectedFile.
- Pairing secrets (PIN, w, x, y, K) and transfer tickets must never be logged or persisted. Audit summaries must never
  contain a secret. Types that hold a secret override ToString or Debug to hide it.
- Ask before any destructive Hyper-V operation, except on throwaway VMs created for tests (deleting those is allowed).
- After any change, without asking (the user's standing instruction): stop everything running from dist/
  (HyperHarbor.Host processes: host and tray; the portable client), rebuild with
  scripts\package.ps1 -Fast (add -SkipTests once the change's tests have passed, and -HostOnly or -ClientOnly
  when only one side changed), then relaunch: dist\host\Start-HyperHarbor.ps1 (service and tray) and
  dist\HyperHarbor-Client-<ver>-portable.exe. Report it if the rebuild fails, and leave things stopped.
  Start-HyperHarbor.ps1 refuses while the HyperHarborHost service is installed; uninstall it first.

## Commands
Toolchains are not on Git Bash PATH. Prefix: export PATH="/c/Program Files/dotnet:/c/Program Files/nodejs:$HOME/.cargo/bin:$PATH"
- Everything CI runs: powershell -ExecutionPolicy Bypass -File scripts\test-all.ps1 [-HostOnly|-ClientOnly] [-SkipAudit]
- Host: dotnet build HyperHarbor.sln -warnaserror && dotnet test HyperHarbor.sln
- Client (from client/): npm run check | npm test | npm run gen:api
- Rust (from client/src-tauri, in PowerShell): cargo fmt --all; cargo clippy --workspace --all-targets -- -D warnings;
  cargo test --workspace
- Package: powershell -ExecutionPolicy Bypass -File scripts\package.ps1 [-Fast] [-SkipTests] [-HostOnly|-ClientOnly]
- Contract changes: edit docs/api.yaml first, then Shared.Contracts, npm run gen:api, and lint (docs/development.md).

## Traps that cost time before
- Build Rust from PowerShell, not Git Bash: Git Bash's /usr/bin/link shadows MSVC link.exe.
- Do not use sed or perl substitutions on text with backslashes (Windows paths, C# verbatim strings); use a file editor.
  In Node's String.replace, pass a function when the replacement can contain $.
- Do not run cargo fetch; it downloads every target platform's dependencies.
- HyperHarbor.Host.exe is a GUI-subsystem executable: from PowerShell, pipe or redirect its output (`| Out-String`).
- Commit messages from Windows PowerShell: write the message to a file and use git commit -F <file>; a here-string piped
  to git splits into arguments.
- Svelte: run the svelte-autofixer on every .svelte file you change
  (`npx --yes @sveltejs/mcp@0.1.27 svelte-autofixer <file>` from client/ without the MCP server).
- Vitest: write `beforeEach(() => { invoke.mockReset(); })` with braces (a returned function runs as a teardown).
- Every non-GET route needs .Audited(); elevated routes add .RequireElevation(). EndpointSecurityTests enforce it.
- When a release changes how a store writes its file, raise DataFormat.Current and add the migration.
- Live tests that need elevation or a real VM are opt-in through environment variables (docs/development.md).
