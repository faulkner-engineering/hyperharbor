# Managing VMs from the host (phase 33)

Status: done (2026-11-09 to 2026-11-11), in three pull requests; what was verified is in docs/history.md, entry 33. What was verified is
recorded in docs/history.md when the phase is done.

## Why
Only a paired client can list VMs and change their settings. On the host itself the tray window covers pairing,
storage, updates, game readiness, backups, and logs, but not the VMs that run there; changing a VM's memory on the
host meant opening the client. The host should be able to do what the client does for its own VMs, with the same
list and dialogs.

## Decisions (the user, 2026-11-09)
- First version: the VM list with state and the power actions (start, shut down, turn off, save, restart), compute
  and disk settings, and delete. Not in it: creating VMs, machine and setup profiles, performance mode, backups of a
  VM, transfers, the console, and Remote Desktop. They can follow with the same parts.
- Elevated actions in the tray ask for the admin passphrase, as in the client (a 5-minute elevation). The tray user
  can be a standard Windows account, so the tray is not trusted with elevation by itself.
- The shared UI lives in a top-level ui/ package that both apps depend on (a file: dependency), not in the client's
  folder.

## Design

### The tray is a paired device of its own host
The tray reaches the service only over the tray pipe (Shared.Contracts/Ipc/TrayMessages.cs), whose messages are tray
features; the VM operations, their checks, their audit entries, and their elevation rules are API routes
(docs/api.yaml). Copying them onto the pipe would make a second contract to keep equal to the first. Instead the tray
calls the API like a client:

- The tray makes a P-256 key and a self-signed client certificate on first use, as the client does (identity.rs),
  and keeps the key with DPAPI for the tray user (ProtectedFile). It sends the certificate's fingerprint to the service
  in a new pipe message. The pipe admits only SYSTEM, Administrators, the service account, and the configured tray
  user, so the message is already authenticated; the service adds a paired device named "Host tray" for the default
  User (PairedDeviceStore.Add; a new certificate replaces the old entry).
- The device shows on the Devices page and can be removed like any other; the tray registers again the next time it
  needs the API. Audit entries for the tray's VM actions carry its device ID, like any client's.
- The tray window cannot reach the network (its CSP has connect-src 'none'). The page sends a request message over
  the WebView2 bridge (method, path, body); the tray's C# side sends it to https://127.0.0.1:<port>/api/v1 with the
  tray certificate, pins the host certificate (the tray reads it from the data folder), and returns the status and
  body. Only paths under /api/v1/vms and the other routes the VM page uses are forwarded (an allow list in C#, tested).
- Elevation: the page asks for the passphrase in the shared elevation dialog and calls the existing elevate route;
  the token travels with the forwarded request (X-HyperHarbor-Elevation), as the client sends it.
- No API change. EndpointSecurityTests and the route checks apply to the tray's calls as they are.

### The shared ui/ package
- ui/ at the repository root: package.json (name @hyperharbor/ui; exports "./*" from src/, Svelte 5 as a peer
  dependency, its own lockfile for svelte-check), src/ with the components, HostApi, and the shared types. No build
  step: each app's Vite compiles it. Imports name the file: "@hyperharbor/ui/components/VmList.svelte".
- Both apps add "@hyperharbor/ui": "file:../ui" (the tray: "file:../../../../ui"), which npm links. Each app's Vite
  config sets resolve.dedupe ["svelte"]: the shared files import svelte from ui/node_modules, and two copies of the
  runtime would not share reactivity (the client build has one). The client's dev server allows ../ui (server.fs).
- scripts\test-all.ps1 and CI install and type-check ui/ (npm ci, svelte-check, npm audit) before the client, whose
  type check reads the shared files with ui/node_modules; the release workflows install it too. Dependabot updates
  /ui with the client and the tray window in the npm group.
- HostApi: the subset of host calls the shared components make (listVms, performVmAction, getVmCompute,
  updateVmCompute, listVmDisks, growVmDisk, getDeletePreview, deleteVm, getHostResources, elevate, getElevation,
  getJob), with the generated types from docs/api.yaml. Each app registers its implementation once at startup
  (provideHostApi; the client in routes/+layout.svelte, from client/src/lib/api/hostApi.ts), and the components call
  hostApi() when they make a request. A module-level provider rather than a Svelte context, because the job and
  elevation helpers are plain modules too. The client implements it with its Tauri commands for the host with that
  key; the tray implements it with the bridge request. Errors keep the client's ClientError shape (code "api" with the
  status, problem code, and field issues), which the tray builds from the host's problem response.
