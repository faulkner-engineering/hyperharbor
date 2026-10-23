# Development history

HyperHarbor was built in phases, one per working session, read-only first. Each entry records what was built, how it was
verified, and what was found along the way. User-facing changes per release are in [CHANGELOG.md](../CHANGELOG.md).

## Phases
1. Solution skeleton + OpenAPI contract (done)
2. List VMs and state (read-only) (done)
3. Start/stop + Kestrel API on localhost (done)
4. mDNS discovery + client VM list (done)
5. PIN pairing + mTLS (done)
6. Wake-on-LAN (done)
7. One-click RDP with a per-User VM account (Windows guests via PowerShell Direct; Linux guests via SSH
   and xrdp)
8. VM lifecycle (done 2026-10-03):
   audit log and admin passphrase elevation; delete VM (off only, optional disks and checkpoints, refuses
   shared parent disks); host resources, ISO library, switches; create Gen 2 VM from ISO (Secure Boot, vTPM)
   as a job; compute settings (vCPU, memory, nested virtualization, MAC spoofing) with shut down and apply;
   client power controls and dialogs for all of it.
9. Console access and unattended provisioning (done 2026-10-04): VM console through a
   tunnel to the host's port 2179 with a per-User standard host account (hhc-<username>); autounattend and
   cloud-init seed ISOs on create; KVP readiness watcher that provisions automatically once RDP answers.
10. Performance mode (GPU-P and Remote Desktop tuning) for Windows VMs (done
   2026-10-04; host side verified live on a throwaway VM, guest setup not yet): apply to an off VM (fixed memory, vCPU, GPU partition share, MMIO gaps,
   no checkpoints, TurnOff stop action, optional storage move); guest setup over PowerShell Direct (driver
   copy, ADMX-verified RDP policy, DWMFRAMEINTERVAL); LAN .rdp tuning; driver drift and re-sync; disk export
   before changes; pre-shutdown of GPU VMs; GPU driver error warnings.
11. Host self-update with side-by-side versions (in progress). Releases go to
   https://github.com/faulkner-engineering/hyperharbor (latest.json manifest: version, URL, SHA-256). Decided:
   health.json plus a TLS check instead of an anonymous health route; "install now" in the tray and (since
   2026-10-05, API 1.11.0) from a client with elevation; stable
   channel by default; Authenticode and Sigstore only as a marked hook. Steps: 11.1 installer (done 2026-10-04:
   single executable, LocalSystem service, verified live including an update from 0.1.0 to 0.1.1), 11.2 data
   format marker, backup, and --self-test (done 2026-10-04; self-test passed on a copy of the development host's real
   data), 11.3 manifest and download (done 2026-10-04; not yet against a real GitHub release), 11.4 hh-update helper (junction flip,
   rollback after two failed starts, recovery at boot; done 2026-10-04, verified live: 0.1.0 to 0.1.1, and a
   broken 0.1.2 rolled back to 0.1.1 in 10 s), 11.5 idle gate and maintenance window (done 2026-10-04; unit
   and API tests only until a real release exists), 11.6 API, tray, and client (done 2026-10-04: GET
   /host/update, POST /host/update/check, PUT /host/update/settings elevated, API 1.9.0; tray Updates and
   Update channel rows with Check now and Install now; client Updates panel), 11.7 release packaging
   (done 2026-10-04; the workflow has not run on GitHub yet), 11.8 live test against a published release.
12a. Setup profiles: capture and pickers (done 2026-10-05, API 1.12.0). YAML profiles per
   User (install: winget/Store ids or aliases; remove: Appx, capabilities, features; tweaks; browser with extensions
   and policies), JSON Schema, curated catalogs, Appx inventory with clean baselines, winget search, extension
   resolver, capture from a VM, and the client's editor, pickers, and capture review. Golden images are later (the
   Appx route takes ?source=vm so another source fits).
12b. Apply setup profiles. Part 1 (done 2026-10-05, API 1.13.0): a setup profile chosen in
   the create dialog (install.setupProfileId, Windows only, snapshotted into the install record, data format 2) is
   applied by the install watcher after the account is set up (state applyingProfile; baseline recorded first;
   SetupProfilePlanner, SetupProfileApplication, PowerShellDirectProfileApplier: packages, removals, settings), with
   one restart if needed, then Ready with setupResult. Verified live 2026-10-05: the applier on HyperHarbor-Test, and
   create to Ready on HyperHarbor-Test2 (17 min, 7 items, no problems). Part 2 (done 2026-10-05, API 1.14.0): POST
   /vms/{vmId}/setup-profile starts an applySetupProfile job (SetupProfileJobs) on a running Windows VM; HKCU values also
   go to the User's account hive when it has a profile in the VM; restartIfNeeded uses GuestRestart (shared with the
   watcher); the job's setupResult carries restartPending. Client: VM menu Apply setup profile… (ApplySetupProfileDialog). Verified live 2026-10-05 (HH_PROFILE_EXISTING_LIVE): 7 items, the account hive written. No
   removal in the live runs needed a restart (MathRecognizer, SmbDirect), so the restart path is unit tested only.

