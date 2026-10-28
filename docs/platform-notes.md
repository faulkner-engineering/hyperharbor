# Platform notes

Behavior of Windows, Hyper-V, Remote Desktop, and the libraries HyperHarbor uses, as found by running it. Read the
section for an area before changing it: most of these were learned from a failure.

## General

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
- Nothing may copy the data folder assuming no file is open: the winget index downloads into winget\ in the
  background (at start and daily), and both the update self-test copy and test cleanup met its open .partial file
  ("being used by another process", 2026-10-25; the flaky UpdateApiTests case). DataBackup leaves winget\ out, and
  TestHost sets Winget:FetchAtStart=false so in-memory hosts never download it.
- When a release changes how a store writes its file, raise DataFormat.Current and add the migration.
- schtasks /Create without /XML makes tasks that start only on AC power (they sit "Queued" on battery).
- System.Text.Json rejects a UTF-8 BOM in raw bytes; read hand-editable JSON through Utf8Json.ReadFile.
  PowerShell 5's Set-Content -Encoding utf8 writes one.
- All endpoints except pairing require a paired client certificate (PairedDeviceAuthenticationHandler).
  Kestrel accepts any client cert in the handshake; the fingerprint check is in the handler.
- Pairing secrets (PIN, w, x, y, K) must never be logged or persisted. Change the protocol only via docs/pairing.md,
  regenerate vectors with HH_WRITE_SPAKE2_VECTORS=1, and confirm the Rust tests still pass.
- Files under %ProgramData%\HyperHarbor that grant access (certificate, paired devices) go through ProtectedFile.
- ProtectedFile gives full control to SYSTEM, Administrators, and the writing account. An elevated helper runs as
  whichever administrator approved the UAC prompt; for a standard user that is another account, so before 0.2.4 the
  users.json and console-accounts.json.protected that Start-HyperHarbor.ps1's setup wrote locked the portable host
  out (UnauthorizedAccessException on users.json, exit code 82; seen 2026-10-26 with hh-owner approving as
  hha-owner). The helper now takes --for-user=<SID> from portable callers (never while the service is installed),
  writes for that account, and first restores its access to existing files (DataDirectoryAccess). A portable host
  that still cannot open a data file says which and exits with code 3.
- Other LAN devices need an inbound firewall rule for TCP 48443. The packaged host's start script creates it;
  a dev run from source does not.
- mDNS on the host goes through DnsServiceRegister (dnsapi.dll). Do not bind UDP 5353 in the host.
- ASP.NET Core 8 logs every handled exception; that category is off in appsettings.json and ApiExceptionHandler logs instead.
- Svelte: run the svelte-autofixer MCP tool on every .svelte file you change. Without the MCP server, the same check runs
  from client/: `npx --yes @sveltejs/mcp@0.1.27 svelte-autofixer <path to .svelte file>`. Its suggestions about
  setInterval and refreshVms inside $effect in routes/+page.svelte are the page's polling timers, which belong there.
- Host log download (API 1.16.0 2026-10-07): POST /host/logs/bundle (elevated, audited) returns
  a zip from Host.Core/Diagnostics/LogBundle (logs\*.log newest first, 100 MB cap, summary.txt; not the audit log). It is a
  POST because EndpointSecurityTests require elevated routes to be audited and forbid auditing GETs. The Rust client
  (download_host_logs) saves it through a file dialog; Host menu > Download host logs…, gated on hostSupports 1.16.0.
- Mixed versions: every 404 the host sends on purpose has a `detail`; a bare 404 (status code pages) means an older
  host without the route, and the client's api.rs check() turns it into problem code hostOutdated with an
  update hint. Gate UI for features added in a newer API version with hostSupports(host, "x.y.z") in client.ts
  (mDNS hosts report apiVersion; unknown versions pass and rely on hostOutdated).
- UI automation of the client: WebView2 inputs ignore SendKeys when the window is not foreground; set
  values with UI Automation ValuePattern instead.

## Client windows (Tauri v2, 2026-10-25)

- Making or closing a window inside a synchronous Tauri command deadlocks on Windows: the new window stays a blank white
  box and the app's other commands stall (the passphrase card, first build). Such commands must be async;
  window_command_tests in lib.rs checks it.
- A second window that loads an app route (WebviewUrl::App("overlay")) needs that route prerendered
  (routes/overlay/+page.ts), so build/overlay.html exists; adapter-static writes only index.html otherwise.
