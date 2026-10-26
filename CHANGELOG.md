# Changelog

All notable changes to HyperHarbor (host and client) are recorded here, newest first. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/). Host and client share one version number
(Directory.Build.props and client/src-tauri/tauri.conf.json); the API version (docs/api.yaml info.version)
is noted where it changes.

Sections: Added, Changed, Fixed, Removed, Security. Write entries for the people who use HyperHarbor, and
note anything that needs action, such as updating the host, a new elevation requirement, or a data migration.

## [Unreleased]

### Changed
- Remote Desktop windows the client opens are titled with the VM's name instead of only its IP address, so several
  sessions are easy to tell apart in the taskbar ("Dev Box - 192.168.0.50 - Remote Desktop Connection"). Console
  windows read "<VM name> (console)", and a maintenance session on the host uses the host's name.

## [0.2.3] - 2026-10-25 (API 1.35.0)

Two fixes in the client and a clearer name on the host. Adding a backup target no longer waits forever on a
passphrase prompt hidden behind it, and the host window's folder for disk exports no longer calls itself "VM backups".
No API change; the host updates itself to this version when it is on the stable channel with automatic updates on, and
installed clients update at their next check.

### Changed
- The host window's "VM backups" folder is now called "Disk exports", because it never held VM backups: it is where
  copies of a stopped VM's disks go (for example before turning on Performance mode) when the client does not choose
  a folder. VM backups go to the backup targets you add from a client (Host menu, Backups). Its default is now
  "HyperHarbor Disk Exports" in Public Documents; exports already made in "HyperHarbor Backups" stay there, and a
  folder you chose yourself is kept.

### Fixed
- Adding a backup target (and any other step from the Backups panel that needs the admin passphrase) no longer sits
  on "Working…" forever: the passphrase prompt opened behind the target dialog, where it could not be seen. The prompt
  now always opens above other dialogs.

## [0.2.2] - 2026-10-25 (API 1.35.0)

Administrator access in Windows VMs. The account you connect with is a standard user; now you can ask for a short
administrator window from the client and type a one-time passphrase into the Windows administrator prompt. The client
also draws its own title bar, and unattended Windows installs and host updates recover from two failures that used to
stop them. Update the host for administrator access (API 1.35.0): with an older host the menu entry stays hidden.
Hosts updating from 0.2.1 can rarely fail to prepare this update; see Fixed. The host updates itself to this version
when it is on the stable channel with automatic updates on; installed clients update at their next check.