13. Lean host (2026-10-07; API 1.17.0; code complete and unit tested, handlers
   partly verified on the development PC, never applied to it): the setup profile engine targets the host itself (profile
   `target: host`; sections services, startup, power, remove.programs, registry type absent) and the tray's Lean host
   page applies the shipped profiles/host-gaming.yaml: dry run required, restore point and registry export before
   the first apply, guards and a startup allowlist, an undo profile from the diff, idle memory and process count
   before and after, a monthly re-apply. See "Lean host" under Gotchas.

14. Camera sharing for concurrent Remote Desktop sessions (2026-10-08; client only, no
   API change; code complete, verified live with two VMs on the development PC, not yet committed or released): mstsc gives the
   physical camera to the first session only, so the client opens it once and gives each Windows VM session a virtual
   camera (Windows 11, MFCreateVirtualCamera) named "HyperHarbor Camera (VM name)"; the .rdp file redirects only that
   camera (camerastoredirect:s:<symbolic link>). Per-VM policy (frozen, blurred, or live while unfocused; share off;
   privacy shutter), physical-camera-in-use detection, one-time elevated "Set up camera sharing", orphan cleanup.
   Windows 10 keeps the physical camera for one VM. See "Camera sharing" under Gotchas.

15. A camera for the client PC's own applications (2026-10-08; client only): while any VM
   session has a virtual camera, "HyperHarbor Camera (This PC)" also exists, always live (no policy, shutter, or effect),
   and it is removed with the last VM camera. Windows has no default camera (apps pick their own), so the "make it the
   default while connected, then restore" idea cannot be done; presence only while connected is the approximation.

16. Client auto-update (2026-10-09; client and release pipeline, no API change; code written,
   unit tested, not yet run against a real release): tauri-plugin-updater with minisign-signed NSIS artifacts. The
   client checks 15 s after start and daily (update-settings.json: channel stable or beta, check automatically) and the
   sidebar's ClientUpdate.svelte offers "Install and restart", which always waits for the user and asks again when
   Remote Desktop or console windows are open. Portable copies only show the releases link. A stale camera DLL under
   Program Files is detected by SHA-256 against the bundled one and fixed through the existing elevated setup.
   Remaining: 16.x live test (two builds, a loopback https manifest or a real prerelease), then the first release.

17. Game ready section in the host window (2026-10-11; tray only, no API change; code
   complete and unit tested, not yet run against real controllers): cards for Wake-on-LAN (moved from the tray-less
   readiness report), Wake from controller (Host.Core/GameReady: ControllerClassifier, ControllerWakeEvaluator,
   LastWakeParser/WakeAttributor, GameReadyCards; Host.Service/GameReady/GameReadyService with the Test wake action),
   Fast startup, USB selective suspend, and a manual BIOS card; five Coming soon stubs. Tray IPC: GameReadyMessages.cs.

18. Console sign-in card in Game ready (2026-10-11; tray only; unit tested with fakes, never
   run against Windows): Host.Core/GameReady/ConsoleSignIn (ConsoleSignInService with undo on failure, ConsoleSignInStore
   DPAPI vault, WindowsLsaSecrets, WindowsConsoleSignInSystem). Not built: display scaling, the Steam desktop controller
   layout, and Host.Tray with a controller chord layer for the Console user (none of them exist yet, and the tray pipe admits
   only administrators); "Require controller chord to unlock" is a hint only.

19. Power mode card in Game ready (2026-10-11; tray only; unit tested with fakes, never run
   against Windows): Host.Core/GameReady/PowerMode (PowerModeService: in-place plan values with a recorded Previous,
   ScheduledSleepGate, ScheduledSleepService, PowerCostModel, idle load), Host.Service PowerScheduleTasks (the tasks
   "HyperHarbor\Console sleep" and "HyperHarbor\Console wake"; the sleep task runs hh-update.exe scheduled-sleep, which asks
   the service over the tray pipe), tray DisplayWake (XInput while the display is off, then one
   SetThreadExecutionState(ES_DISPLAY_REQUIRED)) and ForegroundGameReporter. Not built: Sleep now in the client (needs an
   API route) and the 10-foot screen (does not exist).