- A window with always_on_top and focused(false) draws over a full-screen mstsc session without taking its focus
  (the foreground stayed TscShellContainerClass). Keep such a card below title bar height: at the very top right it
  covered a maximized window's close button.
- Alt+F4 closes an undecorated window through Windows itself, even when the app's command channel is stuck.
- A window's height in tauri.conf.json is its content height. With the native title bar the page had all 640 px; the
  app's own bar (36 px) comes out of that, so the default height is 676 and the minimum 516 to keep the page's space.
  The Create VM dialog is tuned to fit that space unscrolled; at 640 its buttons were cut off (checked by screenshot).

## Tray windows (WebView2, 2026-10-05)

- prefers-reduced-motion is on in many Remote Desktop sessions (Windows animation effects off). A reduced-motion rule
  that only shortens animation-duration leaves infinite animations looping at that speed: the 800 ms spinner and the
  1.4 s sliding bar ran about a thousand times faster (seen 2026-10-27). theme.css also sets animation-iteration-count
  to 1, and ProgressBar draws a still bar; e2e/motion.spec.ts checks both with Playwright's reducedMotion.

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

## Hyper-V (verified on a live host)

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
- The Default Switch's Name is C08CB7B8-9B3C-408E-8E30-5E16A3AEB444. The development host's default VHD folder is
  C:\ProgramData\Microsoft\Windows\Virtual Hard Disks; HyperHarbor-Linux lives in Public Documents.
- A running VM holds its disk files open, so a delete-access check only means something once it is off.

## Remote Desktop (Phase 7)

- PowerShell Direct needs a running Windows guest; Linux guests fail fast with GuestUnavailable (503).
  Provision and connect take tens of seconds (Linux package installs take minutes), so the client uses
  25 min and 90 s request timeouts.
- The client only retries another host address on a connection error, never after a timeout, so a
  provision or rotation is not repeated.
- TERMSRV credentials the client writes are Generic, session-scoped, and tagged "HyperHarbor temporary
  credential"; startup cleanup removes only tagged entries (users may have their own TERMSRV entries).
- An administrator's reset of an account's password (Set-LocalUser -Password, NetUserSetInfo) makes the account's DPAPI
  data unreadable ("Key not valid for use in specified state"); a change from the old password (ADSI
  IADsUser.ChangePassword, as any caller, with the account signed out) keeps it. Checked 2026-10-26 on a Windows 11
  26100 guest with a scheduled task as the account (Start-Process -Credential fails in a PowerShell Direct session with
  0xC0000142). Chromium-based browsers keep their encryption key with DPAPI and check their settings with it, so a reset
  logs them out and Brave disables extensions as "changed by another program". PasswordRotator therefore changes the
  password from the one VmAccountPasswordStore kept, and falls back to a reset (logged with the guest's reason) when that
  fails; hh- accounts are created with UserMayNotChangePassword, which the change clears first.
- Secrets: ProvisionVmRequest, VmConnection, GuestCredential, RotatedPassword, and the Rust VmConnection
  override ToString or Debug to hide passwords. Keep it that way for any new type that holds one.
- Guest error text reaches logs and problem details. ProvisioningService and PasswordRotator pass it
  through GuestErrors.Sanitize (secrets replaced, control characters removed, length capped).
- The client validates VmConnection (IP address, plain account name) before writing the .rdp file.
- mstsc titles a session window "<.rdp file name> - <address> - Remote Desktop Connection", so the client names
  the file after the VM (rdp::window_file_name makes the name safe for a file) and writes each launch's file to
  its own folder under %TEMP%, since two sessions can share a name. The address stays in the title, which is
  how the client tells that the session window opened. (2026-10-26.) Verified live 2026-10-27: the address in the
  title includes the port from "full address" ("HyperHarbor-Test - 172.31.215.11:3389 - Remote Desktop Connection"),
  so the client leaves the port out when it is 3389 (rdp_endpoint) and the title reads "<VM name> - <IP>".
- Live test harness findings (SessionLiveTests, 2026-10-27): a CertHash value under HKCU\Software\Microsoft\Terminal
  Server Client\Servers\<address> with the presented certificate's SHA-1 did not stop mstsc's "could not be
  authenticated" dialog, and Win32_TSGeneralSetting.SSLCertificateSHA1Hash matched the served certificate anyway; the
  test answers Yes in the dialog. During a user's first sign-in, PowerShell Direct refused the stored administrator for
  a while ("The credential is invalid"), then accepted it again. Through Invoke-Command, a string gains PSComputerName
  (and from Get-Content, PSPath), so ConvertTo-Json writes it as an object; read files with [IO.File]::ReadAllText.
- The host sends the bare account name (hh-owner), not .\hh-owner: mstsc adds ".\" itself, and a doubled
  ".\.\hh-owner" fails as an unknown user (guest event 4625, substatus 0xc0000064) and prompts for a password.
- Add-LocalGroupMember takes the group by -SID 'S-1-5-32-555' and the member as the LocalUser object
  (-Member $user); passing $user.SID as the member fails to bind.

## Linux guests (SshAccountManager, SSH.NET)

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

## VM console (Phase 9; verified on the development host 2026-10-04 with mstsc against TestWindows)

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
  `HyperHarbor.Host.exe --setup-console [--for-user=SID] [data dir] [result file]` (tray: Set up console access;
  the installer and Start-HyperHarbor.ps1 run it when console-accounts.json.protected is missing, and the script
  also when a data file cannot be opened) and removed with
  --remove-console. It denies interactive, Remote Desktop, batch, and service logon; network logon stays.
- TestServer cannot upgrade connections (IsUpgradableRequest is always false), so the API tests stop at 426
  and ConsoleTunnelTests run the tunnel handler on real Kestrel with an echo server for VMMS.
- reqwest's request timeout does not end an upgraded stream (console_tunnel_outlives_the_request_timeout,
  ignored because it is slow).