### Added
- Administrator access in Windows VMs. The account you connect with is a standard user, so it cannot answer a
  Windows administrator prompt (installing software, changing settings). Choose Administrator access… in a running
  VM's menu and enter the admin passphrase: the client shows your HyperHarbor administrator account in that VM
  (hha-owner, named after you) and a one-time passphrase of three words and a digit, with a countdown. The
  passphrase also shows in a small card at the top right of your screen that stays above every window, a full-screen
  Remote Desktop session included, so you can read it while you type. In the Windows prompt choose More choices,
  pick that account, and type the passphrase. Access ends after 5 minutes, when you choose Done, or when the VM
  stops; the host then disables the account again and the card goes away. While access is open, the VM's row shows
  "Admin until" and its menu offers End administrator access, also after the client restarts. Done closes the dialog
  at once; if the host cannot be reached, a message says access still ends by itself. The account is created the first
  time you ask, cannot sign in over the network or Remote Desktop (only prompts and the VM's console), and the
  passphrase is never stored or put on the clipboard. Windows guests only; Linux VMs keep using sudo. Needs a host
  with API 1.35.0. The length is a host setting from 2 to 30 minutes (PUT /host/guest-admin/settings).
- API 1.35.0 (host): administrator windows in Windows guests, a one-time passphrase for the User's own guest
  administrator account (hha-owner) to answer a UAC prompt. POST, GET, and DELETE /vms/{vmId}/guest-admin, PUT /host/guest-admin/settings, and the problem codes
  vmNotProvisioned, guestAdminUnsupported, and guestUnavailable.

### Changed
- The client draws its own title bar: the HyperHarbor name over the sidebar and minimize, maximize, and close
  buttons in the app's colors, in light and dark mode, instead of the separate Windows title bar. Drag the bar to
  move the window and double-click it to maximize; dialogs open below it, so the window can still be moved or
  closed with one open. Windows 11's snap layout menu no longer appears when you hover over maximize; Win+Z and
  dragging the window to a screen edge still snap it.

### Fixed
- Creating a Windows VM with an unattended install no longer stops for good at the "No operating system was loaded"
  screen when the VM resets before Windows Setup has written to the disk and misses the "Press any key to boot from CD
  or DVD" prompt: the host now restarts it and presses the key again (up to three times), and otherwise fails the
  install with an explanation instead of waiting out the three-hour timeout.
- A host update could fail to prepare if the host was downloading its package index at the same moment: the copy of
  the data folder that tests the new version met the download in progress, and the host then skipped that version
  until it restarted. The copy now leaves the package index out (it is a cache the host downloads again).
  Action needed: the update to this version is still prepared by the host's current version, which has the old
  copy, so it can rarely fail the same way. If a host's Updates panel stays on 0.2.1 with a "being used by another
  process" message, restart the host service (or the PC) and it prepares the update again. Checking for updates
  again does not retry the same version.
- Scrollbars in the client, such as in the Create VM dialog and the sidebar, are thin and follow the light or dark
  theme instead of the bright default ones.
- The Create VM dialog for a machine profile fits the client's default window size without scrolling.
- The "In library" badge on the ISO library and Profiles tabs no longer breaks across two lines in a narrow name cell.

## [0.2.1] - 2026-10-23 (API 1.34.0)

Machine profiles and the Library. One kind of profile now describes a whole VM (hardware, account, display, backups,
and software) and can create a VM or be applied to one; the host converts your install and setup profiles into it
when it updates (data format 4). The new Library on the client device keeps ISOs and profiles and sends them to any
paired host, and package search now works on any host with internet access, with nothing to set up. Update the host
for these: with an older host, the client shows the earlier tabs and dialogs. The host updates itself to this
version when it is on the stable channel with automatic updates on; installed clients update at their next check.

### Added
- The setup profile editor has a dark terminal look built around a wireframe model of a PC: the section list on the
  left, the model in the middle, and the section's form on the right. Choosing a section flies the camera to its part
  (the drive bay for Install and Remove, the browser tabs on the screen for Browser) and lights it up, and each
  package you add lights one more drive. A line under the model names the section and its counts. A Motion check box
  beside Cancel and Save turns motion on or off; it starts from the Windows animation
  effects setting: with it off, the camera cuts instead of flying and does not orbit, until you check Motion. Without
  WebGL, or when the view stays under 30 frames per second for five seconds, it shows a still exploded drawing with
  the same highlights instead, and says why. The editor fills the window, with thin scrollbars, fields, and buttons
  that glow in the terminal color; with Motion off its own animations (the section fade and the cursor) stop too.
- Finding things in the setup profile editor: each section is one list with a filter box and category chips that stay
  at the top, over the whole catalog (all packages, not only the popular ones), with one scroll bar per panel. What
  the profile holds shows as chips above the list. Selecting an entry opens its details on the model: a package's
  description, an app's rating, or the exact registry values a tweak writes, with a button to add or remove it.
  In Install, typing a winget id offers to add it as typed, and winget search shows its results in the same list.
  Remove lists HyperHarbor's rated built-in apps without a running VM (reading a VM still marks what it has), with
  Remove all safe; capabilities and optional features moved under a fold. Pasting a store link into the extension
  filter looks the extension up.

- Machine profiles: a new Machine profiles tab (hosts with API 1.27.0) for profiles that describe a whole VM in one
  YAML file. The editor has a section for each part of the wireframe machine, grouped as Identity (account, display),
  Hardware (firmware, compute, GPU, network), Care (backups), and Software (install, remove, tweaks, browser), and
  the model follows the settings: the TPM LED blinks with the TPM on, the GPU fans spin in Performance mode, and the
  backup LED pulses on a schedule. Settings left "as it is" keep a VM's current value. The account section can be
  filled from an unattended install profile and the software sections from a setup profile, and Convert on a setup
  profile makes a machine profile from it.
- Create VM… on a machine profile (hosts with API 1.28.0) makes a VM from it: choose a name, an image, and the
  edition. One job builds the VM with the profile's processors, memory, disk, switch, TPM, and Secure Boot, sets
  nested virtualization, MAC address spoofing, Performance mode, and the backup policy while it is still off, and
  starts the unattended install. When the install is ready, the profile's software, theme, accent color, and
  wallpaper color are applied, and for a profile with a GPU share the GPU driver is set up in the guest. Names the
  host does not have (a switch or GPU) stop the create; a missing backup target is a warning.
- Apply machine profile… in a VM's menu (hosts with API 1.29.0) compares a machine profile with the VM and lists each
  setting that differs, marked when it needs the VM off or runs in the guest, with notes for what applies only when
  creating a VM. Choose the parts to apply; with Shut down, apply, and start again, a running VM is shut down for
  the parts that need it off and started again, then the software and display settings are applied in the guest and,
  when Performance mode was turned on, the GPU driver is set up. A profile never removes a TPM, never shrinks a disk,
  and leaves Secure Boot and the switch as they are.
- A machine profile's Remote Desktop settings (Display, Connecting) now reach the client: Connect uses the profile's
  monitors (unless you chose monitors for that VM on this device), full screen or windowed, and whether the clipboard,
  printers, microphone, and camera are shared. VMs get them when created from a profile or when the profile is applied
  (the new Remote Desktop defaults part).
- A machine profile's Backups section can keep a different number of daily, weekly, and monthly backups than its
  target, and use other conditions for when a backup may start (wait while connected, wait while a game runs, a time
  window). A profile that would keep no backups at all is refused.
- Wallpaper images for machine profiles (hosts with API 1.31.0): in the Display section, choose Image, then pick one of
  your images on the host or Add image… from this device (JPEG, PNG, or BMP up to 20 MB). Creating a VM from the
  profile or applying it copies the image into the VM (C:\Users\Public\Pictures\HyperHarbor) and sets it as the
  wallpaper, filling the screen. A profile whose image was deleted is applied without it, with a warning.
- New VM now starts with your machine profiles (hosts with API 1.28.0): Use… on one continues in that profile's create
  dialog. Choosing every setting by hand still works below it, and installing with an unattended install profile and a
  setup profile is now labeled as using the older profiles. The setup profile list notes that machine profiles
  replace setup profiles and that Convert makes one.
- One place for profiles (hosts with API 1.32.0): the Profiles tab holds all your profiles, and the Install profiles
  and Setup profiles tabs are gone (your old profiles are converted when the host updates). New VM starts from one of
  your profiles or a template (Windows Workstation, Windows Burner, Ubuntu Dev Server), and its hardware (processors,
  memory, disk, network) can be changed for that VM without changing the profile. New profile can start from a
  template. Capture setup profile now saves a profile on the Profiles tab, and Apply machine profile replaces Apply
  setup profile. With an older host, the client shows the earlier tabs and dialogs.

- A Library on this device (sidebar, above the hosts) keeps installation images and machine profiles, with their
  wallpaper images, and sends them to any paired host. Save to library on a host's ISO library and Profiles tabs
  copies them here. Each item shows which hosts have a copy and whether it still matches: In sync, Library newer
  (Send), Changed on host (Update library), Both changed (keep either), or Missing on host (Send again). Saving an ISO
  from a host needs a host with API 1.33.0; a stopped download resumes where it stopped.
  + New profile and Edit on the Library page open the profile editor and save into the library; the editor uses a
  paired host's package, setting, and network lists (Catalogs from, when there are several), and the host checks the
  profile when it is sent.

- The package list searches winget as you type (hosts with API 1.34.0): what the catalog lacks shows under "From
  winget", with no button to press. Search matches names, ids, and tags, not descriptions.
- Extensions: typing something the catalog lacks offers Search the Chrome Web Store (and Search Edge Add-ons for
  Edge), which opens the store's search in your browser; paste the extension's link back to add it.
- Package search works on any host with internet access: nothing to set up first (PowerShell 7 and the WinGet
  module are no longer needed), and results come back in milliseconds.

### Security
- The client's cryptography dependencies use ctutils 0.4.5, which fixes non-constant-time comparisons
  (RUSTSEC-2026-0385).

### Changed
- Updating the host converts your install profiles and setup profiles into machine profiles (data format 4): an install
  profile becomes a machine profile with its account settings, a setup profile one with its software. The originals
  stay for older clients. A profile whose file no longer reads is skipped and listed in
  machine-profiles/converted.json in the host's data folder.
- API 1.34.0: GET /packages/search reads the host's own copy of the winget package index (downloaded from
  cdn.winget.microsoft.com when the host starts and refreshed daily) and falls back to PowerShell 7 only without it.
- API 1.33.0: GET /isos/{name}/content downloads an image from the ISO library (a Range header resumes it) and
  GET /profile-images/{name}/content downloads a wallpaper image, so a client can keep them in its own library.
- API 1.32.0: GET /machine-profile-templates (the built-in install profiles as machine profile templates) and
  POST /machine-profile-templates/{templateId}/vms; CreateVmFromProfileRequest takes compute and switch for that VM.
- API 1.31.0: /profile-images (list, upload, delete) keeps each User's wallpaper images; a machine profile's
  display.guest.wallpaper.image names one of them.
- API 1.30.0: VmConnection.defaults carries the machine profile's Remote Desktop defaults; MachinePart has connection.
- API 1.29.0: GET /vms/{vmId}/machine-profile-preview and POST /vms/{vmId}/machine-profile (a new applyMachineProfile
  job kind).
- API 1.28.0: POST /machine-profiles/{profileId}/vms creates a VM from a machine profile as a createVm job.
- API 1.27.0: machine profiles on the host (/machine-profiles: list, create, import, export, update, delete, and
  the JSON Schema). The account section follows the unattended install profile rules and the software sections the
  setup profile rules.
- API 1.26.0: the package catalog has a description per package, the setup profile catalog lists the rated built-in
  apps (appx) and the registry values of each tweak. Update the host for package descriptions, built-in apps without
  a VM, and tweak details; with an older host the editor works as before without them.

### Fixed
- With dynamic memory off, New VM, create from a profile, and a VM's settings hide Maximum memory instead of showing
  it greyed out.
- Buttons and drop-downs that had no style of their own (the Library page, the template list on the Profiles tab)
  no longer show the plain Windows look; every button, list, and field now shares one base style.
- Editing a saved machine profile with an Account section could fail to open the editor.

## [0.2.0] - 2026-10-22 (API 1.25.0)