20. Ambient screen card in Game ready (2026-10-12; tray only; unit tested with fakes, never run
   against Windows): a Windows screensaver ("HyperHarbor Ambient.scr", a copy of the host exe in the install root, made by
   the service: InstallLayout.AmbientScreensaver, AmbientScreensaverFile) that Windows starts after IdleMinutes (default 5)
   and that shows webui route #/ambient (Ambient.svelte: black, clock once a minute, position jump every 5 minutes, optional
   host/VM/device widgets) in WebView2 with --disable-gpu. HostCommandLine runs it for a .scr process path or the "ambient"
   verb (Host.Tray/Ambient/AmbientScreensaverApp). The tray (AmbientCoordinator) sets HKCU SCRNSAVE.EXE and the timeout
   (AmbientScreensaverApplier, user's own settings backed up and restored, Group Policy respected), ends the screen on XInput
   change (AmbientDismissal: ignored while the display is off, 3 s grace after it comes on, soft lock chord gate), writes
   %LOCALAPPDATA%HyperHarborambientscreen.json for the page, and relays the screensaver state and the screen's measured
   CPU/GPU (job object accounting, GPU counters by pid; metrics.json) to the service (AmbientReportMessage). Timeout order
   (screensaver < display) is enforced in AmbientSettings.Validate, in the Power mode apply, and by a card check on the timeout
   Windows actually has. CEC: detection only (Pulse-Eight USB adapter). Not verified: whether Windows starts the saver while
   Steam Big Picture is open, ES_DISPLAY_REQUIRED on an already-off display, the saver under a standard Console user (the tray
   pipe admits only administrators, so the tray and the screen's data need the administrator session).

21. Backup and restore with Kopia (2026-10-12; in progress). Kopia 0.23.1 is pinned in tools/tools.json (hashes),
   downloaded by scripts/fetch-tools.ps1 into .tools, embedded in the host exe, and extracted beside the version's exe
   (KopiaToolLocator verifies the SHA-256 on every use). Done: 21.1 models, DPAPI vault (BackupSecretStore), config store,
   recovery key confirmation, data format 3; 21.2 Kopia wrapper (Host.Core/Backup/Kopia: runner seam, KopiaCli, KopiaOutput),
   RESTORE.md, target tester; 21.3 VM backup (VmBackupService: consistency planner, production checkpoint with a sweep that
   removes every new checkpoint, hard-linked staging, manifest), host state backup, history store, verified live on a
   throwaway VM (VmBackupLiveTests, HH_BACKUP_VM_LIVE=1 with HH_KOPIA_EXE). Done: 21.4 scheduler (BackupCoordinator, BackupSchedulerService), shutdown trigger with
   debounce, run conditions, power hold (via HostActivity); Done: 21.5 restore engine (Host.Core/Backup/Restore: importer, RestoreCoordinator, HostStateRestorer, DiskReplacer; file browse by mounting a VHDX is NOT built yet); Done: 21.6 API 1.18.0 (Host.Service/Api/BackupEndpoints, BackupMapping; BackupManagementService in Host.Core; one recovery key per target covers its VM and host repositories), client (BackupPanel and dialogs), tray Backups card with failure notifications (BackupNotifier), ambient line; also done: the tray's Restore page (Host.Service/Tray/TrayRestoreWizard, IPC in RestoreWizardMessages.cs, webui pages/Restore.svelte: attach a location with its recovery key, list, restore host settings or a VM, no paired client needed), the tray's Back up now (BackupCoordinator.StartAllManual), and snapshot browsing (API 1.22.0: Host.Core/Backup/Restore/SnapshotBrowser restores a snapshot into a .hh-browse-* work folder, mounts its VHDX read-only with Mount-DiskImage (PowerShellVhdMounter), never follows reparse points (BrowsePaths), one session, 30 min idle limit; the real mount is covered only by the opt-in HH_BROWSE_LIVE=1 test, which needs an elevated shell and has not been run); Done: 21.7 monthly restore test (API 1.20.0; Host.Core/Backup/Restore/RestoreTesting.cs: RestoreTestService restores each VM's latest good backup as HH-RestoreTest-<id> with RestoreVmRequest.Isolated (no adapter connected) and Start, passes only when the guest reports in (RestoreVmResult.GuestReported), then always turns off and deletes the throwaway VM and its folder, retrying the delete because TurnOff can return before the VM is off; results in restore-tests.json, settings in restore-test.json; GPU-partition VMs and low disk space are skipped; the tray treats a VM's latest failed test as a failed backup item; verified live on HyperHarbor-BackupTest with HH_BACKUP_VM_LIVE=1, where the VM has no OS so the expected result is Failed with the guest not reporting in; a passing boot is covered by unit tests only). rclone targets are deferred.

22. Game-aware mode (2026-10-13; API 1.19.0; unit and API tested with Hyper-V mocked, never run
   against a real game): the tray's ForegroundProbe (Host.Tray/GameAware) sends ForegroundSignals every 2 s; the service's
   GameAwareMonitor (Host.Core/GameAware) combines them (GamingDetector: SHQueryUserNotificationState 3 or 4, borderless window
   plus a launcher in the lineage, allowlist, ignore list, launcher and own-window exclusions), smooths them (GamingDebouncer:
   3 s in, 10 s out), feeds ForegroundGameTracker (sleep and backup gates), and drives GameAwareController (per-VM Pause, Save,
   or Keep through GamePolicyPlanner; GPU-P forces Pause; VMs with a TCP-visible session are skipped unless AlwaysPause; the
   changed VMs are kept in game-aware-changed.json and resumed on exit, on a 60 s watchdog, and at host start). Off by
   default. UI: client GameAwarePanel and GamingNotice, host window Game-aware page, ambient line. Not built: a Game ready
   card (the page is its own nav item), configurable launcher list, per-VM Pause through the API's VmAction.

23. Tailscale, stage one (use an installed Tailscale client) (2026-10-14; API 1.23.0; unit and API
   tested with the local API mocked, never run against a real tailnet): Host.Core/Tailscale (TailscaleStatusParser for
   `status --json` and whois, TailscaleHttpLocalApi on the configurable port, TailscaleCliLocalApi fallback,
   TailscaleStatusProvider with caches, TailscaleNodeVerifier, HostTailscaleBuilder, tailscale.json settings). The device record
   keeps the Tailscale node it paired or first connected from (PairedDevice.TailscaleNodeId); PairedDeviceAuthenticationHandler
   refuses a request over 100.64.0.0/10 or fd7a:115c:a1e0::/48 from another node (403 tailscaleNodeMismatch) or when whois
   cannot answer for a tied device (tailscaleNodeUnverified). GET /host/tailscale, PUT /host/tailscale/settings (elevated),
   POST /wake/relay (Host.Core/Wake/WakeRelay: only the host's own networks, broadcast computed locally). Tray: the Tailscale
   card (Overview and Game ready, Copy policy entry, port and control URL; the tag is never applied by HyperHarbor, because `tailscale up --advertise-tags` with a tag the policy does not allow signs a signed-in PC out (seen live on 1.104.1, and `tailscale set` has no --advertise-tags there)). Client (src-tauri/src/tailnet.rs): the same
   status reading on this PC, TailnetService scanning peers every 30 s (tagged first, ProbeCache), merge_tailnet into the host
   list, order_candidates (LAN first, a recent Tailscale success stays first), describe_route (Direct or Relayed and the
   split tunneling hint), rank_wake_relays (used by wake_host when this PC is not on the host's LAN), build_reachability,
   TailscalePanel.svelte. Discovery identifies a peer by the TLS certificate it presents (compared with the pinned one), so no
   anonymous host route was added. Not done: Windows named-pipe local API (the command line covers it), a tailnet ACL or
   tag setup helper, stage two (an embedded Tailscale), and a live run against a real tailnet.

24. VM transfer between hosts (2026-10-21; in progress): move or copy an off VM host to host (the source pushes over mTLS with a
   one-time ticket the target issues; the client only authorizes). A move keeps the VM ID and MACs; a copy gets a new ID and
   new dynamic MACs. Done: 24.1 spike (Host.Tests/Transfer; verified on the laptop, Windows 11 Pro, 2026-10-21):
   - PeerHostCertificateSpikeTests: a host certificate with only the Server Authentication purpose reaches a peer Kestrel
     (AllowCertificate) as the client certificate, both through LocalCertificateSelectionCallback and through
     SslOptions.ClientCertificates.
   - TransferSpikeLiveTests (HH_TRANSFER_SPIKE_LIVE=1, elevated): ExportSystemDefinition with CopyVmStorage=false,
     CopyVmRuntimeInformation=false, CopySnapshotConfiguration=1 writes only .vmcx, .vmgs, and .VMRS. ImportSystemDefinition
     copies them into Hyper-V's default folder as a planned system ("Planned Virtual Machines"), so the staging folder can be
     deleted right after RealizePlannedSystem. On the planned system, ModifySystemSettings accepts ElementName,
     AutomaticStartupAction, and the Configuration, Snapshot, SwapFile, Suspend, and Log data roots, but REFUSES
     GuestStateDataRoot (0x80041008); the .vmgs follows ConfigurationDataRoot on realize. Disk HostResource, DVD removal, and
     StaticMacAddress plus Address are changed with ModifyResourceSettings/RemoveResourceSettings before ValidatePlannedSystem.
   - vTPM: Grant-HgsKeyProtectorAccess with a guardian imported from Export-HgsGuardian (public certificates only, imported
     with -AllowUntrustedRoot), then SetKeyProtector, works on a VM that already has TPM state; the VM then starts with only the
     granted guardian's private keys present (the owner's were deleted). SetKeyProtector with the saved bytes restores the
     original exactly. GetKeyProtector after the first start differs from the bytes set (Hyper-V rewrites it), so save it
     with GetKeyProtector, never from what was set. Supported config versions: Msvm_VirtualSystemSettingData with InstanceID
     LIKE 'Microsoft:Definition\\VirtualSystem\\Version\\%' (8.0 to 12.0 on the laptop). Hyper-V will not start a VM whose
     DVD holds a file that is not a real image (0x80070570); the spike builds one with SeedIso.
   Done: 24.2 delete cleanup (2026-10-21): VmDeletionService.ForgetRecords also removes the VM's provisioned accounts
   (ProvisioningStore.Remove), game-aware policy, and backup policies; a record that cannot be removed is logged only.
   Done: 24.3 core read and preflight (2026-10-21; unit tested only, not wired into the API yet): Host.Core/Transfer.
   TransferPreviewService (source) reads the VM and lists TransferBlockers (NotOff, SavedState, Checkpoints, DifferencingDisk,
   SharedDisk, DiskMissing, InstallInProgress) and warnings (GPU partition, attached image). TransferPreflight (target, pure)
   turns a preview plus TransferTargetFacts into a TransferPlan: errors (existing ID on a move, unsupported config version,
   compute through VmSettingsValidator, free space with a 1 GB margin, name via VmCreationService.CheckName, disk file names
   that are not plain .vhdx/.vhd), the name ("(moved)" or "(copy)" through RestoreNaming), the VM's own folder under
   VmStorageLocation.RootFolder or Hyper-V's default configuration folder, disk targets (numbered on clashes), switches via
   SwitchMapper, and static MACs for a move. TransferPreflightService gathers the facts. CimHyperVVersions reads a VM's
   Version and the host's supported versions (HostVersionCapabilities); IDiskFiles gained SizeBytes. Secure Boot templates
   are not checked (not queryable through CIM); ValidatePlannedSystem is the backstop.
   Done: 24.4 CIM wrappers (2026-10-21; verified live on the laptop with TransferCimLiveTests, HH_TRANSFER_CIM_LIVE=1, elevated):
   IHyperVTransfer/CimHyperVTransfer (ExportAsync, ImportPlannedAsync, FixPlannedAsync, Validate, Realize, DeletePlanned,
   SetAutomaticStartupActionAsync; Fix returns the original AutomaticStartupAction and sets None until the transfer
   completes; adapters are matched in Hyper-V's order because a copy's planned adapters read 000000000000; an unplaced or
   unknown disk is a TransferException). IKeyProtectorAccess/CimKeyProtectorAccess call the HGS classes directly
   (MSFT_HgsGuardian Export, Import, Remove; MSFT_HgsKeyProtector ConvertToByRawBytes, Grant; CmdletOutput holds the
   result, ReturnValue is signed), and HyperV/HgsGuardians holds the shared lookup that CimHyperVBuilder now uses.
   KeyProtectorHandover grants the peer ("HyperHarbor peer <hostId>") around one step and always sets the original back
   (not cancelable), reads it back, and removes the guardian entry. Remove-HgsGuardian never deletes certificates (seen in
   the spike), which is why removing a peer is safe on one PC; the code refuses to remove UntrustedGuardian. The live test
   found CimHyperVDefinitionReader.SwitchIdOf matching CreationClassName (fixed in its own commit).
   Done: 24.5 receive side (2026-10-21; unit tested with fakes, not wired into the API yet): TransferReceiver and
   IncomingTransferStore (transfers.json through ProtectedFile; no ticket in it). OfferAsync runs the preflight (errors 400,
   unacknowledged warnings 409, a second move of the same VM 409), creates the VM's own folder with its staging folder
   inside (.hh-transfer), exports the local guardian when the VM has a TPM, and issues a 256-bit ticket kept only as a
   SHA-256 hash in memory. Authenticate needs that ticket plus the source certificate's fingerprint (fixed-time
   comparisons) and slides a 30-minute idle timeout. Files are "disk:<index>" (sizes fixed by the preview) or
   "definition:<VM GUID>.vmcx|vmgs|vmrs" (declared by their first block; at most 8, 1 GB each, never empty). Each block is
   exactly BlockSize (256 MiB) or the file's rest, starts at the verified offset (else TransferOffsetException with the
   expected offset), and is hashed while written to <file>.partial, flushed to disk, and only then counted; a bad block
   is truncated away. ImportAsync: Importing is saved first, then the planned ID, then the original startup action; any
   failure removes the planned or realized VM and returns to Receiving with the files kept. Rules: before Imported,
   cancel, expiry, or a restart rolls back (VM removed, then only the VM's own folder deleted); after Imported, cancel and
   expiry complete instead (startup action restored), so this host never deletes an imported VM. ExpireIdleAsync also
   ends transfers whose ticket a restart lost, so the service must call it at startup and periodically (phase 7).
   Done: 24.6 send side (2026-10-21; unit tested, the sender end to end against a real TransferReceiver in process; not wired
   into the API yet): TransferSender.RunAsync(TransferSendRequest, report, token) is the work of the transfer job (the
   TransferVm job kind waits for the contract change in phase 7). It re-previews, opens each disk FileShare.Read (keeps the
   VM from starting), exports (inside KeyProtectorHandover when the VM has a TPM; the original key protector is saved to
   OutgoingTransferStore, outgoing-transfers.json through ProtectedFile, before the grant, and RecoverAsync takes a grant
   back after a crash), sends definition files then disks in blocks hashed with RandomAccess (two reads per block, no 256 MiB
   buffer; since 2026-10-22 the next block is hashed while the current one is sent), resumes from the receiver's count after PeerTransportException (5 in a row give up), imports with retries (the
   receiver answers a repeated import with the same VM), then, no longer cancelable, deletes a moved VM through
   VmDeletionService.DeleteAsync (the in-job form of a delete; a failure is a result warning, never a failed job) and
   completes. Any failure before the import cancels the receiver. VmJobStore.Cancel(jobId, userId) signals a job's
   Stopping token; a canceled job ends Failed with ErrorTitle "<step> canceled" and VmJobStore.CanceledDetail.
   PeerHostClient opens IPeerTransferChannel over HTTPS/1.1: pins the target by SHA-256 of its certificate (a mismatch is a
   TransferException, never retried), presents the host certificate through LocalCertificateSelectionCallback, tries each
   address (re-probing after a network failure; a block goes only to an address that just answered). Wire format for the
   phase 7 routes: Authorization "HyperHarbor-Transfer <ticket>"; GET /transfers/{id} -> {state, files[{key, size,
   received}]}; PUT /transfers/{id}/files/{key} (key URL-escaped, e.g. disk:0) with Content-Range "bytes a-b/size" and
   Content-Digest "sha-256=:<base64>:"; 409 problem code transferOffset with expectedOffset; POST .../import -> {vmId,
   name}; POST .../complete; DELETE /transfers/{id}; 401 means the ticket ended; 502/503/504 are retried.
   Done: 24.7 API 1.24.0 (2026-10-21; API tested, including a copy from a TestHost to itself through the real job, the real
   HttpPeerTransferChannel, and the real ticket routes in memory; not yet between two hosts): Host.Service/Api/TransferEndpoints
   (getTransferPreview; startTransfer and offerIncomingTransfer elevated and audited, the ticket never in the audit summary;
   cancelTransfer; the ticket routes getIncomingTransfer, receiveTransferBlock (416 without Content-Range, 400 without
   Content-Digest, body limit lifted to one block), importTransfer, completeTransfer, cancelIncomingTransfer), TransferMapping
   (Host.Core records to Shared.Contracts.Transfers DTOs; the alias is Domain because "Core" resolves to the namespace),
   Security/TransferTicketAuthenticationHandler (scheme and policy "TransferTicket"; only the ticket routes use the policy, so
   a ticket opens nothing else and a paired device's certificate gets 401 there; claims carry the receiving User and the
   transfer; the audit names the caller "peer host (transfer ticket) <id>"), Transfer/TransferMaintenanceService
   (RecoverAsync, then ExpireIdleAsync every minute), Core TransferJobs (validation, confirmName for a move, VmJobKind.TransferVm,
   VmJob.transferResult; JobEndpoints.ToContract must copy every result field, which the API test caught). TransferMode,
   TransferBlockerCode, and IncomingTransferState moved to Shared.Contracts.Vms; the DTOs live in Shared.Contracts.Transfers so
   their names do not clash with Host.Core.Transfer's. HostActivity counts TransferReceiver.AnyActive. TestHost fakes the
   definition reader, versions, Hyper-V transfer, key protectors, and the peer channel factory; tests that offer a transfer
   must set HostSettingsStore.SetVmFolder first, or the receiver creates folders under the fake default C:\Hyper-V\Config.
   EndpointSecurityTests.TicketEndpoints_MatchContract keys on operations whose security lists only transferTicket.
   Done: 24.8 records (2026-10-21; unit and API tested): Core TransferRecords (accounts, admin GuestCredential, GameVmPolicy;
   ToString hides the credential) and TransferRecordService (Collect on the sender; Apply on the receiver, matching each
   account to the User whose VmAccountName equals it, else a warning). POST /transfers/{id}/records (sendTransferRecords,
   ticket; audit summary counts only) keeps them in the receiver's Session, in memory only (they hold a credential; a restart
   rolls the transfer back anyway); ImportAsync applies them under the final VM ID (a copy's new one) right after realize, a
   failure there is a warning, and TransferImportResult/TransferImported carry the warnings into the job's transferResult.
   The sender sends them just before the import. Not sent, on purpose: Performance mode (GPU partition removed; preview warns),
   backup policies (TransferPreviewService warns when the VM has one), and a finished unattended install record.
   ProvisioningStore.ForVm; GameAwareService.ToWire/ToCore are public for the mapping (TransferRecordsWire).
   Done: 24.9 client (2026-10-21; Vitest, Rust unit tests, svelte-check; not yet clicked through against two hosts):
   TransferVmDialog.svelte (VM menu "Move or copy to another host…" for off VMs, page gate hostKnownToSupport(host,
   TransferApiVersion), which unlike hostSupports refuses an unknown apiVersion). Flow: get_transfer_preview on the source,
   offer_transfer (withElevation on the target; a resourceWarnings 409 shows the issues and "Continue anyway" asks again with
   acknowledgeWarnings), start_transfer (withElevation on the source), waitForJob on the source, Cancel transfer
   (cancel_transfer), then carry_vm_settings. Rust (lib.rs): offer_transfer reads both pinned fingerprints, sends the source's
   as sourceFingerprint, keeps the offer's ticket (Zeroizing) and guardian in AppState.transfers and strips them from what
   the webview gets; start_transfer builds TransferTarget from the target's paired record (hostId, display name, its
   HostEntry addresses and port, pinned fingerprint) and sends the kept ticket; forget_transfer_offer drops an abandoned offer
   (the target ends it after 30 idle minutes, since only the source can cancel it there). carry_vm_settings uses
   MonitorChoiceStore::carry and CameraService::carry_prefs: a move moves the settings to "<target host id>/<vm id>", a
   copy duplicates them under the copy's ID. svelte-autofixer (command-line form) found no issues in the dialog or VmList.
   Done: 24.10 two-PC test, move laptop to the desktop (2026-10-21): same ID, MAC, and VHDX hash, about 17 s. It found:
   - Only a key protector's OWNER can grant (Grant re-signs with the owner's signing key; elsewhere "Unable to find the
     signing ... certificate"). So a moved VM can go back to its owner but not on to a third host with its TPM.
   - The desktop failed Enable-VMTPM ("client configuration settings could not be retrieved") and opening a granted key ("Not
     implemented 0x80004001"). NOT a Windows limitation: loader snaps (IFEO GlobalFlag 0x2 on a renamed rundll32 under a tiny
     debugger) showed vmhgs.dll importing "EventUnregkster", a single flipped bit in the cached in-memory image (the file on
     disk was intact), so the ext-ms-win-hyperv-hgs API set never loaded and vmms reported E_NOTIMPL from
     hostguardianserviceextensionwrapper.cpp. After a reboot the product's TPM probe passed both checks on 26200. Owners could
     still open keys because that path did not need the failing load in the same way. Never remove UntrustedGuardian or the
     Shielded VM Local Certificates there.
   - A locked TPM cannot be reset in place (a new key protector gives "authentication tag did not match"; the config is
     encrypted too), so a fresh TPM means defining the VM anew (new ID).
   - Windows 11 guests leave an automatic checkpoint after a failed start, which blocks a transfer.
   Done: 24.11 TPM modes and merge (2026-10-21; verified live between the laptop and the desktop: merge then move keeping the TPM,
   the move back to the owner, a move with a fresh TPM (new ID, same static MAC), the owner (the desktop) granting on the way back,
   and a copy with a fresh TPM; each VM started on arrival): KeyProtectorHolders (owner and
   guardian IDs = SHA-256 hex of the encryption certificate, from ConvertToByRawBytes) in the preview; TransferPreflight
   .DecideTpm: keep = owner here (no grant, no probe), or already a holder, or the source owns it (grant); each but the
   first needs TpmCapability.CanOpenGrantedKeys; fresh needs CanEnableTpm. PowerShellTpmCapabilityProbe (TpmCapability.cs)
   creates a throwaway 32 MB VM with a temporary owner guardian, Enable-VMTPM, Start-VM, and removes everything; cached per
   process. TransferTpmMode.Fresh: the sender exports nothing (disks only) and the receiver's RebuildAsync defines the VM
   with IHyperVImporter (ImportAdapter.StaticMacAddress for a move) and EnableTpmAsync. A null guardian from the target now
   means "export without a grant". POST /vms/{vmId}/checkpoints/merge (CheckpointMergeService, job mergeCheckpoints); the
   dialog shows Merge checkpoints when checkpoints are the only blocker, and an "Its TPM" choice for TPM VMs.
   Done: 24.12 transfer speed and the TPM check (2026-10-22): the first real copy (57 GB, the desktop to the laptop)
   went to the laptop's Wi-Fi address at under 68 MB/s and broke when the Wi-Fi dropped. The offer now carries the
   receiving host's own addresses (TransferAddresses.Rank over INetworkAdapterReader: connected with a default route, wired
   before wireless, then link speed; Wi-Fi reported 2.4 Gbps against Ethernet's 1 Gbps there, and vEthernet adapters report
   NdisPhysicalMedium 0), API 1.25.0, and the client's transfer_addresses puts them before its own. The TPM check
   (PowerShellTpmCapabilityProbe) runs detached from its callers: the client's 10 s request timeout canceled it halfway,
   leaving its VM running and nothing cached; the client now allows the offer 3 minutes.

