# Development

Building, testing, packaging, and releasing HyperHarbor. The architecture is in [architecture.md](architecture.md).

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
- Tray window browser tests (from host/src/Host.Tray/webui, after npx playwright install chromium): npm run test:e2e. They load
  the built UI in Chromium with a stand-in for WebView2's message channel and the first bridge.fixtures.json state, then check
  the painted theme (light and dark), the contrast of every piece of text against what it is drawn on, and sideways scrolling,
  on every page. Add a page to the nav and it is covered; vitest and jsdom cannot see the cascade (a global style in
  Ambient.svelte once turned the whole window black and passed every unit test).
- Lint contract: npx @redocly/cli lint docs/api.yaml
- Client (from client/): npm run check | npm test (Vitest) | npm run gen:api | npm run tauri dev
- Rust (from client/src-tauri, in PowerShell): cargo fmt --all; cargo clippy --workspace --all-targets -- -D warnings;
  cargo test --workspace (the workspace includes the camera DLL and the shared frame format)
- Live mDNS browse (host service running): cargo test live_browse -- --ignored --nocapture
- Live pairing over LAN (service running, something writing the PIN to the file):
  HH_LIVE_HOST=<lan ip> HH_PIN_FILE=pin.txt cargo test live_pairing -- --ignored --nocapture
- End to end on loopback (no LAN, no setup): powershell -ExecutionPolicy Bypass -File scripts\e2e.ps1
  It starts the real service on a free loopback port, so it can run while a packaged host is running.
- Live lifecycle (creates, changes, and deletes HyperHarbor-Test; ask first):
  HH_LIFECYCLE_LIVE=1 dotnet test HyperHarbor.sln --filter LifecycleLiveTests
- Live disk growth (a 1 GB VHDX in a temporary folder, no VM):
  HH_DISK_GROW_LIVE=1 dotnet test HyperHarbor.sln --filter DiskGrowLiveTests
- Live Performance mode (same VM; GPU partition, storage move, export; ask first):
  HH_PERFORMANCE_LIVE=1 dotnet test HyperHarbor.sln --filter PerformanceLiveTests

## Packaging (multi-machine testing)
- powershell -ExecutionPolicy Bypass -File scripts\package.ps1 [-Fast] [-SkipTests] [-HostOnly|-ClientOnly]
- Output in dist/ (git-ignored): HyperHarbor-Host-<ver>.exe (the whole host; about 187 MB uncompressed with
  -Fast), dist\host (the same exe plus portable Start/Stop-HyperHarbor.ps1 for development), client NSIS setup
  exe, portable client exe. The script fails if the host publish yields more than the one exe.
- -Fast is for test builds: the client without LTO at opt 1, incremental with 256 codegen units (a small client
  change rebuilds in seconds), no client installer (the portable exe only; a same-version installer left in dist
  is removed), an uncompressed host executable. Switching between fast and full recompiles once
  (about 6 min). Measured 2026-10-04: thin LTO took 170 s for a one-line client change, -Fast about 7 s.
- Measured 2026-10-23 (laptop, client crate about 23,000 lines): the release profile's codegen-units = 1 also
  applied to -Fast, so incremental reused nothing and every client build spent 100 s on the client crate; with
  256 units a rebuild takes 12 s. The rust-lld linker made no measurable difference (11.2 s against 11.7 s) and
  switching linkers rebuilds every dependency, so the builds keep MSVC link.exe. The camera DLL step takes
  under a second when the DLL is current.
- scripts\add-defender-exclusions.ps1 (elevated, once per development PC; -Remove undoes it) excludes the
  repository and the Rust, NuGet, and npm caches from Defender's real-time scanning.
- Upload throughput on loopback (2026-10-04): about 320 MB/s into the host and 351 MB/s from the Rust client
  (IsoUpload_Throughput with HH_BENCHMARK=1; upload_throughput with HH_E2E_SERVICE_EXE).
- Version comes from Directory.Build.props (host) and client/src-tauri/tauri.conf.json (client); keep them equal.
- The script refuses to run while anything is running from dist/ (Windows locks the exe).
- After `HyperHarbor.Host.exe uninstall` (data kept), %ProgramData%\HyperHarbor keeps the installed service's ACL
  (Administrators, SYSTEM), so the unelevated host that Start-HyperHarbor.ps1 starts dies on data-format.json
  (it now says which file it cannot open and exits with code 3). Start dist\host\HyperHarbor.Host.exe run elevated
  instead (seen on the laptop 2026-10-21); the tray from Start-HyperHarbor.ps1 connects to it.