The first stable release since 0.1.6. It brings everything from the 0.1.7 and 0.2.0 betas below to the stable channel:
moving or copying VMs between hosts, backup and restore with Kopia, Tailscale for connecting from away, the Game ready
checklist and game-aware mode, camera sharing for several VMs at once, client self-update, the Lean host action, and the
Apache License 2.0. Both hosts must run 0.2.0 or later to transfer a VM between them. The host updates itself to this
version when it is on the stable channel with automatic updates on; installed clients update at their next check.

### Changed
- Hosts and clients on the beta channel are now offered a stable release when it is newer than the channel's latest beta,
  starting with this one. Before, the beta channel moved only with prereleases, so a beta install stayed on its last beta
  until the next one.

### Fixed
- Deleting a VM that has no disks, or deleting a VM and keeping its disks elsewhere, left its folder behind with empty
  Hyper-V folders in it, so the next VM of that name (a VM moved back, for one) was named "(moved)" or "(copy)". The
  host now also removes the VM's configuration folder when it is named after the VM and nothing but empty Hyper-V
  folders is left in it.
- Every change to a VM's settings, and every transfer, warned that its maximum memory was more than the host has, for
  any VM made in Hyper-V Manager: Hyper-V gives those a 1 TB maximum, which means "no limit". That maximum is no longer
  reported, and a change to other settings no longer repeats the warning for a maximum set earlier.
- The update helper's own log lines were lost: it wrote to the host's daily log file, which the running service keeps
  open, so nothing it logged during an update (including why a run failed) reached the disk. It now writes
  logs\update-yyyyMMdd.log, which Download host logs includes.

## [0.2.0-beta.2] - 2026-10-22 (API 1.25.0)

### Fixed
- A VM with a network adapter that is connected to no switch could not be moved or copied: the receiving host refused
  the import with "The VM's definition has 1 network adapter(s) where 0 were expected". The host listed a VM's adapters
  from their switch connections, so an unconnected adapter was missed. This also left such adapters out of VM backups,
  so a restored VM came back without them. Every adapter is now listed, connected or not. Update the sending host.
- The host's update helper (hh-update.exe) crashed at the end of every run, after the update had already succeeded, which
  left an "Application Error" event in Windows and a failed last result on the "HyperHarbor\Update" scheduled task. Updates
  themselves were not affected. Each update is run by the helper of the version installed before it, so the update to
  this version still ends that way; updates from this version on end cleanly.

## [0.2.0-beta.1] - 2026-10-22 (API 1.25.0)

### Added
- HyperHarbor is now licensed under the Apache License 2.0 (LICENSE, NOTICE). Before, the public repository had no
  license, so nothing in it could be reused. WARRANTY.md explains that it comes with no warranty and what it changes on
  your PCs; the README has install steps, screenshots, and how to contribute (pull requests with a DCO sign-off).
- A security policy (SECURITY.md: report vulnerabilities privately through the repository's Security tab), a code of
  conduct, and issue forms that ask for the host and client versions and the Windows build. Each GitHub release now
  shows that version's section of this changelog.
- "Trust HyperHarbor connections" at the bottom of the client's sidebar. Since the April 2026 Windows security update,
  Remote Desktop shows a security warning for every connection file it cannot trust, with the clipboard, microphone,
  camera, and other sharing switched off until ticked, every time. With one administrator approval, the client now
  makes a signing certificate for this PC's user, has Windows trust it, and signs every Remote Desktop file it opens
  (VMs, the host, and the console), so they open without the warning and with their sharing as set. The certificate's
  key cannot be exported, and the certificate cannot vouch for anything else: it is not a certificate authority and
  names no server. "Stop trusting them" undoes it with one more approval. Uninstalling the client does not remove the
  trust; use "Stop trusting them" first.

### Changed
- VM transfers go over the receiving host's wired connection when it has one (API 1.25.0). The receiving host now lists
  its own addresses in its answer to the offer (`addresses` in `TransferOffer`), wired before Wi-Fi and then the faster
  link, and the client puts them first for the sending host, followed by the addresses it knows (such as Tailscale).
  Before, the sending host used whichever address the client listed first: a laptop with both received a copy over
  Wi-Fi at under 68 MB/s, and the copy failed when the Wi-Fi dropped, while its 1 Gbps Ethernet sat idle. Update the
  receiving host and the client; with an older receiving host, transfers work as before.
- Transfers send faster: the sending host now reads and checks the next block of a disk while the current one is on the
  network, instead of between blocks.
- The host and client executables, the client installer, and the host's entry in Installed apps now name Faulkner
  Engineering LLC as the publisher, instead of "HyperHarbor" and "hyperharbor". The builds are still not code signed, so
  Windows still shows "Unknown publisher" when it asks for permission. The host's Installed apps entry changes the next
  time the host is installed.

### Fixed
- Moving or copying a VM that keeps its TPM failed with "The host could not be reached: ... operation timed out" when the
  receiving host had not yet checked its Hyper-V since it started. The check starts a throwaway VM, which takes longer
  than the client waited, and the client giving up stopped the check halfway, so every retry timed out again and the
  throwaway "HyperHarbor TPM probe" VMs were left running. The receiving host now always finishes the check and keeps its
  answer, whoever stops waiting, and the client waits up to three minutes for the receiving host's answer. Update the
  receiving host and the client.
- Adding an ISO to a host with no admin passphrase set showed the reason for a moment and then cleared it, because the
  image list refreshed after the attempt. The message now stays.

## [0.1.7-beta.6] - 2026-10-21 (API 1.24.0)

### Added
- A readiness check, "Network is private", in the client's Wake-on-LAN panel and the host window's Wake-on-LAN card. It
  warns when the host's network connection is on a Public network (or a domain network), where Windows Firewall blocks
  HyperHarbor (TCP 48443) and other devices cannot reach the host, and says how to make the network Private. It is never
  changed automatically, because that loosens the firewall on that network. Seen on a host whose Hyper-V external switch
  adapter Windows had made Public.
- Moving or copying a VM between hosts (API 1.24.0). A paired client checks the
  VM on the sending host (`GET /vms/{vmId}/transfer-preview`), gets a one-time ticket from the receiving host
  (`POST /transfers/incoming`, elevated), and starts a `transferVm` job on the sending host (`POST /vms/{vmId}/transfer`,
  elevated; `POST /vms/{vmId}/transfer/cancel` stops it). The hosts then talk to each other directly over HTTPS: the
  sending host pins the receiving host's certificate and presents its own, and the receiving host accepts only that
  certificate with the ticket, on its transfer routes only. Only an off VM without checkpoints can go. A move keeps the
  VM's ID and MAC addresses and deletes it from the sending host once the receiving host has it; a copy gets a new ID
  and new MAC addresses. A VM's TPM goes with it: the receiving host is given access to the TPM's key only for the
  export, so BitLocker does not ask for its recovery key, and the sending host's access is put back exactly. Disks are
  sent in checked blocks and a broken connection resumes; until the receiving host has the VM, a failure or cancel
  undoes everything there. The VM's set-up accounts (for the User with the same account on the other host), HyperHarbor's
  administrator credential for it, and its game-aware action go with it, so Connect works there without setting the VM up
  again. Performance mode (turn it on again; the GPU partition is removed on the way) and backup schedules do not; the
  transfer dialog warns about both. Both hosts need this version.
- Client: Move or copy to another host… in an off VM's menu. Pick a paired host (hosts on an older version, or whose
  version this device has not seen yet, are listed but not selectable), choose Move or Copy, and type the VM's name to
  confirm a move. The client asks each host for its admin passphrase when needed, shows the other host's warnings for you
  to accept, follows the transfer's progress (with Cancel transfer), and says where the VM ended up. The transfer ticket
  never reaches the client's window. This device's monitor and camera settings for the VM go with it.