25. Profile builder machine view (2026-10-22; client only, no API change). The setup profile editor is a terminal-style
   workspace (Dialog terminal: larger than xl, dark CRT variables): a vertical section list (tabs with counts) drives both
   the wireframe machine in the middle and the form panel on the right (Install packages, Remove debloat, Browser
   browser, Tweaks overview); a HUD line under the machine names the section, its counts, and why it is a still.
   Green terminal and Motion checkboxes are stored per device (hh.machinePalette, hh.machineMotion); Motion starts
   from prefers-reduced-motion and, once set, overrides it (MachineView motion: system, on, off). The first version
   sat beside the old tab strip on the light dialog and, on a PC with Windows animation effects off, never moved. Only perItem has a control there (one drive per package); blink, pulse, and spin follow
   ControlState fields that no editor sets yet (TPM, backup schedules, and performance mode live in other dialogs).
   Fallback: exploded.svg from build_model.py when WebGL is missing, the scene fails to start, or FrameRateWatch
   sees under 30 fps for 5 s; it stays the still for the dialog's life. Reduced motion: cuts, no orbit, no fan spin, no
   CRT flicker (blink and pulse stay, as status). Verified in headless Edge with SwiftShader WebGL (overview, gpu with
   spin, firmware, green palette) and with --disable-3d-apis (the still). The GPU fans gained blades: a plain ring
   spinning about its own axis looks still; viewer.html only appeared to spin them because it rotated world-space
   lines about the origin.