- Concurrent consoles share the TERMSRV/127.0.0.1 credential until each session opens.

## Unattended installs (Phase 9.2 to 9.4)

- Host.Core/Unattend: profiles (three read-only built-ins plus each User's own), AutounattendBuilder,
  CloudInitBuilder (SHA-512 crypt via Sha512Crypt), SeedIso, IsoInspector, UnattendedSetup (create job
  steps), UnattendedInstallStore, UnattendedInstallWatcher (run by InstallWatcherService every
  Install:PollSeconds).
- Microsoft's Windows ISOs are UDF only (their ISO 9660 part holds a README), so IsoInspector tries
  DiscUtils.Udf first. Editions come from the XML resource in sources\install.wim or install.esd.
- DiscUtils pads the Joliet volume label with ASCII spaces (U+2020 in UCS-2); SeedIso rewrites it, because
  cloud-init finds the seed only by the label CIDATA. Extensionless Joliet names read back from DiscUtils
  with a trailing period (user-data.); Linux strips it. Not yet verified with a real Ubuntu install.
- Msvm_Keyboard.TypeText sends nothing the Ubuntu 24.04 installer's console shows, while TypeKey works (Enter, and
  letters as virtual-key codes 0x59 0x45 0x53 for "yes"); checked 2026-10-27. The host reports awaitingConfirmation from
  the start of a Linux install, so keys sent at once land in GRUB's menu instead ("e" opens its editor and the boot
  stops); the menu times out after about 30 seconds.
- Linux installers need the Microsoft UEFI CA Secure Boot template (also for console installs). Ubuntu asks
  "Continue with autoinstall?" at the console, since the seed cannot change the kernel command line.
- The create job presses Space (Msvm_Keyboard.TypeKey) twelve times on the first boot only, to pass
  Windows media's "Press any key to boot from CD or DVD".
- Turning a VM off (RequestStateChange 3) can return before it is off; a Start right after it fails with "Start is not
  valid in the current state" (2026-10-26, nested Hyper-V). WindowsSetupBootRecovery retries Start for 30 seconds.
- Windows' DVD boot loader (cdboot.efi in efisys.bin) reads UDF 1.02 but not UDF 2.50: an IMAPI2 image with
  UDFRevision 0x250 passes "Press any key", then the firmware falls through to PXE and logs 18603. The same files as
  UDF 1.02 (IMAPI2's default, and what Microsoft's media use) install. On the same VM, Start-VM made an automatic checkpoint (AutomaticCheckpointsEnabled
  is on for VMs HyperHarbor creates) while HyperHarbor's own CIM start did not; a checkpoint blocks growing the disk.