- Transfers and VM TPMs: only the host that created a VM's TPM key can give another host access to it, so a moved VM
  can go back to that host but not on to a third one with its TPM. The transfer preview now says which host owns the key
  (`tpmKeyOwner`, `tpmKeyGuardians`, `tpmOwnedBySource`), and the receiving host refuses early, with the reason, when
  keeping the TPM cannot work: the sending host does not own the key, or Windows on the receiving host cannot open a key
  another host gave it (it tries this once on a throwaway VM). A VM going back to the host that owns its key needs no
  grant. The transfer dialog adds "Its TPM": Keep it, or Give it a new, empty TPM (`tpmMode: fresh`). With a new TPM the
  receiving host defines the VM anew around its disks, so it gets a new VM ID even on a move (a move still keeps its MAC
  addresses), and BitLocker asks for its recovery key once.
- Merge checkpoints: `POST /vms/{vmId}/checkpoints/merge` (elevated, audited) deletes every checkpoint of a VM as a
  `mergeCheckpoints` job, merging them into its disks. The transfer dialog offers Merge checkpoints when checkpoints are
  the only thing in the way.

### Fixed
- The receiving host's virtual TPM check could leave its throwaway VM ("HyperHarbor TPM probe ...") running when the host
  was busy, because turning off a VM that is still starting fails. The check now waits and retries until the VM is gone,
  logs a warning if anything is left, and removes any earlier check's VM before it starts.
- Deleting a VM with its disks, or moving it to another host, left its folder behind with empty "Virtual Hard Disks" and
  "Virtual Machines" folders, so a VM of the same name coming back later was renamed "(moved)". The VM's own folder (named
  after it, holding its disks' folder) is now removed too, but only when nothing else is left in it.
- Backups recorded the wrong virtual switch for each network adapter, so every restored VM was connected to the
  Default Switch (with a warning) instead of the switch it had. New backups record the right switch. Backups taken
  before this fix still restore to the Default Switch; reconnect the adapter afterwards if it needs another switch.
- Deleting a VM now also removes what the host kept about it: the Users' set-up accounts (Connect and the VM list
  no longer remember them), its game-aware policy, and its backup policies, so scheduled and "Back up now" runs no
  longer try to back up a VM that is gone. Backups already taken stay in their backup locations and can still be
  restored. A record that cannot be removed is logged and no longer fails a deletion that already removed the VM.

### Security
- The client no longer includes the zip archive library its updater used only for the old archive update format
  (RUSTSEC-2026-0367 to 0369: extracting an archive could write outside its folder through symbolic links). Client
  updates are the signed installer itself and never an archive, so nothing changes for updates. The client's
  certificate checking library (rustls-webpki) is updated to 0.103.16 and its root certificates (webpki-root-certs) to
  1.0.10 (RUSTSEC-2026-0382 and 0383). Update the client on every PC.

## [0.1.7-beta.5] - 2026-10-14 (API 1.23.0)

### Added
- Tailscale, host side (API 1.23.0; uses the Tailscale client you already installed, nothing is installed or signed in for you):
  the host reads its own client through the local API (port 41112 by default, changeable for other setups) and falls
  back to `tailscale status --json`. `GET /host/tailscale` reports installed, signed in, the host's Tailscale
  address and MagicDNS name, and whether it has the `tag:hyperharbor-host` tag, with the `tagOwners` policy entry the tag needs
  and the `tailscale up` command for signing a new PC in with it (it includes `--login-server` when a Headscale URL is
  configured; `PUT /host/tailscale/settings`, elevated). HyperHarbor never applies the tag itself: on a PC that is signed in,
  advertising a tag the policy does not allow makes Tailscale sign the PC out (found by trying it). Add the policy entry,
  then add the tag under Machines, Edit ACL tags, in the Tailscale admin console.
- A device paired or first connecting over the tailnet is tied to its Tailscale node. A later request over a Tailscale
  address from a different node is refused with 403 `tailscaleNodeMismatch`, or `tailscaleNodeUnverified` when the host
  cannot tell which node sent it. Requests over the local network are not affected. To use a paired device from a new
  Tailscale node, pair it again.
- `POST /wake/relay`: a paired host on a sleeping host's LAN sends the magic packet for a client that is away. It
  only sends from, and to the broadcast address of, its own networks.
- A Tailscale card in the host window (Overview and Game ready): installed, signed in, connected, the host's
  Tailscale address and MagicDNS name, and whether it has the `tag:hyperharbor-host` tag, with a Copy policy entry button
  and fields for the local API port and a Headscale control server URL. Tailscale stays optional: a PC without it shows
  the card as unavailable.
- Tailscale in the client (stage one: it uses the Tailscale you already have installed). This PC reads Tailscale
  through its local API (port 41112, changeable) and falls back to `tailscale status --json`. Every 30 seconds it
  probes the online tailnet peers on the HyperHarbor port, tagged hosts (`tag:hyperharbor-host`) first, and keeps the
  answers for a few minutes. A host found that way is merged with the one found on the local network, so it is listed once
  with both addresses; a paired host that is only reachable over Tailscale is listed too, and an unknown one appears as
  "Found on Tailscale".
- Connections try the local network address first and the Tailscale address second. A Tailscale address that worked a
  moment ago stays first, so away from home nothing waits for the local address to time out. The line under the host name
  says which route the last request took, and whether Tailscale is Direct or Relayed; a relayed path explains split
  tunneling for VPN users. A path is reported only once Tailscale has chosen one (an idle device has none yet), and the
  reachability test pings the host first so there is one to report.
- Host menu > Tailscale: Tailscale on this PC and on the host, the connection route, a reachability test that checks the way
  step by step (Tailscale on this PC, host on the tailnet, online, path, HyperHarbor answering, certificate, signed-in
  request), the policy entry for the tag with a Copy button, and the local API port and Headscale control server settings (the host's
  need the admin passphrase).
- Waking a sleeping host from away from its network: when this PC has no address on that network, it asks another
  paired host that is online on it to send the magic packet, choosing the best one automatically (direct paths first).
  If none is reachable, Wake says so.
- Backup foundations (work in progress toward backup and restore with Kopia): backup targets (local folder, S3,
  Azure Blob) and per-VM policies are stored in `backup-config.json`; repository passphrases and cloud keys are kept
  only in a DPAPI-protected vault. A repository is created only after you confirm that you saved its recovery key by
  typing back its last four groups.

- The host now carries Kopia 0.23.1 (the backup engine, Apache-2.0, see THIRD-PARTY-NOTICES.md). It is checked
  against a pinned SHA-256 before every use. This adds about 50 MB to the host executable. Storage keys and
  passphrases reach Kopia only through environment variables, never on a command line.
- Each local backup folder gets a RESTORE.md with the plain Kopia commands (it never contains the passphrase).
- A connection test for backup targets: a local folder is written to and read back; S3 and Azure are checked by
  connecting, with plain-language results for wrong keys or an unreachable host.

