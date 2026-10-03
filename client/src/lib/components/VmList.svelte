<script lang="ts">
  import type { Vm, VmAction, VmState } from "$lib/api/client";

  interface Props {
    vms: Vm[];
    /** VM whose Connect or Provision request is in progress. */
    busyVmId?: string | null;
    /** VM whose power action is being sent. */
    actionVmId?: string | null;
    onconnect?: (vm: Vm) => void;
    onprovision?: (vm: Vm) => void;
    onaction?: (vm: Vm, action: VmAction) => void;
    onsettings?: (vm: Vm) => void;
    ondelete?: (vm: Vm) => void;
  }

  let { vms, busyVmId = null, actionVmId = null, onconnect, onprovision, onaction, onsettings, ondelete }: Props =
    $props();

  /** Runs a menu item and closes its menu. */
  function choose(event: MouseEvent, run: () => void) {
    (event.currentTarget as HTMLElement).closest("details")?.removeAttribute("open");
    run();
  }

  const stateLabels: Record<VmState, string> = {
    running: "Running",
    off: "Off",
    saved: "Saved",
    paused: "Paused",
    starting: "Starting",
    stopping: "Stopping",
    saving: "Saving",
    pausing: "Pausing",
    resuming: "Resuming",
    other: "Other",
  };

  function stateTone(state: VmState): string {
    switch (state) {
      case "running":
        return "ok";
      case "off":
      case "saved":
        return "idle";
      case "paused":
        return "warn";
      default:
        return "busy";
    }
  }

  function formatUptime(seconds: number | undefined): string {
    if (seconds === undefined) return "";
    const days = Math.floor(seconds / 86400);
    const hours = Math.floor((seconds % 86400) / 3600);
    const minutes = Math.floor((seconds % 3600) / 60);
    if (days > 0) return `${days}d ${hours}h`;
    if (hours > 0) return `${hours}h ${minutes}m`;
    return `${minutes}m`;
  }

  function formatMemory(mb: number | null | undefined): string {
    if (mb === null || mb === undefined) return "";
    return mb >= 1024 ? `${(mb / 1024).toFixed(1)} GB` : `${mb} MB`;
  }
</script>

