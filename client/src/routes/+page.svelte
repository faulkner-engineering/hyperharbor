<script lang="ts">
  import {
    errorMessage,
    isClientError,
    listHosts,
    listVms,
    isOffline,
    onHostsChanged,
    unpair,
    wakeHost,
    connectVm,
    openConsole,
    hasProblemCode,
    dropElevation,
    performVmAction,
    type VmAction,
    type HostEntry,
    type Vm,
  } from "$lib/api/client";
  import { onMount } from "svelte";
  import { SvelteMap } from "svelte/reactivity";
  import HostList from "$lib/components/HostList.svelte";
  import PairingPanel from "$lib/components/PairingPanel.svelte";
  import VmList from "$lib/components/VmList.svelte";
  import WakePanel from "$lib/components/WakePanel.svelte";
  import HostUpdatePanel from "$lib/components/HostUpdatePanel.svelte";
  import ProvisionDialog from "$lib/components/ProvisionDialog.svelte";
  import ElevationDialog from "$lib/components/ElevationDialog.svelte";
  import DeleteVmDialog from "$lib/components/DeleteVmDialog.svelte";
  import CreateVmDialog from "$lib/components/CreateVmDialog.svelte";
  import ComputeDialog from "$lib/components/ComputeDialog.svelte";
  import PerformanceDialog from "$lib/components/PerformanceDialog.svelte";
  import GpuWarnings from "$lib/components/GpuWarnings.svelte";
  import ConfirmDialog from "$lib/components/ConfirmDialog.svelte";
  import { elevation, ElevationCancelled, withElevation } from "$lib/lifecycle.svelte";
  import IsoLibrary from "$lib/components/IsoLibrary.svelte";
  import InstallProfiles from "$lib/components/InstallProfiles.svelte";
  import Toasts from "$lib/components/Toasts.svelte";
  import BrandMark from "$lib/brand/BrandMark.svelte";
  import { toasts } from "$lib/toasts.svelte";

  const REFRESH_INTERVAL_MS = 5000;

  let hosts = $state<HostEntry[]>([]);
  let selectedKey = $state<string | null>(null);
  let vms = $state<Vm[] | null>(null);
  let vmError = $state<string | null>(null);
  let loading = $state(false);
  let unpairing = $state(false);
  let offline = $state(false);
  let showWake = $state(false);
  let showUpdates = $state(false);
  let provisioning = $state<Vm | null>(null);
  let connectingVmId = $state<string | null>(null);
  let consoleVmId = $state<string | null>(null);
  let actionVmId = $state<string | null>(null);
  let confirmTurnOff = $state<Vm | null>(null);
  let deleting = $state<Vm | null>(null);
  let editing = $state<Vm | null>(null);
  let tuning = $state<Vm | null>(null);
  let creating = $state(false);
  let view = $state<"vms" | "isos" | "profiles">("vms");
  let now = $state(Date.now());

  const selectedHost = $derived(hosts.find((host) => host.key === selectedKey) ?? null);
  // VMs are only fetched from paired hosts; others show the pairing panel instead.
  const pairedKey = $derived(selectedHost?.paired ? selectedHost.key : null);

  function selectHost(key: string | null) {
    if (key === selectedKey) return;
    selectedKey = key;
    vms = null;
    installStates.clear();
    vmError = null;
    offline = false;
    showWake = false;
    showUpdates = false;
    provisioning = null;
    confirmTurnOff = null;
    deleting = null;
    editing = null;
    tuning = null;
    creating = false;
    view = "vms";
    elevation.finish(false);
  }

  async function refreshHosts() {
    hosts = await listHosts();
    if (selectedKey === null || !hosts.some((host) => host.key === selectedKey)) {
      selectHost(hosts[0]?.key ?? null);
    }
  }

  /** Each VM's install state at the last refresh, to announce when an install ends. */
  const installStates = new SvelteMap<string, string>();

  function announceInstalls(list: Vm[]) {
    for (const vm of list) {
      const before = installStates.get(vm.id);
      const now = vm.installState ?? null;
      if (before && before !== now) {
        if (now === "failed") toasts.error(`Installing ${vm.name} failed. Open its console to see why.`);
        else if (now === null && vm.provisioned) toasts.show(`${vm.name} is installed and ready. Press Connect.`);
      }

      if (now) installStates.set(vm.id, now);
      else installStates.delete(vm.id);
    }
  }

  async function refreshVms(key: string) {
    loading = true;
    try {
      const result = await listVms(key);
      if (key !== selectedKey) return;
      vms = result;
      announceInstalls(result);
      vmError = null;
      offline = false;
    } catch (error) {
      if (key !== selectedKey) return;
      vms = null;
      offline = isOffline(error);
      vmError = errorMessage(error);
      // The host forgot this device (for example, it was removed in the tray).
      if (isClientError(error) && error.code === "api" && error.status === 401) {
        vmError = "The host no longer recognizes this device. Unpair and pair again.";
      }
    } finally {
      loading = false;
    }
  }

  async function connect(vm: Vm) {
    if (selectedKey === null || !vm.remoteDesktop.address) return;
    connectingVmId = vm.id;
    try {
      await connectVm(selectedKey, vm.id, vm.remoteDesktop.address);
      toasts.show(`Opening Remote Desktop to ${vm.name}…`);
    } catch (error) {
      toasts.error(errorMessage(error));
    } finally {
      connectingVmId = null;
    }
  }

  async function openVmConsole(vm: Vm) {
    if (selectedKey === null) return;
    consoleVmId = vm.id;
    try {
      await openConsole(selectedKey, vm.id);
      toasts.show(`Opening the console of ${vm.name}…`);
    } catch (error) {
      toasts.error(
        hasProblemCode(error, "consoleSetupRequired")
          ? "Console access is not set up on this host. On the host, double-click the HyperHarbor tray icon and choose Set up console access."
          : errorMessage(error),
      );
    } finally {
      consoleVmId = null;
    }
  }

  function requestAction(vm: Vm, action: VmAction) {
    // Forcing a VM off is like pulling the power cord, so it is confirmed first.
    if (action === "turnOff") {
      confirmTurnOff = vm;
    } else {
      sendAction(vm, action);
    }
  }

  const actionVerbs: Record<VmAction, string> = {
    start: "Starting",
    shutdown: "Shutting down",
    turnOff: "Forcing off",
    save: "Saving",
    restart: "Restarting",
  };

  async function sendAction(vm: Vm, action: VmAction) {
    const key = selectedKey;
    if (key === null) return;
    actionVmId = vm.id;
    try {
      await withElevation(key, () => performVmAction(key, vm.id, action));
      toasts.show(`${actionVerbs[action]} ${vm.name}…`);
      await refreshVms(key);
    } catch (error) {
      if (!(error instanceof ElevationCancelled)) toasts.error(errorMessage(error));
    } finally {
      actionVmId = null;
    }
  }

  function turnOffConfirmed(confirmed: boolean) {
    const vm = confirmTurnOff;
    confirmTurnOff = null;
    if (confirmed && vm) sendAction(vm, "turnOff");
  }

  /** Closes a lifecycle dialog and refreshes the list if it changed anything. */
  function lifecycleClosed(changed: boolean) {
    deleting = null;
    editing = null;
    tuning = null;
    creating = false;
    if (changed && selectedKey) refreshVms(selectedKey);
  }

  async function dropSelectedElevation() {
    if (selectedKey === null) return;
    const key = selectedKey;
    elevation.forget(key);
    try {
      await dropElevation(key);
    } catch {
      // The token is forgotten locally either way; the host ends it within minutes.
    }
  }

  const elevatedUntil = $derived(selectedKey ? elevation.expiresAt[selectedKey] : undefined);
  const elevatedSeconds = $derived(
    elevatedUntil ? Math.max(0, Math.round((Date.parse(elevatedUntil) - now) / 1000)) : 0,
  );

  // Tick once a second while elevated, for the countdown.
  $effect(() => {
    if (!elevatedUntil) return;
    const timer = setInterval(() => (now = Date.now()), 1000);
    return () => clearInterval(timer);
  });

  function provisioned(done: boolean) {
    const vm = provisioning;
    provisioning = null;
    if (done && vm && selectedKey) {
      toasts.show(`${vm.name} is set up. Press Connect to open Remote Desktop.`);
      refreshVms(selectedKey);
    }
  }

  async function wakeSelected() {
    if (selectedKey === null) return;
    try {
      await wakeHost(selectedKey);
      toasts.show("Wake signal sent. The host usually responds within a minute.");
    } catch (error) {
      toasts.error(errorMessage(error));
    }
  }

  async function unpairSelected() {
    if (selectedKey === null) return;
    unpairing = true;
    try {
      await unpair(selectedKey);
    } catch (error) {
      // The local pairing is removed even when the host cannot be reached.
      vmError = errorMessage(error);
    } finally {
      unpairing = false;
      vms = null;
      await refreshHosts();
    }
  }

  onMount(() => {
    refreshHosts();
    const unlisten = onHostsChanged(refreshHosts);
    return () => {
      unlisten.then((stop) => stop());
    };
  });

  // Poll the selected host's VM list while it is selected and paired.
  $effect(() => {
    const key = pairedKey;
    if (key === null) return;

    refreshVms(key);
    const timer = setInterval(() => refreshVms(key), REFRESH_INTERVAL_MS);
    return () => clearInterval(timer);
  });