- VM backup engine (not yet reachable from the API or the apps): a running VM is backed up from a production
  checkpoint (a standard one if the guest cannot take it, with a warning), the frozen parent disk is hard-linked and
  uploaded with Kopia together with a manifest of the VM (generation, processors, memory, Secure Boot template, TPM,
  switch, GPU partition, Performance mode), and the checkpoint is always removed again, even when the upload fails or is
  cancelled. VMs that already have checkpoints of their own are skipped with a warning; VMs with a GPU partition are
  backed up only while off unless you allow a live (crash-consistent) copy. Retention (7 daily, 4 weekly, 6 monthly)
  keeps one snapshot per day, so several backups on one day leave the newest.
- Host state backup (pairings, certificate, settings, profiles, provisioning records) to a separate repository. Secrets
  sealed to this PC (VM credentials, console accounts, the backup vault) are never included.
- Backup job kinds backupVm and backupHostState in the job list (API enum only; the endpoints come later).
- Backup scheduling (runs only for targets whose recovery key is confirmed and VMs with a policy): a nightly slot per
  target (02:00 by default; a missed slot runs when the host is next up), an optional run after a VM shuts down (a
  quiet period of 10 minutes by default; a restart within it cancels the run), and run conditions (no active session on
  the VM, no game in the foreground, optional time window). A run whose conditions do not hold waits and is recorded as
  skipped after 12 hours. Each run keeps the host awake and holds back a host update. The session check sees only
  connections between the host and the guest, not Remote Desktop from other PCs.
- Restore engine (not yet reachable from the API or the apps): a VM snapshot restores as a new VM on a new ID, named with
  "(restored)", "(restored 2)" and so on when the name or its folder exists. Disks go to `<destination>\<name>\Virtual
  Hard Disks`; processors, memory, Secure Boot template, notes, nested virtualization, and MAC spoofing come from the
  backup's manifest. Network adapters use the switch of the same name, else the Default Switch (with a warning), else stay
  unconnected. A VM with a virtual TPM gets a new key protector (the old one is never reused) and a warning that BitLocker
  may ask for its recovery key. A GPU partition is restored when the host allows it and always flagged for a Performance
  mode driver sync. Optionally the VM starts, waits for the guest to report in (5 minutes; otherwise it is kept and the
  console is suggested), and creates your HyperHarbor account with the administrator credential you give once. A failure
  removes everything the restore created. Generation 1 backups and manifests from a newer HyperHarbor are refused.
- Host-state restore (pairings, certificate, settings, profiles; only the names a backup writes are accepted; the backup
  configuration of this PC is kept) and replace-disk (VM off, no checkpoints, the exact VM name typed; the old disks are
  kept until every new disk is in place and restored on any failure). Not yet built: browsing files inside a snapshot.
- Backup and restore API (API 1.18.0, `Backup` tag): targets (list, add, change, remove, connection test), the recovery
  key (shown once; the user types its last four groups back to confirm; one key protects a target's VM and host-state
  repositories), adding a target that already holds backups with its key, per-VM policies, status with an overdue flag,
  history, run now, scan, restore as a new VM, restore host state, and replace disk. Restores run as jobs (`restoreVm`,
  `restoreHostState`, `replaceDisk`) whose `restoreResult` lists warnings, a GPU driver re-sync, and a possible BitLocker
  recovery prompt. Adding or changing targets, the recovery key, scans, and every restore need elevation. Scheduled
  backups run under no User, so only manual runs appear to the client that started them.
- Backups in the client (Host menu, Backups): add a target (folder, S3-compatible, Azure Blob) with a connection test, or
  add one that already holds backups using its recovery key; the recovery key is shown once and confirmed by typing its
  last four groups back; choose which VMs back up to which target, and whether also after they shut down; run a backup
  now; see the latest result and history; Restore… (look at what a target holds, restore a VM as a new VM, or restore the
  host's settings); and a VM's menu has "Restore disk from backup…" (VM off, its exact name typed).
- The host window has a Backups card, and a failed backup or one with no good copy for twice its schedule is announced
  (each failure once, each overdue item once a day). The Ambient screen shows one muted line about backups with the host
  status. On a new PC: set the admin passphrase in the host window, pair a client, add the target with its recovery key,
  then restore.
- Restore test (API 1.20.0, on by default): every 30 days, between 03:00 and 06:00 and only when no game or backup is running,
  the host restores the latest backup of each VM as a throwaway VM with no network, starts it, waits for the guest to report
  in, records passed, failed, or skipped with the reason, and always deletes the throwaway VM and its files. VMs with a GPU
  partition and VMs that would not fit on the disk are skipped. GET /backup/restore-test, PUT /backup/restore-test/settings,
  and POST /backup/restore-test/run (run now).
  In the client: Host menu, Backups, Restore test (turn on or off, interval and time window, Run test now, latest results).
  A failed test is announced by the tray like a failed backup and shows on the Backups card and the Ambient screen.

- Browse the files inside a backup (API 1.22.0): in the client's Restore window, "Browse files…" restores the chosen snapshot
  into a work folder on the host, opens its disks read-only, and lets you walk the folders and save single files without
  restoring the VM. It needs the admin passphrase, shows one backup at a time, closes by itself after 30 minutes without
  use, and removes the work folder when closed (or at the next host start). While open, the disk's volumes appear as
  read-only drives on the host. Links inside the backup are never followed. Disks with a file system Windows cannot read
  (for example most Linux guests) or an encrypted volume cannot be browsed; restore the VM instead.
- Restore page in the host window, for a new or reinstalled PC (no paired client needed): enter where the backups are (a
  folder, S3-compatible storage, or Azure Blob) and the recovery key, then restore the host's settings (paired devices,
  certificate, settings, profiles) and VMs as new VMs. The key is checked against the backups and kept in this PC's protected
  store. The Overview shows a "New PC?" card on a host with no devices and no backups configured. Restore the VMs first or
  the settings first; after restoring settings, restart the host service.
- The host window's Backups card has a "Back up now" button: it starts the host-state backup and every VM that has a backup
  policy, on every ready target, and says which could not start and why.