- A first boot can still end with nothing installed (2026-10-25, laptop, build 26300 media, a second VM running): the
  keys got Setup started, the guest reset itself 26 seconds later (cause unknown), the second boot's prompt timed
  out with no key, the empty disk did not boot, and the VM sat at the firmware's boot summary ("The boot loader
  failed") for an hour. Hyper-V logs each boot in Microsoft-Windows-Hyper-V-Worker-Admin: 18601 when the firmware
  starts any boot loader (the DVD prompt included), 18603 when every boot device failed, and 18514 for a reset by the
  guest; the VM ID is in the user data (<VmId>, upper case). The watcher now restarts a Windows install whose newest
  boot event is 18603 and presses the key again, at most three times (WindowsSetupBootRecovery). 18603 cannot happen
  once Windows is on the disk, so this never touches an installed system.
- The watcher counts only time the VM runs toward Install:TimeoutMinutes, waits for the guest's OS and
  address in data exchange, then for a real RDP Connection Confirm (Windows) or SSH identification line
  (Linux), and marks the VM provisioned only after Remote Desktop answers again after setup. It then
  rotates the one-time administrator password and ejects and deletes the seed. Failed setups back off
  1, 2, 4, 8 minutes, then fail. A Linux profile without a desktop ends after SSH (no Remote Desktop).
- Verified end to end on 2026-10-04 with a Windows 11 Pro install (TESTWINDOWSINSTALL), including Connect.
  Not yet with a real Ubuntu install.

## Administrator windows (phase 28; verified 2026-10-25 on HyperHarbor-Test, Windows 11 build 26300)

- Logon rights from PowerShell Direct are reliable: the guest compiles an LSA wrapper with Add-Type
  (LsaAddAccountRights, LsaEnumerateAccountRights) and SeDenyNetworkLogonRight and SeDenyRemoteInteractiveLogonRight
  read back set. Opening a window (create or enable, groups, rights, read back) took 2.6 s; closing took 2.3 s.
- ADSI's IsMember compares ADsPaths, and a member's path includes the workgroup (WinNT://WORKGROUP/PC/user), so
  IsMember("WinNT://PC/user") is False for a real member. The script compares objectSid of the group's members
  instead, which also works when a group holds a SID that no longer resolves (Get-LocalGroupMember fails on those).
- Script blocks run with Invoke-Command do not inherit the caller's $ErrorActionPreference; each admin script block
  sets Stop itself, so a failed Add-LocalGroupMember throws instead of being skipped.
- An open that fails leaves its close to a background task; a test process that exits right away leaves the account
  enabled (it did once). The host service's sweep closes such a window within 30 seconds.

## Performance mode (Phase 10; Host.Core/Performance)

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
  incomplete export. The default folder is the tray's disk export folder (Storage, Disk exports; still BackupFolder in
  host-settings.json), default Public Documents\HyperHarbor Disk Exports. Until 0.2.3 the tray called it "VM backups"
  and the default was Public Documents\HyperHarbor Backups, which made people look there for the Kopia backups; those
  go to backup targets.
- Pre-shutdown: GpuVmShutdownCoordinator shuts guests down and never turns a VM off. As a Windows service,
  PreshutdownServiceLifetime sets ServiceBase._acceptedCommands (PreshutdownServiceLifetimeTests fail if
  .NET renames it); not yet tried through a real Windows shutdown. In console mode the tray's ShutdownGuard (a
  hidden top-level window) refuses WM_QUERYENDSESSION with a block reason while GPU VMs run, except on
  sign-out; it stands aside while the installed service runs. Not yet tried by hand.
- GpuEventReader reads the System log (nvlddmkm, amdkmdag, amdwddmg, igfx*, and Display 4101) for the last
  7 days, cached 5 minutes, into HostGpu.warnings.

## Lean host (2026-10-07)

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

## Host Remote Desktop (maintenance sessions on the host itself; API 1.10.0 2026-10-05)

- GET /host/remote-desktop reads fDenyTSConnections, RDP-Tcp PortNumber, EditionID (Home editions start with
  "Core" and are unsupported), and the firewall group "@FirewallAPI.dll,-28752" through HNetCfg.FwPolicy2.
- POST /host/remote-desktop/enable (elevated, audited) calls Win32_TerminalServiceSetting.SetAllowTSConnections(1, 1)
  in root\cimv2\TerminalServices, which also opens the firewall group; NLA is left alone. An unelevated host
  answers 409 requiresInstalledService.