{#if vms.length === 0}
  <p class="empty">This host has no virtual machines.</p>
{:else}
  <table>
    <thead>
      <tr>
        <th scope="col">Name</th>
        <th scope="col">State</th>
        <th scope="col" class="num">CPU</th>
        <th scope="col" class="num">Memory</th>
        <th scope="col" class="num">Uptime</th>
        <th scope="col">Address</th>
        <th scope="col" class="actions"><span class="visually-hidden">Actions</span></th>
      </tr>
    </thead>
    <tbody>
      {#each vms as vm (vm.id)}
        <tr>
          <td class="name">
            {vm.name}
            {#if vm.guestOs.name}<span class="os">{vm.guestOs.name}</span>{/if}
          </td>
          <td>
            <span class="badge {stateTone(vm.state)}">{stateLabels[vm.state]}</span>
          </td>
          <td class="num">{vm.cpuUsagePercent == null ? "" : `${vm.cpuUsagePercent}%`}</td>
          <td class="num">{formatMemory(vm.memoryAssignedMb)}</td>
          <td class="num">{formatUptime(vm.uptimeSeconds)}</td>
          <td class="address">{vm.ipAddresses[0] ?? ""}</td>
          <td class="actions">
            {#if vm.provisioned}
              <button
                type="button"
                class="primary"
                disabled={vm.state !== "running" || !vm.remoteDesktop.address || busyVmId !== null}
                title={vm.state !== "running" ? "Start the VM to connect" : !vm.remoteDesktop.address ? "Waiting for the VM to report an address" : ""}
                onclick={() => onconnect?.(vm)}>{busyVmId === vm.id ? "Connecting…" : "Connect"}</button
              >
            {:else if vm.state === "running" && vm.guestOs.family !== "unknown"}
              <button type="button" disabled={busyVmId !== null} onclick={() => onprovision?.(vm)}>Set up…</button>
            {:else if vm.state === "running"}
              <span class="hint" title="The VM has not reported its operating system through Hyper-V data exchange yet.">
                Detecting OS…
              </span>
            {/if}
            {#if vm.state === "off" || vm.state === "saved"}
              <button type="button" disabled={actionVmId !== null} onclick={() => onaction?.(vm, "start")}>
                {actionVmId === vm.id ? "Starting…" : "Start"}
              </button>
            {:else if vm.state === "running"}
              <button type="button" disabled={actionVmId !== null} onclick={() => onaction?.(vm, "shutdown")}>
                {actionVmId === vm.id ? "Sending…" : "Shut down"}
              </button>
            {/if}
            <details class="menu">
              <summary aria-label="More actions for {vm.name}">⋯</summary>
              <div class="menu-items">
                {#if vm.state === "running"}
                  <button type="button" onclick={(event) => choose(event, () => onaction?.(vm, "restart"))}>Restart</button>
                  <button type="button" onclick={(event) => choose(event, () => onaction?.(vm, "save"))}>Save state</button>
                {/if}
                {#if vm.state !== "off" && vm.state !== "saved"}
                  <button type="button" class="danger" onclick={(event) => choose(event, () => onaction?.(vm, "turnOff"))}>
                    Turn off…
                  </button>
                {/if}
                <button type="button" onclick={(event) => choose(event, () => onsettings?.(vm))}>Settings…</button>
                <button type="button" class="danger" onclick={(event) => choose(event, () => ondelete?.(vm))}>Delete…</button>
              </div>
            </details>
          </td>
        </tr>
      {/each}
    </tbody>
  </table>
{/if}

<style>
  table {
    width: 100%;
    border-collapse: collapse;
  }

  th {
    text-align: left;
    font-size: 0.75rem;
    font-weight: 600;
    text-transform: uppercase;
    letter-spacing: 0.05em;
    color: var(--muted);
    padding: 0.4rem 0.6rem;
    border-bottom: 1px solid var(--border);
  }

  td {
    padding: 0.55rem 0.6rem;
    border-bottom: 1px solid var(--border);
  }

  .num {
    text-align: right;
    font-variant-numeric: tabular-nums;
  }

  .name {
    font-weight: 600;
  }

  .os {
    display: block;
    font-size: 0.8rem;
    font-weight: 400;
    color: var(--muted);
  }

  .hint {
    font-size: 0.8rem;
    color: var(--muted);
  }

  .address {
    font-family: ui-monospace, "Cascadia Mono", Consolas, monospace;
    font-size: 0.85rem;
  }

  .badge {
    display: inline-block;
    padding: 0.1rem 0.5rem;
    border-radius: 999px;
    font-size: 0.8rem;
    font-weight: 600;
  }

  .ok {
    background: var(--ok-bg);
    color: var(--ok-fg);
  }

  .idle {
    background: var(--idle-bg);
    color: var(--muted);
  }

  .warn {
    background: var(--warn-bg);
    color: var(--warn-fg);
  }

  .busy {
    background: var(--busy-bg);
    color: var(--busy-fg);
  }

  .actions {
    text-align: right;
    white-space: nowrap;
  }

  .actions button {
    padding: 0.3rem 0.8rem;
    border: 1px solid var(--border);
    border-radius: 6px;
    background: var(--surface);
    color: inherit;
    cursor: pointer;
  }

  .actions button.primary {
    background: var(--accent);
    border-color: var(--accent);
    color: var(--accent-fg);
  }

  .actions button:disabled {
    opacity: 0.5;
    cursor: default;
  }

  .actions > button + button,
  .actions > button + details,
  .actions > span + button {
    margin-left: 0.35rem;
  }

  .menu {
    display: inline-block;
    position: relative;
    margin-left: 0.35rem;
  }

  .menu summary {
    list-style: none;
    padding: 0.3rem 0.6rem;
    border: 1px solid var(--border);
    border-radius: 6px;
    cursor: pointer;
    user-select: none;
  }

  .menu summary::-webkit-details-marker {
    display: none;
  }

  .menu-items {
    position: absolute;
    right: 0;
    z-index: 5;
    display: flex;
    flex-direction: column;
    min-width: 10rem;
    margin-top: 0.25rem;
    padding: 0.25rem;
    border: 1px solid var(--border);
    border-radius: 6px;
    background: var(--surface);
    box-shadow: 0 4px 12px rgb(0 0 0 / 0.15);
  }

  .menu-items button {
    border: none;
    text-align: left;
    padding: 0.4rem 0.6rem;
  }

  .menu-items button:hover {
    background: var(--hover);
  }

  .menu-items button.danger {
    color: var(--danger);
  }

  .visually-hidden {
    position: absolute;
    width: 1px;
    height: 1px;
    overflow: hidden;
    clip: rect(0 0 0 0);
  }

  .empty {
    margin: 0;
    color: var(--muted);
  }
</style>