- Game-aware mode additions (API 1.21.0): you can add your own game launchers to the built-in list (a game a launcher starts
  counts; the launcher's own window does not), in the client's Game-aware panel and the host window's Game-aware page
  ("launchers" in the settings). The Game ready section now has a Game-aware mode card with its status, an on/off switch,
  and a link to the settings, in place of the Coming soon card.
- Game-aware mode (API 1.19.0, off by default): when a game is in the foreground on the host, the running VMs are paused
  (or saved and stopped, or left running, per VM) so the game has the PC, and exactly those VMs are resumed when the game
  ends. A game is found from a full-screen Direct3D app or presentation mode, a borderless full-screen window started by
  Steam, Epic, Battle.net, Xbox, GOG, or Playnite, or a program on your allowlist. Programs on the ignore list, the
  launchers' own windows, HyperHarbor's windows, and the desktop never count. A game must hold for 3 seconds before VMs
  are touched and be gone for 10 seconds before they resume. A VM with a GPU partition (Performance mode) is always
  paused. A VM someone is connected to is left running unless its setting says to pause it anyway, and the client then
  shows "the host is gaming". If the tray stops reporting, or no game is reported for a minute, the VMs resume, and a
  host restart resumes any VM a game left paused. Every pause and resume is logged with the program that caused it.
  Where to find it: the client's Host menu, Game-aware mode (switch, per-VM action, allowlist and ignore list, "This was
  not a game", and a banner while the host is gaming); the host window's Game-aware page; a line on the Ambient screen.
  The same foreground check now also makes a scheduled backup or the scheduled sleep wait for a game, and it recognizes
  borderless windowed games, which the old full-screen-only check missed. The session check for "someone is connected"
  sees only connections between the host and the guest, not Remote Desktop from other PCs.

### Fixed
- The host window looked half dark: a black page behind white cards, with faded headings. The ambient screensaver page
  (added with the Ambient screen card) set a black background and light text on the whole page, and it shares one bundle
  with the host window, so every tray window got it. It now applies to the ambient screen only. This is not a dark mode: the
  tray follows the Windows app theme as before. Text links (for example What's new on the Updates page) are darker in light
  mode, because the amber was hard to read on white.

### Changed
- Data format 3 (additive: new backup files). An older host that finds format 3 data will refuse to start, as with
  any newer data format. The `backup` folder in the data directory (Kopia caches) is not copied into update backups.

### Security
- The client's TLS library (rustls) is updated to 0.23.46, which fixes four published advisories (RUSTSEC-2026-0346 to 0349:
  secrets in debug output, data sent after an ECH rejection, a large pre-authentication allocation, and a possible
  key-exchange downgrade). Update the client on every PC.

## [0.1.7-beta.4] - 2026-10-12 (API 1.17.0)

### Fixed
- After a host update the tray now restarts itself. It used to keep running the old version, so the host window
  showed the old screens until the tray was closed and opened again. The tray compares its version with the one the
  service reports and starts the new copy when they differ. This takes effect from the update after the one that
  installs it.

### Added
- Game ready: an Ambient screen card for Console idle. After 5 minutes (you choose) without input, Windows starts a full-screen
  black screen with a clock and, if you turn them on, small widgets for the host status, the running virtual machines, and the
  paired devices. It is a Windows screensaver (HyperHarbor Ambient.scr in the install folder), so Windows keeps the idle timer
  and ends the screen on keyboard or mouse input; the tray also ends it when a controller is used. The screensaver must start
  before the display turns off: Apply refuses a screen time at or above the display timeout, a shorter display timeout is
  refused while the screen is on, and the card checks the timeout Windows actually has. When the display comes back on, the
  screen stays for a moment and the next button press returns to the launcher. An optional soft lock asks for a controller
  chord (LB + RB + Start by default) and otherwise shows the chord as a hint; it is not a security boundary, and keyboard and
  mouse input still end the screen. Your own screensaver settings are saved and put back when you turn the screen off. The card
  shows the screen's measured CPU and GPU use. An HDMI-CEC adapter is detected but standby over CEC is not built yet. Needs the
  installed service and the Console idle power mode.
- Game ready: a Power mode card with three exclusive modes. Console idle sets the active power plan's sleep and hibernate
  timeouts (and the sleep after an unattended wake) to never and turns the display off after a time you choose (10 minutes
  by default), so a controller press brings the TV back at once. An optional schedule (sleep at 01:00, wake at 17:00 by
  default) registers two Windows scheduled tasks: one sleeps the host when no remote session, job, or full-screen game is in
  use and tries again every 15 minutes until the wake time, the other wakes it, and wake timers are allowed in the plan.
  Outside the schedule the host never sleeps on its own. Balanced puts the plan's earlier values back; Sleep when idle uses
  short timers (display off after 5 minutes, sleep after 15). The modes change the active plan in place, so its GPU and
  launcher tweaks stay, and switching is repeatable. The card shows an estimated idle cost per month from a wattage and a
  price per kWh you set, and the host's measured CPU and GPU load in Console idle. While the display is off in Console idle,
  the tray watches game controllers and asks Windows for the display once when one changes. "Sleep now" is in the tray menu
  and on the card. Applying needs the installed service.
- Game ready: a Console sign-in card. It creates a dedicated standard local user (named Console by default, not an
  administrator and not a Hyper-V administrator) that Windows signs in automatically, and starts Steam Big Picture or
  Playnite full screen at sign-in (Steam is started minimized for Playnite). The password is generated on the host, kept
  only in the Windows LSA secret that Winlogon reads and in the host's encrypted store, and never shown. Setup needs the
  installed service, undoes its own changes if a step fails, and refuses to replace an automatic sign-in or an account it
  did not create. Disable restores the earlier values and can also delete the user and its profile. The card warns that
  anyone with physical access reaches the Console desktop, and when a Microsoft-account administrator needs Windows Hello.
  Note: Windows keeps "require sign-in after sleep" per power plan, so turning it off for the Console user applies to
  every account while that plan is active; Disable restores it.

## [0.1.7-beta.3] - 2026-10-11 (API 1.17.0)

### Added
- The host window has a Game ready section: a checklist of cards, each with its status, a short explanation, and a Fix
  button where the host can change the setting itself. Wake-on-LAN readiness is
  the first card (the clients' Wake panel still shows the same checks). Fast startup has its own card, shared by
  Wake-on-LAN and controller wake.
- Wake from controller: lists the connected game controllers and wireless receivers (Xbox, Xbox Wireless Adapter,
  GameSir, 8BitDo, Sony, and others recognized by name) with how they connect and whether Windows lets them wake the PC:
  Armed, Can arm (Fix arms the device with powercfg /deviceenablewake), or Cannot wake. Bluetooth controllers are
  reported as unable to wake the PC, with the advice to use a cable or a receiver; receivers that do not report wake
  support get a firmware update and XInput mode hint. Arming needs the installed service (administrator rights).
- Test wake puts the host to sleep after 10 seconds, and when the PC resumes reports whether Windows (powercfg /lastwake)
  blamed a controller, another device, or a power button or timer. It works with English Windows output; on other
  languages it reports that Windows did not say.
- Companion cards: USB selective suspend (Fix turns it off on AC power) and a manual card for the BIOS or UEFI USB power
  in sleep setting, which Windows cannot read.
- Sleep policy, Boot to launcher, Steam desktop layout, Lean host, and Game-aware mode appear as Coming soon cards, so the
  section has its final shape. The Lean host page is unchanged.

## [0.1.7-beta.2] - 2026-10-09 (API 1.17.0)

### Added
- The client updates itself (installed copies). It checks the stable or beta channel at start and once a day, and the
  sidebar shows "Version X is available" with what is new. Nothing installs until you choose Install and restart, which
  downloads the update, checks its signature against the key built into the client, and runs the installer; if Remote
  Desktop or console windows are open, it asks first, because the restart closes them. A portable copy cannot replace
  itself and shows the releases page instead. The sidebar's Settings picks the channel (a beta build follows beta by
  default) and turns the automatic check off. This client cannot update itself yet: install the first build that has
  this by hand, and later versions arrive through the update. For maintainers: releases are signed by the workflow with
  the repository secrets TAURI_SIGNING_PRIVATE_KEY and TAURI_SIGNING_PRIVATE_KEY_PASSWORD, and publish
  client-latest.json beside the host's latest.json (the beta channel copies both).
- After an update, the Camera… dialog offers "Update camera component" when the camera component installed under
  Program Files differs from the one the new version ships with (one administrator prompt; open sessions lose their
  camera until they reconnect). It is never replaced without asking.
- Camera sharing for several VMs at once (client, Windows 11). Remote Desktop gives the physical camera to the first
  session that asks for it, so a second VM never got video. The client now opens the camera once and gives each Windows
  VM session a virtual camera of its own, named "HyperHarbor Camera (VM name)", so every VM shows live video at the
  same time. Each session's connection file redirects only its own virtual camera, never the physical one. The
  microphone is unchanged.
- The virtual cameras offer one size, 1280x720 at 30 fps. Offering 640x480 as well made a camera start in 640x480 after
  another application had probed it, and stay there until the app asked again; an app that wants a smaller picture
  scales the 1280x720 one itself.
- A camera for this PC's own applications: while a VM with a virtual camera is connected, apps on the client PC (Teams,
  Zoom, the browser) can also use a camera named "HyperHarbor Camera (This PC)". It always shows live video, with no
  shutter, blur, or freezing, and it goes away when the last VM disconnects. Windows has no system default camera, so
  each app has to be pointed at it once; apps that remember their last camera then use it while it exists and fall back
  to the physical camera when it is gone.