- The client's connect_host writes an .rdp file with no user name and prompt for credentials:i:1; mstsc asks
  for the host's Windows account. No host credential is stored or sent.
- Host controls other than + New VM live in the header's Host menu (components/Menu.svelte, shared with the
  VM row's menu). Menu closes the <details> element directly: jsdom fires no toggle event, so bind:open fails in tests.

## Setup profiles (Phase 12a)

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
  wingetUnavailable. Since API 1.34.0 this is only the fallback.
- winget package index (2026-10-23): https://cdn.winget.microsoft.com/cache/source2.msix is about 3.8 MB, a zip with
  Public/index.db (about 9 MB SQLite, metadata majorVersion 2, about 15,750 packages). Table packages has id, name,
  moniker, latest_version; tags2 and tags2_map hold tags (often hyphenated, such as video-editor). No descriptions or
  publishers: those are in per-package manifests, not the index. source.msix (about 22 MB) is the older format 1.
  winsqlite3.dll in System32 reads it (sqlite3_*16 functions); download plus first search took about 0.4 s.
- Extension stores have no public search API; the client opens chromewebstore.google.com/search/<text> and
  microsoftedge.microsoft.com/addons/search/<text> in the browser instead.
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

## Applying setup profiles (Phase 12b; spike and live runs on HyperHarbor-Test, Windows 11 Pro 24H2 media, 2026-10-05)

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
- Default browser (browser.makeDefault, 0.2.4): Windows 11's UserChoice Protection Driver (UCPD) stops other programs
  from writing the http and https UserChoice keys, and their hash ties them to the user, so a script cannot choose
  the default browser. ProgIds in catalogs/browsers.yaml: ChromeHTML, MSEdgeHTM, BraveHTML.
  The policy "Set a default associations configuration file" (DefaultAssociationsConfiguration), which 0.2.4-beta.1
  used, does nothing on HyperHarbor's VMs: Windows reads it only on domain-joined (Group Policy) or Microsoft Entra
  joined (ApplicationDefaults CSP) PCs. Seen 2026-10-27 with SessionLiveTests on Windows 11 build 26300: with the
  policy and its file in place before hh-owner's first sign-in, links opened in Edge, and Shell-Core/AppDefaults logged
  no policy step at any sign-in. At the first one ("First logon of new user") it applied only the OEM defaults
  ("AppDefaults-ApplyOEMDefaultAssociations - apply dll resource", then "- apply xml file", which set http and https
  to MSEdgeHTM); later ones logged only "AppDefaults-Logon-UserProfileLoaded".
  So the applier now merges the browser's four associations into that xml file,
  C:\Windows\System32\OEMDefaultAssociations.xml (owned by TrustedInstaller, so through
  `Dism /Online /Import-DefaultAppAssociations`, which replaces the whole file). It merges into the file itself:
  /Export-DefaultAppAssociations exports the administrator's own associations instead (83 entries against the
  image's 157). This reaches only accounts that have not signed in yet, as on a new VM; one that has keeps its choice,
  and the default can be changed in the VM afterwards. Verified 2026-10-27 on a new VM (SessionLiveTests): after
  hh-owner's first sign-in, http and https resolved to BraveHTML, with a hashed UserChoice Windows wrote itself.
- Edge cannot be removed outside the European Economic Area: Windows offers the uninstall only when the region chosen
  at setup is in the EEA, and other apps need its WebView2 runtime. Forced removals are unsupported and Windows Update
  can put Edge back. The tweak system.hideEdge only sets Edge and EdgeUpdate policies (StartupBoostEnabled,
  BackgroundModeEnabled, HideFirstRunExperience, DefaultBrowserSettingEnabled, CreateDesktopShortcutDefault); the
  taskbar pin needs a taskbar layout file and is left alone. A removal that finds no package counts as done (so
  profiles can be applied again), so remove.appx never reports a name that matches nothing.
- After a restart, GuestRestart counts Remote Desktop only after one check found it down (Windows answers for a few
  seconds after the request, and a guest reboot does not reset uptime): 60 checks of 15 seconds.
- The User's account hive: loaded under HKU\<SID> while it is signed in, otherwise its NTUSER.DAT loaded under
  HKU\HyperHarborUser. An account that never signed in has no profile, so only the Default hive changes. The live
  test gives the account a profile with userenv CreateProfile, which needs no sign-in.