- dist\host\Start-HyperHarbor.ps1 does one elevated setup (Private-profile firewall rule for TCP 48443,
  Hyper-V Administrators membership, console account), then starts "HyperHarbor.Host.exe run" and "--tray". It asks
  for elevation again when the current account cannot open a file in the data folder, and setup restores access
  (--setup-console --for-user). Builds are unsigned.
- Verified on 2026-10-02: packaged host plus client paired and listed VMs across machines.
- package.ps1 also writes dist\latest.json with "HyperHarbor.Host.exe write-update-manifest" (the code that
  reads manifests writes it; a prerelease version goes to the beta channel).
- Releases (.github/workflows/release.yml): push the commit to main, then tag vX.Y.Z (must equal both versions); the
  workflow runs package.ps1 -SkipTests, waits for the CI workflow to pass for that commit, and creates a DRAFT release
  with the host exe, latest.json, and the client installer. The release body is the version's CHANGELOG.md section
  (scripts\release-notes.ps1), so move the [Unreleased] entries under "## [X.Y.Z] - <date> (API a.b.c)" before tagging;
  the workflow fails in its first minute without that section (about 25 minutes when it also ran the tests and compiled
  every Rust dependency, 2026-10-21). Its Rust cache (shared key "release") comes from main, because a tag run cannot
  read another tag's caches: release-cache.yml builds the release profile on main when the Rust dependencies change
  and weekly, and builds nothing when the cache already matches. Run scripts\test-all.ps1 with cargo-audit installed
  (cargo install cargo-audit --locked) before tagging; without it the script skips the audit CI runs.
  Publishing the draft is the owner's step: stable hosts read releases/latest/download/latest.json (newest
  published non-prerelease); publishing a prerelease copies its latest.json to the fixed "channel-beta"
  release that beta hosts read. Publishing a stable release copies it there too when it is newer than the beta the
  channel offers, so beta users get it; the job rewrites the host manifest's "channel" to "beta", which hosts check.

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

## Live testing
- Live tests skip themselves when Hyper-V is unreachable ([HyperVFact]); the account must be in Hyper-V Administrators.
  [EnvironmentFact("VAR", ...)] skips unless the variables are set; [LocalHardwareFact] skips when CI is set.
- TestHost (in-memory API) captures logs at every level (Logs.AssertNoneContain) and uses a unique
  Tray:PipeName. RealTlsTests starts the service exe on loopback via Api:ListenAddress.
- Test VM HyperHarbor-Linux: Ubuntu 24.04 server (no desktop), user hhadmin, SSH key
  ~/.ssh/hyperharbor_linux_ed25519; sudo needs hhadmin's password. Dynamic memory 768 MB startup, because
  the development PC often had little free RAM.
- For throwaway VMs, create HyperHarbor-Test and delete it afterwards (LifecycleLiveTests does both, with a
  1 GB disk in a temporary folder). Ask before changing any other VM.