26. Machine profiles (planned 2026-10-22 with the user; in progress). One document that describes a whole VM, edited in
   the terminal editor with one section per part of the wireframe machine. Decisions:
   - One self-contained document, not references: account (today's unattended install profile fields), display,
     firmware (TPM), compute, GPU (Performance mode), network (switch, MAC spoofing), backup (schedule, retention,
     triggers), and the software sections (install, remove, tweaks, browser). Setup profiles and unattended install
     profiles become legacy, with a migration into machine profiles.
   - Applies at VM creation (one job chain: create, unattended install, setup, performance, backup policy) and to an
     existing VM, with a preview of what changes and the off-VM requirements for hardware.
   - Display holds both guest settings (resolution, scaling, dark mode, applied in the guest) and connection defaults
     (monitors, full screen or windowed), which a client may still override per device.
   - Discovery: one scroll area per pane with a pinned filter box and category chips over the whole curated catalog
     (winget search as the fallback group), the profile's items as a chip strip, and a detail drawer (package
     descriptions; the registry values a tweak writes).
   Done: 26a discovery (2026-10-22; API 1.26.0): PackageCatalogItem.description (catalogs/packages.yaml),
   TweakCatalogItem.values (TweakCatalogValue with the clean-install default), SetupProfileCatalog.appx
   (AppxCatalogItem from catalogs/appx.yaml). Client: CatalogBrowser (pinned filter and chips, counts follow the
   filter), ProfileChips, DetailCard on the machine stage, and InstallSection, RemoveSection, TweaksSection,
   BrowserSection replace PackagePicker, AppxChecklist, and ExtensionPicker. Verified in a browser preview with the
   repository catalogs; not yet against a live host.
   The document format, API, migration, and apply design for 26b to 26d: [machine-profiles.md](machine-profiles.md).
   Done: 26b machine profiles (2026-10-23; API 1.27.0). Host: MachineProfile contracts, MachineProfileYaml (machine
   sections through the contract JSON with unknown keys refused; software sections through ProfileYamlWriter.AppendBody
   and the reader's helpers), MachineProfileValidator (UnattendProfileStore.Normalize for account, ProfileValidator for
   software, hardware rules), MachineProfileStore, /machine-profiles routes. Client: the Tauri profile commands take a
   kind (setup or machine); ProfileWorkspace (the terminal shell), SoftwareDraft and SoftwarePanel (shared software
   sections), MachineDraft, the seven section components, MachineProfiles (list), Convert on setup profiles.
   Verified with unit and API tests and a browser preview of every section; not yet against a live host. Not done in
   26b: wallpaper image upload, retention and conditions overrides for backups (the target's apply), and anything
   that applies a machine profile (26c, 26d).
   Done: 26c create a VM from a machine profile (2026-10-23; API 1.28.0). MachineProfileVmCreation reads the profile
   against the host (switch and GPU by name are errors when missing, a backup target is a warning; empty compute takes
   the Create VM dialog's defaults; Performance mode forces fixed memory), turns the account into an UnattendProfile
   through UnattendProfileStore.Normalize ("machine-<id>") and the software plus display.guest into the setup
   profile snapshot (theme: appearance.darkMode or the light values; accent: DWM AccentColor and Explorer\Accent
   AccentColorMenu as ABGR; wallpaper color: Control Panel\Colors Background and an empty Desktop WallPaper), and
   calls VmCreationService with CreationExtras (UnattendedSetup.PlanFor instead of profile IDs, SecureBoot on the
   blueprint, the profile's warnings, and a Configure step after the TPM: nested virtualization and MAC spoofing,
   VmPerformanceService.ApplyWithinJobAsync, IMachineBackupPolicies). The install record's GuestPerformanceSetup
   makes the watcher start the guest setup job (IGuestPerformanceStarter) once ready. Unit and API tested with fakes;
   run live afterwards (below). Not verified: the wallpaper color values in a real guest. Not done: connection defaults (display.connection) do not
   reach the client yet, and wallpaper images.
   Live (2026-10-23, laptop, MachineProfileLiveTests through the harness, Windows 11 26300 ISO, Intel iGPU at 25%):
   the createVm job made the VM with the TPM, Secure Boot, 2 processors, 4 GB fixed, nested virtualization, and the
   GPU partition, and Windows Setup ran to Ready in about 9 minutes with the partition attached from the start. The
   watcher applied the setup profile snapshot (4 items, no problems) and started the guest Performance setup. Found:
   the test's guest check used Select-Object on a hashtable (empty values); a turnOff right after Ready is refused while
   the guest setup job holds the VM, so the test retries until the VM is off.
   Done: 26d apply to an existing VM (2026-10-23; API 1.29.0). MachineProfileApplier builds one plan for the preview and
   the job: compute (processors, memory, nested virtualization; left to Performance mode when that is applied), MAC
   spoofing, a TPM added (never removed), Performance mode on, re-shared, or off, the backup policy, and the software
   plus display.guest snapshot; notes for account, disk size, switch, Secure Boot, connection defaults, and turning
   backups off. The applyMachineProfile job sets the policy, shuts the VM down when a chosen part needs it off (only
   with shutDownToApply), applies, starts it again, waits for Remote Desktop (GuestRestart.WaitUntilAnswersAsync),
   applies the software with one restart if needed, and runs the guest Performance setup inline
   (VmPerformanceService.GuestSetupWithinJobAsync, since the job holds the VM). Problems are the job's setupResult.
   Live (2026-10-23, laptop): a full MachineProfileLiveTests run (HH_MACHINE_GPU=1, HH_MACHINE_KEEP=1) made the VM,
   reached Ready, wrote the theme, accent, and tweak values, and the watcher's guest Performance setup copied the Intel
   driver (32.0.101.8860, no drift, no reboot): the first live run of the guest setup. MachineProfileApplyLiveTests on
   that VM then shut it down, went from 2 to 3 processors, started it, waited for Remote Desktop, wrote the setting
   (Hidden = 1 in the Default hive), reported no problems, and deleted the VM. Found: a string returned by
   Invoke-Command carries remoting properties into ConvertTo-Json, so the test check now interpolates it.
   Stages: 26a discovery for the software sections (client plus catalog fields, API 1.26.0); 26b the machine profile
   contract, host store, migration, and the editor's eleven sections; 26c create a VM from a machine profile; 26d apply
   to an existing VM.

v2 (paid tier, not in MVP): per-user accounts with roles and SSO mapping. Also out of MVP scope:
per-device VM accounts and a user management UI.