## Signed .rdp files (client src-tauri/src/rdp_trust 2026-10-22; found on build 26100.8875)

- Since the April 2026 update (CVE-2026-26151), mstsc shows "Remote Desktop Connection security warning" for any .rdp
  file it cannot trust, every redirection unticked, every launch. A file skips it only when signed by a certificate that
  is BOTH on the trusted publishers list (TrustedCertThumbprints under Software\Policies\Microsoft\Windows NT\Terminal
  Services, HKCU or HKLM, entries "sha256:<HEX>", comma-separated) AND chains to a trusted root. The list alone still
  showed "Unknown publisher" for a self-signed certificate. HKCU\Software\Policies is writable only by administrators.
- rdpsign.exe: only option "/sha256 <hash>", but it finds a CurrentUser\My certificate only by its SHA-1 thumbprint
  (0x80092004 for the SHA-256); no administrator rights needed. It refuses a Document Signing certificate (0x8007000d);
  Server Authentication and Code Signing work. It writes UTF-16 with "\r\r\n" line ends, which mstsc reads. It signs
  only the "secure" settings (signscope); camerastoredirect, audiocapturemode, and monitor lines stay outside it.
- Adding to or removing from CurrentUser\Root always asks the user (a Windows dialog, even elevated; Import-Certificate
  fails with "UI is not allowed"), so the elevated copy (--trust-rdp-signer, --untrust-rdp-signer) uses
  LocalMachine\Root and writes HKEY_USERS\<sid> of the requesting user. It checks the certificate (rdp_trust::check_signer:
  self-issued "HyperHarbor connection signing", critical basic constraints not a CA, Server Authentication only, no
  other extensions) because an unelevated process wrote the file.
- HH_RDP_TRUST_LIVE=1 cargo test --lib rdp_trust::system::live -- --ignored: makes a real signer, signs with rdpsign,
  deletes it (no elevation).
- Verified on a laptop 2026-10-22: "Trust HyperHarbor connections" (one UAC prompt, no other dialog), then
  Connect opened without the security dialog.

## Audit and elevation (Phase 8)

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
- Wake-on-LAN fixes need elevation too (API 1.8.0, decided 2026-10-04): the installed
  service runs as LocalSystem and applies them directly, with no tray approval. An unelevated console
  host still asks the tray as well. TestHost pins the unelevated WakeFixCoordinator path.

## Kopia backups and encrypted guests (measured 2026-10-27 with kopia 0.23.1)

- Kopia dedups unchanged data of an encrypted disk: a snapshot of 1 GiB of random data (the stand-in for BitLocker
  ciphertext) with no change uploaded 566 bytes. BitLocker's XTS-AES gives an unchanged sector the same ciphertext.
- What hurts is the piece size. Each changed 4 KiB sector re-uploads the whole piece around it, and ciphertext does not
  compress. With the default splitter (DYNAMIC-4M-BUZHASH, pieces of about 4 MiB) 10 scattered 4 KiB writes in 1 GiB of
  random data uploaded 60 MB, 100 uploaded 405 MB, and 1,000 the whole file. On compressible data the same writes cost
  kilobytes, because the re-uploaded pieces compress.
- 100 scattered 4 KiB writes in 256 MiB of random data, by splitter: DYNAMIC-4M 211 MiB, DYNAMIC-1M 112 MiB,
  FIXED-1M 84 MiB, FIXED-256K 24 MiB. A disk image never shifts its data, so fixed pieces lose nothing against
  content-defined ones. HyperHarbor uses FIXED-1M for VM disks (`policy set --splitter`); smaller pieces mean more
  index entries (about 4 million per TB at 256 KiB). Changing a source's splitter makes its next snapshot a full upload.
- kopia 0.23.1 does not report a snapshot's upload: `snapshot create --json` has only logical sizes (stats.totalSize),
  and its progress line on standard error is not printed at the end. `content stats` has no --json flag; with --raw it
  prints "Total Bytes: N" and, when some content is compressed, "Total Packed: N (compression x%)". The packed total
  before and after a snapshot is what it stored. It reads the local index cache.
- `policy show <source> --json` gives the effective policy; `"splitter": {}` means kopia's default.

## Growing disks (0.2.4)