- Setup profiles: HH_PROFILE_LIVE=1 with HH_LIVE_GUEST_VM=<VM id> runs a read-only capture against a running Windows VM
  with a stored credential (reads the host's data folder, so run elevated; HH_PROFILE_LIVE_OUT=<file> saves the draft).
  HH_PACKAGE_SEARCH_LIVE=1 searches winget through pwsh; HH_EXTENSION_LIVE=1 checks catalog ids against the stores.
- Live host harness (Host.Tests/Live/LiveHostHarness): the real host exe from the test build on a loopback port with
  its own data folder (%LOCALAPPDATA%\HyperHarborHarness or HH_HARNESS_DATA), a seeded pairing, and a test passphrase,
  so live tests drive the API without the user and without touching the installed host. HH_HARNESS_ISO_FOLDER and
  HH_HARNESS_VM_FOLDER point it at images and VM storage (for example D:\ISOs and D:\VMs). It runs from Host.Tests\bin, so
  do not rebuild Host.Tests while one runs (build to another folder with -o; ExtensionResolverTests then fail because
  they look for catalogs/ above the output folder).
  - HH_SPIKE_LIVE=1 HH_SPIKE_ISO=<image>: creates HyperHarbor-Test with an unattended install if missing (about 20
    minutes) and runs the 12b spike checks. HH_PROFILE_APPLY_LIVE=1 HH_LIVE_GUEST_VM=<id>: applies a small profile
    with the real applier and reads it back (about 30 s). HH_PROFILE_E2E_LIVE=1 HH_SPIKE_ISO=<image>: creates
    HyperHarbor-Test2 with a setup profile and waits for Ready (about 25 minutes). HH_PROFILE_EXISTING_LIVE=1
    HH_SPIKE_ISO=<image>: creates HyperHarbor-Test, applies a profile through the API with a restart, checks the
    account's hive, and deletes the VM. Throwaway VMs created for tests may be deleted without asking (decided 2026-10-05); ask before changing any other VM.
- Administrator windows: HH_GUEST_ADMIN_LIVE=1 with HH_LIVE_GUEST_VM=<VM id> runs GuestAdminLiveTests against a running
  Windows VM that HyperHarbor set up (its credential and setup come from the host's data folder, so run elevated, or
  from HH_GUEST_ADMIN_DATA, for example the harness folder). It opens a window, checks over PowerShell Direct that
  hha-owner is enabled, in Administrators, not in Remote Desktop Users, and denied network and Remote Desktop sign-in,
  closes it, and checks it is disabled. The account stays in the VM, disabled. A throwaway VM for it:
  MachineProfileLiveTests with HH_MACHINE_KEEP=1 (HyperHarbor-Test, kept), then HH_GUEST_ADMIN_DATA=%LOCALAPPDATA%\HyperHarborHarness.
- The UAC prompt itself is checked by hand (after a host and client with API 1.35.0 are running):
  1. Connect to a set-up Windows VM, and in the client choose the VM's menu > Administrator access…; enter the admin
     passphrase if asked. The dialog shows hha-<username> and a passphrase of three words and a digit, and a card with
     the passphrase appears at the top right of the screen.
  2. In the VM, start something that needs elevation (right-click Terminal > Run as administrator, or open
     Computer Management). The UAC prompt asks for credentials: choose More choices, pick "HyperHarbor administrator
     for <username>", type the passphrase with its hyphens, and choose Yes. The program starts elevated.
  3. Choose Done in the client. Start another elevated program in the VM: the HyperHarbor account is no longer
     offered (it is disabled), and typing hha-<username> with the old passphrase fails.
  4. Open a window again and let it run out (set 2 minutes with PUT /host/guest-admin/settings to wait less): the
     dialog says it ended, and step 3's checks hold. Also check that `mstsc` to the VM as hha-<username> with the
     passphrase is refused while the window is open (SeDenyRemoteInteractiveLogonRight), and so is
     `net use \\<vm>\c$ /user:<vm>\hha-<username>` (SeDenyNetworkLogonRight).
- Camera tests (2026-10-08): HH_VCAM_LIVE=1 cargo test --lib camera::live -- --ignored --nocapture --test-threads=1 (from
  client/src-tauri; needs the camera source registered, which opens the physical camera for a moment). Two throwaway
  VMs for redirection tests: HH_CAMTEST_LIVE=1 HH_SPIKE_ISO=<image> with HH_HARNESS_ISO_FOLDER and HH_HARNESS_VM_FOLDER
  runs CameraTestVmsLiveTests, which installs HyperHarbor-Cam1 and -Cam2 one after the other (about 25 minutes each).
  Windows 11 setup refuses less than 4 GB even with the LabConfig bypass in the autounattend, so they install at 4096 MB
  and are cut to 2048 MB afterwards (not yet fixed in the create flow: a VM created below 4 GB sits on the "requirements"
  screen with no error). CameraConnectionLiveTests (HH_CAMCONN_LIVE=1, HH_CAMCONN_OUT=<file>) writes the VMs' Remote
  Desktop credentials to that file (delete it afterwards) and then runs guest queries: write the VM name on the first
  line and PowerShell below it to <file>.query, read <file>.result, and create <file>.done to stop it. The product's
  own end to end check is camera::live::the_client_gives_two_vm_sessions_their_own_cameras with HH_CAMCONN_FILE=<that file>.
  Run the harness from a shell whose PSModulePath is the Windows PowerShell default: the host runs powershell.exe 5.1,
  which inherits PowerShell 7's module path from a pwsh shell and then cannot load Microsoft.PowerShell.Security
  ("could not connect to the guest", "ConvertTo-SecureString ... module could not be loaded").
  PowerShellDirectRunner scripts must answer {ok, result} or {ok:false, error, stage}; any other keys read as null or
  "Unknown error.".
- VM console automation: Msvm_Keyboard.TypeText can drop characters, so send TypeKey one key at a time.
  Read the screen with GetVirtualSystemThumbnailImage (RGB565) and confirm a prompt is gone before moving on.