- Moved into ui/: VmList, ComputeDialog, DiskDialog, DeleteVmDialog, ElevationDialog, and the Dialog, JobProgress,
  and Menu components they use; the job and elevation helpers (lifecycle.svelte.ts; client/src/lib keeps a
  re-export); and the API types with the error helpers (api/model.ts; client.ts re-exports them, so client code
  imports them as before).
- The generated API types moved to ui/src/api/types.ts (npm run gen:api in client/ writes there), one copy for both.
- The components' tests stay in the client and run them through the client's HostApi against a mocked Tauri invoke
  (each such test imports #lib/api/hostApi.ts). ui/ has no tests of its own yet; the tray's page tests add a fake
  HostApi in pull request 3.
- ui/src/theme.css (pull request 3) holds what the shared components need from the page around them: the brand
  tokens, the terminal palette, --idle-bg, and the base look of buttons and inputs, scoped to the hh-controls class.
  The client loads it from app.css and puts hh-controls on <html>, so it looks as before; the tray loads it before its
  theme.css and puts the class on the VM page only, so its own buttons keep their look. Each app keeps its own
  semantic colors (--bg, --surface, --accent, and the rest): they differ slightly on purpose (the tray's --selected,
  its --surface-2 and --link), and the shared components use only the ones both define.
- VmList hides an action whose handler the page leaves out (the tray gives no Connect, Console, Set up, Performance
  mode, or Monitors), rather than showing a button that does nothing.

### The tray's VM page
- A "Virtual machines" page in the host window's nav, first after Overview. It polls the VM list every 5 seconds
  while it is shown (as the client's main page does) and stops when the window is hidden.
- Power actions and the three dialogs come from ui/. Features the first version leaves out are not shown (no
  disabled buttons that lead nowhere; docs/ui-principles.md).
- When the service is not running or the tray is not registered yet, the page says so and offers what fixes it,
  rather than an empty list.

## Pull requests
1. Client onto ui/: create the package, move the components, HostApi with the client's implementation, gen:api into
   ui/, and the scripts and CI. No visible change in the client.
2. Tray device and API forwarding: the certificate and its DPAPI key, the pipe message and the device entry, the
   bridge request and the C# forwarder with its allow list. Tests: the device is added for the configured tray user
   only, a removed device is registered again, forwarding refuses paths outside the allow list, and an end-to-end test
   (no Hyper-V) lists VMs through the forwarder against a test host.
3. The VM page in the tray: the merged theme tokens, the nav entry, the page, the tray's HostApi, the Playwright theme and fit checks for the
   page, the CHANGELOG, and a live check on this machine (list, a compute change with the passphrase, a disk grow on a
   throwaway VM).

## Risks
- Svelte components compiled by two Vite configurations: the tray has no SvelteKit, so ui/ must not import $app/* or
  #lib (checked by its own svelte-check, which runs without SvelteKit).
- The pipe squatting limitation (docs/architecture.md, Known limitations) also applies to the registration: a process
  that took the pipe name before the service started could answer it with its own address and certificate
  fingerprint, and the tray would then send it the VM page's requests, including the admin passphrase when the page
  asks for it. The tray already sends that process the passphrase today (the passphrase page sends only its hash, but
  the Devices page's removals and other commands go there too), so this widens an existing gap rather than opening a
  new one. Pull request 3 closes it before the page ships: the tray checks the pipe's owner before it sends anything
  (PipeServerCheck). The installed service runs as LocalSystem, so its pipe is owned by SYSTEM or Administrators,
  which a standard user's process cannot set; a portable host runs as the tray's own user, and a process of that
  user can already read the tray's files. This was simpler and stronger than identifying the server process by its
  image path, which a standard user's tray cannot always read for a LocalSystem process.