- VM menu, Camera…: choose what a VM sees while its window is not in front (a frozen picture, which is the default and
  the cheapest, a blurred picture, or live video), turn camera sharing off for that VM, and close a privacy shutter that
  shows the VM black at once, even in front. The choices are saved on this device. The dialog also says what each VM sees
  right now.
- The client says when another application is using the physical camera (the VMs keep their last picture meanwhile and
  the camera is taken back when it is free), and when Windows' camera privacy setting blocks it.
- One-time setup: Camera… offers "Set up camera sharing", which installs a small Windows component under
  `C:\Program Files\HyperHarbor Camera` and asks for administrator permission once. Windows loads the component into its
  camera service, which runs as a different account, so it cannot live in the per-user install folder. The installer
  ships the component; the portable client needs `hyperharbor_vcam.dll` beside it. Until setup is done the first VM
  gets the camera directly, as before. "Remove camera sharing from this device…" in the same dialog takes the component
  off again (administrator permission once more); uninstalling HyperHarbor does not, because its uninstaller cannot
  ask for administrator permission.
- Cleanup: virtual cameras are removed when their session ends, when the client closes, and at the next start if a
  previous run was cut short (only cameras this client created are touched).

### Changed
- On Windows 10, which has no virtual cameras, the first VM you connect to gets the physical camera as before and a
  second VM is told the camera is already in use, instead of silently getting none.
- The client's Rust build is now a workspace (client, the camera component, and the shared frame format). CI and
  `scripts/test-all.ps1` check all three, and `scripts/package.ps1` builds the component for the installer.

## [0.1.7-beta.1] - 2026-10-07 (API 1.17.0)

### Added
- Lean host action in the tray (HyperHarbor Host window, Lean host page): applies the shipped "Host gaming" profile to
  the host PC itself. It removes consumer apps (Clipchamp, News, Weather, Copilot, Xbox app, Phone Link, Teams, and
  similar), disables unneeded services and startup entries, turns off Widgets, Copilot, search highlights, Game Bar's
  startup panel, and Game DVR, uninstalls OneDrive and known RGB vendor suites (iCUE, Razer Synapse, Armoury Crate, MSI
  Center, Gigabyte Control Center, SignalRGB, NZXT CAM, and others), sets the High performance power plan, lets only the
  wired network adapter, keyboards, and mice wake the PC (Wake-on-LAN keeps working), and sets Edge policies (no background mode, startup
  boost, shopping, or sidebar). It needs the installed service (LocalSystem) or an administrator.
- Safety rules for the Lean host action:
  - A dry run is required. It reads the PC and lists every change; an apply is refused unless a dry run of the same
    profile was done in the last 24 hours and the PC still gives the same result.
  - The first apply makes a system restore point and exports the registry keys it touches (under
    `%ProgramData%\HyperHarbor\host-lean\backup`). If either fails, nothing is changed. System Protection must be on
    for the system drive.
  - Guards: Gaming Services and the Xbox Identity Provider stay while a game from a curated list is found in a Steam
    library; the print spooler stays while a physical printer exists; Photos stays while no other image viewer is
    installed. Hyper-V, Remote Desktop, HyperHarbor, and core Windows services are never changed, framework apps are
    never removed, and a startup entry on the built-in allowlist (security, audio, GPU, touchpad, VPN, Steam) is never
    disabled. Startup entries are disabled through StartupApproved (as Task Manager does), so nothing is deleted.
  - Every change is made only if the PC differs, so applying again changes nothing. After an apply, a second look
    checks that the changes stayed.
  - The changes become an undo profile (`host-lean\undo.yaml`) that puts back service startup types, startup entries,
    registry values, the power plan, and wake devices; the Undo button runs it (dry run first). Removed apps and
    uninstalled programs cannot be undone this way.
  - Idle memory in use and process count are recorded before and after each apply and shown on the page.
  - Monthly re-apply (on after the first apply, can be turned off on the page): keeps the profile in place after
    Windows updates. It runs only while the shipped profile is the one a person applied, and is audited. Each apply,
    undo, and scheduled run is recorded in the audit log.
- Setup profiles can target the host (`target: host`) with new sections: `services` (startup types), `startup`
  (disable, enable, keep), `power` (plan, wake rule nicOnly, nicAndInput, or unchanged, wake devices), and `remove.programs`. Registry tweaks accept `type: absent` to
  delete a value. The profile schema, the YAML reader and writer, and the validator know them. API 1.17.0 adds them to
  SetupProfile (all optional); host profiles are refused when applied to a VM.
- New curated tweaks: Turn off Copilot for every account, Turn off search highlights, Turn off Game DVR and background
  recording, Stop Game Bar from showing its startup panel. New browser policy: keep running in the background after
  the browser closes.

## [0.1.6] - 2026-10-07 (API 1.16.0)

### Added
- Download the host's logs from the client: Host menu > Download host logs…. After the admin passphrase, the host
  sends a zip of its log files (up to 100 MB, newest first) and a summary.txt (host and API versions, Windows
  version, uptime), and the client asks where to save it. The audit log is not included, and log entries hold no
  secrets. The download is recorded in the audit log. API: POST /host/logs/bundle (elevated, audited); update the
  host to use it (the menu entry says so for older hosts).

### Changed
- The client now explains how to install software that needs administrator approval in a VM: add it to a setup
  profile and use Apply setup profile…, which the host runs as the VM's administrator (a note in the Apply dialog and
  on the Setup profiles tab), shown in a small info popup next to the Setup profile label and the tab's description.

### Fixed
- Starting a VM now reports why it could not start. Before, a start that Hyper-V accepted but then failed, such as
  when the host does not have enough free memory, did nothing and showed no message (a new VM then sat at
  "installing OS" while off). The start request now waits for Hyper-V and the client shows its reason. Update the
  host.

## [0.1.5] - 2026-10-05 (API 1.15.0)

### Added
- Update progress: while the host downloads and tests a new version, the client's Updates panel and the host
  window show how far the download is (MB and percent), then checking the package and testing it on a copy of the
  host's data. API: GET /host/update has progress (step, bytesDone, bytesTotal) while preparing; update the host.

### Changed
- A new HyperHarbor Host window on the host PC (double-click the tray icon). It matches the client app, follows
  Windows' light and dark theme, and sizes itself for the display it opens on. Sections are in a sidebar (Overview,
  Devices, Storage, Updates, Logs). Everything you start shows its progress where you started it: setting the
  passphrase, setting up console access or package search, changing a folder, removing a device, and update
  checks (with Check, Download and test, Ready, and Install steps). Confirmations and results appear in the
  window instead of message boxes. The pairing PIN has its own window with a countdown. The tray needs the
  Microsoft Edge WebView2 Runtime, which Windows 11 includes; without it, the tray offers the download and shows
  pairing PINs as notifications.