</script>

<div class="app">
  <aside>
    <h1><BrandMark size="1.4em" decorative />HyperHarbor</h1>
    <HostList {hosts} {selectedKey} onselect={selectHost} />
  </aside>

  <main>
    {#if selectedHost === null}
      <p class="placeholder">Select a host to see its virtual machines.</p>
    {:else}
      <header>
        <div>
          <h2>{selectedHost.displayName}</h2>
          <p class="subtitle">
            {selectedHost.isLocal ? "This PC" : selectedHost.addresses.join(", ")}
            · port {selectedHost.port}
          </p>
        </div>
        {#if selectedHost.paired}
          <div class="header-actions">
            {#if elevatedSeconds > 0}
              <span class="elevated" title="Changes that need the admin passphrase are allowed until this runs out.">
                Elevated {Math.floor(elevatedSeconds / 60)}:{String(elevatedSeconds % 60).padStart(2, "0")}
                <button type="button" class="link" onclick={dropSelectedElevation}>End</button>
              </span>
            {/if}
            <button type="button" class="primary" onclick={() => (creating = true)} disabled={offline || vms === null}>
              + New VM
            </button>
            <button type="button" onclick={() => (showWake = !showWake)}>
              Wake-on-LAN
            </button>
            <button type="button" onclick={() => (showUpdates = !showUpdates)}>Updates</button>
            <button type="button" onclick={unpairSelected} disabled={unpairing}>Unpair</button>
            <button
              type="button"
              onclick={() => pairedKey && refreshVms(pairedKey)}
              disabled={loading}>Refresh</button
            >
          </div>
        {/if}
      </header>

      {#if !selectedHost.paired}
        {#key selectedHost.key}
          <PairingPanel host={selectedHost} onpaired={refreshHosts} />
        {/key}
      {:else if offline}
        <div class="notice" role="status">
          <p class="notice-title">{selectedHost.displayName} is offline or asleep.</p>
          {#if selectedHost.canWake}
            <button type="button" class="primary" onclick={wakeSelected}>Wake</button>
          {:else}
            <p>It has not shared Wake-on-LAN details, so it cannot be woken from here.</p>
          {/if}
        </div>
      {:else if vmError}
        <div class="notice error" role="status">{vmError}</div>
      {:else if vms === null}
        <p class="placeholder">Loading…</p>
      {:else}
        {#key selectedHost.key}
          <GpuWarnings host={selectedHost} />
        {/key}
        <div class="views" role="tablist" aria-label="Host views">
          <button type="button" role="tab" aria-selected={view === "vms"} class:active={view === "vms"} onclick={() => (view = "vms")}>
            Virtual machines
          </button>
          <button type="button" role="tab" aria-selected={view === "isos"} class:active={view === "isos"} onclick={() => (view = "isos")}>
            ISO library
          </button>
          <button
            type="button"
            role="tab"
            aria-selected={view === "profiles"}
            class:active={view === "profiles"}
            onclick={() => (view = "profiles")}>Install profiles</button
          >
        </div>
        {#if view === "isos"}
          {#key selectedHost.key}
            <IsoLibrary host={selectedHost} />
          {/key}
        {:else if view === "profiles"}
          {#key selectedHost.key}
            <InstallProfiles host={selectedHost} />
          {/key}
        {:else}
          <VmList
            {vms}
            busyVmId={connectingVmId}
            {actionVmId}
            {consoleVmId}
            onconnect={connect}
            onconsole={openVmConsole}
            onprovision={(vm) => (provisioning = vm)}
            onaction={requestAction}
            onsettings={(vm) => (editing = vm)}
            onperformance={(vm) => (tuning = vm)}
            ondelete={(vm) => (deleting = vm)}
          />
        {/if}
        {#if provisioning}
          <ProvisionDialog host={selectedHost} vm={provisioning} onclose={provisioned} />
        {/if}
        {#if confirmTurnOff}
          <ConfirmDialog
            title="Force shut off {confirmTurnOff.name}?"
            message="Forcing it off is like pulling the power cord: anything not saved in the VM is lost. Use Shut down when the guest can respond."
            confirmLabel="Force shut off"
            danger
            onclose={turnOffConfirmed}
          />
        {/if}
        {#if deleting}
          <DeleteVmDialog host={selectedHost} vm={deleting} onclose={lifecycleClosed} />
        {/if}
        {#if editing}
          <ComputeDialog host={selectedHost} vm={editing} onclose={lifecycleClosed} />
        {/if}
        {#if tuning}
          <PerformanceDialog host={selectedHost} vm={tuning} onclose={lifecycleClosed} />
        {/if}
        {#if creating}
          <CreateVmDialog
            host={selectedHost}
            onclose={lifecycleClosed}
            onopenlibrary={() => {
              creating = false;
              view = "isos";
            }}
            onopenprofiles={() => {
              creating = false;
              view = "profiles";
            }}
          />
        {/if}
      {/if}

      {#if elevation.pending && elevation.pending.hostKey === selectedHost.key}
        <ElevationDialog host={selectedHost} />
      {/if}

      {#if selectedHost.paired && showWake}
        {#key selectedHost.key}
          <WakePanel host={selectedHost} />
        {/key}
      {/if}

      {#if selectedHost.paired && showUpdates}
        {#key selectedHost.key}
          <HostUpdatePanel host={selectedHost} />
        {/key}
      {/if}
    {/if}
  </main>
</div>

<Toasts />

<style>
  :global(:root) {
    --bg: var(--hh-fog);
    --surface: #ffffff;
    --text: var(--hh-ink);
    --muted: var(--hh-text-muted);
    --border: var(--hh-border);
    --hover: #eef1f4;
    --selected: #dde7f3;
    --danger: #cf222e;
    --ok-bg: #dafbe1;
    --ok-fg: #116329;
    --idle-bg: #eef1f4;
    --warn-bg: #fff8c5;
    --warn-fg: #7d4e00;
    --busy-bg: #ddf4ff;
    --busy-fg: #0550ae;
    --notice-bg: #ddf4ff;
    --error-bg: #ffebe9;
    --accent: var(--hh-accent);
    --accent-fg: var(--hh-ink);
    font-family: "Segoe UI", system-ui, sans-serif;
    font-size: 14px;
    color: var(--text);
    background-color: var(--bg);
  }

  @media (prefers-color-scheme: dark) {
    :global(:root) {
      --bg: #0d1117;
      --surface: #151b23;
      --text: var(--hh-fog);
      --muted: #9198a1;
      --border: #30363d;
      --hover: #1c232c;
      --selected: #1f2d3d;
      --danger: #ff7b72;
      --ok-bg: #12261e;
      --ok-fg: #56d364;
      --idle-bg: #21262d;
      --warn-bg: #2e2410;
      --warn-fg: #e3b341;
      --busy-bg: #0c2d4b;
      --busy-fg: #79c0ff;
      --notice-bg: #0c2d4b;
      --error-bg: #3c1618;
      --accent: var(--hh-accent);
      --accent-fg: var(--hh-ink);
    }
  }

  :global(body) {
    margin: 0;
  }

  .app {
    display: grid;
    grid-template-columns: 260px 1fr;
    height: 100vh;
  }

  aside {
    display: flex;
    flex-direction: column;
    gap: 1.25rem;
    padding: 1.25rem 1rem;
    border-right: 1px solid var(--border);
    background: var(--surface);
    overflow-y: auto;
  }

  h1 {
    display: flex;
    align-items: center;
    gap: 0.4rem;
    margin: 0;
    font-size: 1.2rem;
  }

  main {
    padding: 1.25rem 1.5rem;
    overflow-y: auto;
  }

  header {
    display: flex;
    align-items: flex-start;
    justify-content: space-between;
    gap: 1rem;
    margin-bottom: 1rem;
  }

  h2 {
    margin: 0;
    font-size: 1.25rem;
  }

  .subtitle {
    margin: 0.2rem 0 0;
    color: var(--muted);
  }

  .header-actions {
    display: flex;
    gap: 0.5rem;
  }

  header button {
    padding: 0.4rem 0.9rem;
    border: 1px solid var(--border);
    border-radius: 6px;
    background: var(--surface);
    color: inherit;
    cursor: pointer;
  }

  header button:disabled {
    opacity: 0.5;
    cursor: default;
  }

  header button.primary {
    background: var(--accent);
    border-color: var(--accent);
    color: var(--accent-fg);
  }

  .views {
    display: flex;
    gap: 0.25rem;
    margin-bottom: 1rem;
    border-bottom: 1px solid var(--border);
  }

  .views button {
    padding: 0.45rem 0.9rem;
    border: none;
    border-bottom: 2px solid transparent;
    background: none;
    color: var(--muted);
    cursor: pointer;
    font: inherit;
  }

  .views button.active {
    border-bottom-color: var(--accent);
    color: var(--text);
    font-weight: 600;
  }

  .elevated {
    display: inline-flex;
    align-items: center;
    gap: 0.4rem;
    padding: 0.2rem 0.6rem;
    border-radius: 999px;
    background: var(--warn-bg);
    color: var(--warn-fg);
    font-size: 0.85rem;
    font-variant-numeric: tabular-nums;
  }

  header .elevated button.link {
    padding: 0;
    border: none;
    background: none;
    color: inherit;
    text-decoration: underline;
  }

  .placeholder {
    color: var(--muted);
  }

  .notice {
    padding: 0.75rem 1rem;
    border-radius: 6px;
    background: var(--notice-bg);
  }

  .notice-title {
    margin: 0 0 0.75rem;
    font-weight: 600;
  }

  .notice p:last-child {
    margin-bottom: 0;
  }

  .notice button.primary {
    padding: 0.45rem 1rem;
    border: 1px solid var(--accent);
    border-radius: 6px;
    background: var(--accent);
    color: var(--accent-fg);
    cursor: pointer;
  }

  .notice.error {
    background: var(--error-bg);
    color: var(--danger);
  }
</style>
