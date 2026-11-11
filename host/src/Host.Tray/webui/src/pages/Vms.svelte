<script lang="ts">
  // The VMs on this host, with the client's list and dialogs (@hyperharbor/ui; docs/host-vm-management.md). Requests
  // go through the tray to this host's API as the tray's device, and elevated ones ask for the admin passphrase.
  import { onMount } from "svelte";
  import { hostApi, type HostRef } from "@hyperharbor/ui/api/hostApi.ts";
  import { errorMessage, type Vm, type VmAction } from "@hyperharbor/ui/api/model.ts";
  import ComputeDialog from "@hyperharbor/ui/components/ComputeDialog.svelte";
  import DeleteVmDialog from "@hyperharbor/ui/components/DeleteVmDialog.svelte";
  import DiskDialog from "@hyperharbor/ui/components/DiskDialog.svelte";
  import ElevationDialog from "@hyperharbor/ui/components/ElevationDialog.svelte";
  import VmList from "@hyperharbor/ui/components/VmList.svelte";
  import { ElevationCancelled, elevation, withElevation } from "@hyperharbor/ui/lifecycle.svelte.ts";
  import ConfirmDialog from "../components/ConfirmDialog.svelte";
  import type { TrayViewState } from "../bridge";
  import { tray } from "../store.svelte";

  interface Props {
    view: TrayViewState;
  }

  let { view }: Props = $props();

  /** The tray window has one host, this one; the key only labels its elevation. */
  const host: HostRef = { key: "local", displayName: "this host" };

  /** The client's main page refreshes its list as often. */
  const REFRESH_INTERVAL_MS = 5000;

  let vms = $state<Vm[] | null>(null);
  let loadError = $state<string | null>(null);
  let actionVmId = $state<string | null>(null);
  let confirmTurnOff = $state<Vm | null>(null);
  let editing = $state<Vm | null>(null);
  let disks = $state<Vm | null>(null);
  let deleting = $state<Vm | null>(null);

  const actionVerbs: Record<VmAction, string> = {
    start: "Starting",
    shutdown: "Shutting down",
    turnOff: "Forcing off",
    save: "Saving",
    restart: "Restarting",
  };

  async function refresh() {
    try {
      vms = await hostApi().listVms(host.key);
      loadError = null;
    } catch (error) {
      loadError = errorMessage(error);
    }
  }

  onMount(() => {
    void refresh();
    const timer = setInterval(() => {
      // Nothing to ask while the service is stopped; the banner above says so.
      if (view.connected) void refresh();
    }, REFRESH_INTERVAL_MS);
    return () => clearInterval(timer);
  });

  function requestAction(vm: Vm, action: VmAction) {
    // Forcing a VM off is like pulling the power cord, so it is confirmed first.
    if (action === "turnOff") confirmTurnOff = vm;
    else void sendAction(vm, action);
  }

  async function sendAction(vm: Vm, action: VmAction) {
    actionVmId = vm.id;
    try {
      await withElevation(host.key, () => hostApi().performVmAction(host.key, vm.id, action));
      tray.toast("info", `${actionVerbs[action]} ${vm.name}`, "The list shows when it is done.");
      await refresh();
    } catch (error) {
      if (!(error instanceof ElevationCancelled)) tray.toast("error", `${vm.name} did not change`, errorMessage(error));
    } finally {
      actionVmId = null;
    }
  }

  function turnOffConfirmed() {
    const vm = confirmTurnOff;
    confirmTurnOff = null;
    if (vm) void sendAction(vm, "turnOff");
  }

  function closed(changed: boolean) {
    editing = null;
    disks = null;
    deleting = null;
    if (changed) void refresh();
  }
</script>

<!-- The shared components take the base look of their controls from the hh-controls class (ui/src/theme.css). -->
<div class="hh-controls vms">
  {#if vms === null && loadError === null}
    <p class="muted" aria-busy="true">Reading the VMs on this host…</p>
  {:else if vms === null}
    <div class="problem" role="alert">
      <p><strong>The VM list could not be read.</strong> {loadError}</p>
      <button type="button" class="btn" onclick={() => void refresh()}>Try again</button>
    </div>
  {:else}
    {#if loadError}
      <p class="stale" role="status">The list may be out of date: {loadError}</p>
    {/if}
    {#if vms.length === 0}
      <p class="muted">This host has no VMs yet. Create one from the HyperHarbor client.</p>
    {:else}
      <VmList
        {vms}
        {actionVmId}
        onaction={requestAction}
        onsettings={(vm) => (editing = vm)}
        ondisks={(vm) => (disks = vm)}
        ondelete={(vm) => (deleting = vm)}
      />
    {/if}
  {/if}

  {#if editing}
    <ComputeDialog {host} vm={editing} onclose={closed} />
  {/if}
  {#if disks}
    <DiskDialog {host} vm={disks} onclose={closed} />
  {/if}
  {#if deleting}
    <DeleteVmDialog {host} vm={deleting} onclose={closed} />
  {/if}
  {#if elevation.pending}
    <ElevationDialog {host} />
  {/if}
</div>

{#if confirmTurnOff}
  <ConfirmDialog
    title="Force shut off {confirmTurnOff.name}?"
    message="Forcing it off is like pulling the power cord: anything not saved in the VM is lost. Use Shut down when the guest can respond."
    confirmLabel="Force shut off"
    danger
    onconfirm={turnOffConfirmed}
    oncancel={() => (confirmTurnOff = null)}
  />
{/if}

<style>
  .vms {
    display: grid;
    gap: 0.75rem;
  }

  .problem {
    display: grid;
    gap: 0.6rem;
    justify-items: start;
    padding: 0.9rem 1rem;
    border: 1px solid var(--border);
    border-radius: var(--radius);
    background: var(--error-bg);
  }

  .stale {
    margin: 0;
    padding: 0.5rem 0.75rem;
    border-radius: 8px;
    background: var(--warn-bg);
    color: var(--warn-fg);
  }
</style>