### Fixed
- Check now could do nothing at all once the host service had run for a while: each quiet minute left a wait
  behind that took the next request. Fixed in the host; update the host to get it.
- Check now (client Updates panel) gave no feedback: the host answers before the check runs, so the button
  went straight back and the old status stayed. The panel now shows Checking… until the host reports the check,
  then says what it found (up to date, a new version, or why the check failed), or that the host did not finish
  within 90 seconds. This works with hosts from before this fix too. The host now reports a requested check as
  checking at once, and the tray's Check now shows Checking… as soon as it is clicked.

## [0.1.4] - 2026-10-05 (API 1.14.0)

### Added
- Apply setup profile (VM menu, running Windows VMs): applies one of your setup profiles to an existing VM, the same
  way as after a new install. Your account's settings change too when you have signed in to the VM before (some
  appear only after you sign out and back in). Choose whether HyperHarbor may restart the VM when a change needs
  it; the dialog lists what was applied and anything that could not be. Needs a host at API 1.14.0.
- API: POST /vms/{vmId}/setup-profile (elevated, audited) starts an applySetupProfile job, whose setupResult says
  what it did; setupResult.restartPending reports a restart left to you.
- Choose a setup profile when creating a Windows VM with an automatic install (Create VM, after the computer name).
  Once Windows is installed and your account is set up, the host installs the profile's apps with winget, removes
  the apps, capabilities, and features it lists, writes its tweaks (your account's settings go to the Default user
  profile, so they apply at your first sign-in) and browser policies, and installs its extensions (you can turn
  them off, not remove them). It restarts the VM if something needs it. The VM list shows "Applying the setup
  profile…", and the ready notice lists anything that could not be applied. The profile is kept as it was when
  the VM was created. Needs a host at API 1.13.0; the host's data format changes to 2 (older records stay as they are).
- API: install.setupProfileId on POST /vms, install state applyingProfile, and setupProfileName and setupResult on
  GET /vms/{vmId}/install. The host audits applySetupProfile with counts only.
- Setup profiles: YAML files on the host that list what to install (winget ids, Microsoft Store ids, or aliases),
  what to remove (provisioned Appx packages, capabilities, features), registry tweaks, and a browser with its
  extensions and policies. Each id is written with its friendly name as a comment, and files reference a JSON Schema
  (schemas/profile.v1.schema.json) so editors can check them. New Setup profiles tab in the client: build profiles
  with a package picker (Popular, and winget search), an Appx checklist read from a running VM with ratings and a
  debloat preset, a tweak list, and an extension picker that takes a store link; import and export YAML files.
  Applying a profile to a VM comes later.
- Capture setup profile (VM menu, running Windows VMs): reads what the VM has that a clean install does not, and
  shows it in three columns to keep or drop before saving a profile. Capture now also lists winget packages in a
  fresh VM whose administrator never signed in (App Installer is registered for it first). Removed packages are found by comparing with a
  clean baseline, recorded automatically after an unattended Windows install or on request.
- Package search needs PowerShell 7 and the WinGet PowerShell module on the host; the tray's Set up package search
  installs both (one administrator prompt).
- API: setup profile routes (list, get, create, update, delete, import, export, schema, catalog), GET /packages/search
  and /packages/catalog, GET /extensions/resolve and /extensions/catalog, GET /vms/{vmId}/appx, POST
  /vms/{vmId}/appx-baseline, POST /vms/{vmId}/profile-capture; problem code wingetUnavailable.
- The host stays awake while a paired device uses it, so a host woken with Wake-on-LAN no longer goes back to
  sleep after about 2 minutes (Windows' unattended sleep timeout). It stays awake for 10 minutes after the
  client's last contact (an open client checks in every few seconds), while work such as a console session or
  a job is in progress, and while someone is signed in to the host over Remote Desktop; then Windows' own sleep
  settings apply again. The client keeps checking in once a minute while Remote Desktop or console windows it
  opened stay open, as long as the client app is running. Running VMs alone do not keep the host awake.
  Update both the host and the client.
- Wake diagnostics also report the unattended sleep timeout and the active power requests.

## [0.1.3] - 2026-10-05 (API 1.11.0)

### Added
- "Install <version> now" in the client's Updates panel installs a downloaded and tested update right away,
  whatever the update mode. It needs the admin passphrase and is audited; work in progress on the host still
  finishes first. Hosts before API 1.11.0 install only from their tray, and the client shows no button for them.
- API: `POST /host/update/install` (elevated), problem code `updateNotReady`.

### Fixed
- The HyperHarbor Host window (double-click the tray icon) no longer runs past the bottom of scaled or
  high-resolution screens. Its sections are on tabs (Overview, Storage, Updates, Logs) that scroll, the window
  can be resized, it opens within the screen's work area, and it refits when moved to a monitor with other
  scaling. The Updates tab reads "Updates (ready)" while a version waits to install. The pairing PIN and
  admin passphrase windows are also kept on screen.

## [0.1.2] - 2026-10-05 (API 1.10.0)

### Added
- Remote Desktop to the host itself for maintenance, from the client's Host menu. mstsc asks for the host's
  Windows account; HyperHarbor stores and sends no host credentials. Home editions are reported as unsupported.
- The host can turn on its own Remote Desktop (and the Windows Firewall "Remote Desktop" rule) when a client
  asks. This needs the admin passphrase and is audited. A host run without administrator rights (a development
  console run) asks the user to turn it on in Windows Settings instead.
- API: `GET /host/remote-desktop` and `POST /host/remote-desktop/enable` (elevated), problem codes
  `remoteDesktopUnsupported` and `requiresInstalledService`.
- Per-VM monitor choice for Connect (one monitor, all, or selected ones), from the VM's Monitors… menu item.
  The choice is kept on each device and names monitors by device path, so it survives rearranging displays.

### Changed
- The host header shows only "+ New VM" (and the elevation countdown); Wake-on-LAN, Updates, Refresh, Unpair,
  and Remote Desktop to host moved into a "Host ▾" menu. The VM row menu uses the same menu component.
- A feature the host does not have yet (an older host) now says to update the host, instead of "Not Found".
  Features from a newer API version are greyed out for hosts that report an older version over mDNS.

### Fixed
- The tray pipe server waits for tray connections to finish their message when it stops.

## [0.1.1] - 2026-10-04 (API 1.9.0)

Version bump used to verify the self-update path (0.1.0 to 0.1.1, and the rollback of a broken build).

## [0.1.0] - 2026-10-04 (API 1.9.0)

First packaged version, covering roadmap phases 1 to 11.7 (see docs/history.md):
- Hyper-V VM inventory, power actions, and a Kestrel API with mutual TLS.
- mDNS discovery, PIN pairing (SPAKE2) with certificate pinning, Wake-on-LAN with readiness checks and fixes.
- One-click Remote Desktop to VMs with a rotated per-User account (Windows via PowerShell Direct, Linux via SSH
  and xrdp).
- VM lifecycle: create from an ISO (Gen 2, Secure Boot, vTPM), compute settings, delete; ISO library; audit
  log and admin passphrase elevation.
- VM console through the host, unattended Windows and Linux installs, Performance mode (GPU-P and Remote
  Desktop tuning).
- A single host executable that installs itself as a LocalSystem service, with side-by-side versions,
  self-test, rollback, and update checks against GitHub releases.