- Msvm_ImageManagementService.ResizeVirtualHardDisk(Path, MaxInternalSize) grows a VHDX; Hyper-V allows it on a running
  VM only for a VHDX on a SCSI controller (every disk of a Generation 2 VM), and never while the VM has checkpoints.
  GetVirtualHardDiskSettingData gives MaxInternalSize, Format (3 is VHDX), and ParentPath. Verified 2026-10-26 on a
  bare VHDX (DiskGrowLiveTests); online growth on a running VM not yet.
- HyperHarbor's autounattend puts EFI, MSR, and C: (Extend) on disk 0, with no recovery partition after C:, so C: is the
  last partition and can grow. Windows installed from the console often puts a recovery partition after C:; the
  extender then leaves it and says so.
- Mount-DiskImage needs administrator rights, so only the installed service extends an off VM's partition. A running
  guest sees the new size after Update-HostStorageCache; the extender waits up to 30 seconds for it.

## VM lifecycle (Phase 8)

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

## Camera sharing (2026-10-08; every point below was found by running it on Windows 11 build 26200)

- A virtual camera source is a COM DLL the Frame Server (svchost as LocalService) loads, not code in the client. The
  class must be in HKLM and the DLL readable by LocalService: %ProgramData%\HyperHarbor is locked to Administrators,
  SYSTEM, and the host's service account, so a DLL there gives "Access is denied" from MFCreateVirtualCamera.
  Per-user folders fail the same way. The client therefore runs a copy of itself elevated (ShellExecuteEx runas;
  "hyperharbor-client.exe --setup-camera <dll> <result file>" and "--remove-camera <result file>", dispatched in main.rs)
  that copies the DLL to %ProgramFiles%\HyperHarbor Camera and runs regsvr32. The elevated copy installs only a DLL named
  hyperharbor_vcam.dll beside its own exe (is_trusted_source), because it registers a class the Frame Server loads.
- The Frame Server never unloads the DLL (DllCanUnloadNow is S_FALSE), so replacing it needs the FrameServer service
  stopped (setup does that; it starts again on demand).
- The Frame Server activates the source's class as an IMFActivate and sets attributes on it: the friendly name
  (MF_DEVSOURCE_ATTRIBUTE_FRIENDLY_NAME) and the creating process id (attribute 5F8D322E-0FE4-43E4-9E50-D83ECD9FC2B8).
  One CLSID serves every camera: the pipe is pipe_name(creator pid, friendly name), and the source checks
  GetNamedPipeServerProcessId against the creator pid. AddRegistryEntry on the virtual camera is denied; not needed.
- Frames cross by named pipe (client serves, source connects), not shared memory: a named mapping in the Global
  namespace needs a privilege a user process lacks. The pipe's SDDL is D:P(A;;GA;;;SY)(A;;GA;;;OW)(A;;GR;;;LS).
- The virtual camera offers ONE size (1280x720). With 640x480 also offered, an application that probed the formats and
  picked 640x480 left the next application starting in 640x480 until it asked again. The Frame Server calls
  CreatePresentationDescriptor once per activation, keeps that descriptor, reuses it for every application, and updates
  its current type whenever any application picks one; Start then arrives with the cached type and Stop is not called
  between applications (found with a temporary log inside the source, then removed). The source cannot reset the cache,
  so nothing that could stick is offered. Regression test: camera::live::every_consumer_gets_720p_whatever_an_earlier_one_tried.
- Samples need timestamps from MFGetSystemTime plus MFSampleExtension_DeviceTimestamp; frame numbers counted from zero
  made the Frame Server's readers block forever. Pace the stream at 30 fps inside RequestSample.
- Windows lists a virtual camera as "<name> (Windows Virtual Camera)" (names_match handles the suffix); its symbolic
  link starts with \\?\swd#vcamdevapi#. It is listed within milliseconds of Start.
- mstsc accepts camerastoredirect:s:<one symbolic link>; each guest then lists exactly that camera as a Remote Desktop
  camera (Get-PnpDevice -Class RDCamera, "(redirected)") and neither physical camera. Two sessions at once each see
  only their own (tested with two VMs; concurrent sessions to one VM were not tried, and Windows client editions are
  believed to allow one interactive session).
- GetVersionExW lies without a compatibility manifest (reports build 9200); use RtlGetVersion (winmf::windows_build).
- windows-rs imports every function it calls by name at load time (raw-dylib). Calling MFStartup and the like directly
  made the client import mfplat.dll, mf.dll, mfreadwrite.dll, and mfsensorgroup.dll, which would stop it starting on
  Windows 10 (no MFCreateVirtualCamera) and Windows N editions. All free Media Foundation functions go through
  camera/mfapi.rs; mfapi's test parses the test exe's import table and fails on any mf*.dll. COM interfaces need no
  import. dumpbin /imports on the real exe shows only ordinary DLLs.
- COM interface types are not Send in windows-rs; VirtualCamera has a documented unsafe impl Send (multithreaded apartment).
- Media Foundation error codes for a busy camera (checked against the windows crate): MF_E_VIDEO_RECORDING_DEVICE_PREEMPTED
  0xC00D3EA3, MF_E_HW_MFT_FAILED_START_STREAMING 0xC00D3704, invalidated 0xC00D3EA2, E_ACCESSDENIED for the privacy
  setting. A busy camera could not be reproduced here (Frame Server cameras are shared), so the mapping is from the
  documented codes only.
- The camera for this PC (CameraService::ensure_local and remove_local) is one more fan-out entry with a fixed KeepLive
  policy that nothing focuses or shuts. It is made after the first VM camera, removed when no session has a virtual
  camera, and a failure making it is ignored. Tests that count cameras use Log::vm_created and friends to leave it out.
- Windows 11 has no system default camera (unlike the default microphone): each app picks one, some remembering the
  last used and some taking the first listed. The Settings > Cameras page holds per-camera image settings only. The
  virtual cameras are listed after the physical ones, and disabling the physical device would stop the capture too.
- The Rust crate-wide unsafe_code = "deny" stays; the camera files that call Win32 carry a file-level allow.
- connect_vm takes the VM's name (for the camera's name) and returns an optional notice for the toast.
- Tauri's bundle.resources fails every build when the file is missing, so the DLL is added by a separate config
  (tauri.camera.conf.json, passed by package.ps1) after package.ps1 builds it.
- Tests that cannot run live: "camera in use" by another application, a real Windows 10 start, and focus changes by
  clicking between windows (the foreground poll is unit tested with a fake).

## Machine view (three.js in the client, 2026-10-22)
- jsdom has no WebGLRenderingContext, and calling canvas.getContext there logs "Not implemented". webglAvailable()
  checks the constructor first, so component tests take the still path quietly; tests mock $lib/machine/scene.
- glTF from trimesh bakes positions into the vertices (node transforms are identity). Rotating a LineSegments built from
  them turns it about the world origin; center the edges and set the object's position to the center first.
- Headless Edge renders the scene with `--enable-unsafe-swiftshader --use-angle=swiftshader`; `--virtual-time-budget`
  sets how far the animation runs before `--screenshot`. `--disable-3d-apis` exercises the no-WebGL fallback.
- Windows animation effects off (Settings, Accessibility, Visual effects; SPI_GETCLIENTAREAANIMATION false) reaches
  WebView2 as prefers-reduced-motion: reduce. It was off on the laptop, so the view never moved until the Motion
  option overrode the preference. Headless Edge reproduces it with --force-prefers-reduced-motion.
- three is about 580 KB minified; MachineView imports scene.ts dynamically so it is its own chunk, fetched only when
  WebGL is available.

## Wake-on-LAN lessons (from real hosts)
- Readiness checks can all pass while waking fails. Seen on the desktop (2026-10-02): BIOS "PCI-E wake" was
  disabled. Firmware settings are not readable from Windows; the client shows a firmware checklist instead.
- A Hyper-V external switch bound to the wired NIC is a known Wake-on-LAN breaker, not yet a readiness check.
- Seen on the desktop (2026-10-21): Windows made the external switch's adapter (vEthernet (External), which carries the LAN
  address) a Public network, so the Private-only firewall rules for 48443 and SSH stopped applying and the host vanished
  from the LAN. The privateNetwork readiness check warns about it (adapters with a default route only, since the Default
  Switch's adapter is often Public; never fixed automatically).
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
  count: a Remote Desktop session to a VM never passes through the host, so the client
  checks in with GET /host every minute (sessions.rs) while mstsc windows it launched stay open, as long as the
  client app runs. Check with `powercfg /requests` (elevated): "HyperHarbor: ..." under SYSTEM.
