<script lang="ts">
  import type { UnattendedInstallState, Vm, VmAction, VmState } from "$lib/api/client";
  import Menu from "./Menu.svelte";

  interface Props {
    vms: Vm[];
    /** VM whose Connect or Provision request is in progress. */
    busyVmId?: string | null;
    /** VM whose power action is being sent. */
    actionVmId?: string | null;
    /** VM whose console is being opened. */
    consoleVmId?: string | null;
    onconnect?: (vm: Vm) => void;
    onconsole?: (vm: Vm) => void;
    onprovision?: (vm: Vm) => void;
    onaction?: (vm: Vm, action: VmAction) => void;
    onsettings?: (vm: Vm) => void;
    onperformance?: (vm: Vm) => void;
    onmonitors?: (vm: Vm) => void;
    /** Capture a setup profile from a running Windows VM; leave unset for hosts without setup profiles. */
    oncapture?: (vm: Vm) => void;
    ondelete?: (vm: Vm) => void;
  }

  let {
    vms,
    busyVmId = null,
    actionVmId = null,
    consoleVmId = null,
    onconnect,
    onconsole,
    onprovision,
    onaction,
    onsettings,
    onperformance,
    onmonitors,
    oncapture,
    ondelete,
  }: Props = $props();

  /** How long a running VM may go without reporting its OS before the list stops saying "Detecting". */
  const OS_DETECTION_SECONDS = 180;

  function osDetectionTimedOut(vm: Vm): boolean {
    return (vm.uptimeSeconds ?? 0) >= OS_DETECTION_SECONDS;
  }

  const installLabels: Record<UnattendedInstallState, string> = {
    installing: "Installing the OS…",
    awaitingConfirmation: "Installing (type yes in the console if asked)",
    waitingForGuest: "Starting the installed OS…",
    waitingForRemoteAccess: "Waiting for Remote Desktop…",
    configuring: "Setting up your account…",
    ready: "Ready",
    failed: "Install failed",
    canceled: "Install canceled",
  };

  /** Turn off is allowed in these states (VmActionPolicy on the host). */
  function canForceOff(state: VmState): boolean {
    return state === "running" || state === "paused" || state === "starting" || state === "stopping";
  }

  /** The one action shown as a button; everything else is in the menu. */
  function primaryAction(vm: Vm): "start" | "connect" | "setup" | "console" | null {
    if (vm.state === "off" || vm.state === "saved" || vm.state === "paused") return "start";
    if (vm.state !== "running") return null;
    // An unattended install sets up the account itself; until then the console shows its progress.
    if (vm.installState && vm.installState !== "failed") return "console";
    if (vm.provisioned) return "connect";
    return vm.guestOs.family === "unknown" ? "console" : "setup";
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
        {@const primary = primaryAction(vm)}
        <tr>
          <td class="name">
            {vm.name}
            {#if vm.performanceMode}
              <span class="gpu" title="Performance mode: this VM has a share of the host's GPU">GPU</span>
            {/if}
            {#if vm.installState}
              <span class="os install" class:failed={vm.installState === "failed"}>{installLabels[vm.installState]}</span>
            {:else if vm.guestOs.name}
              <span class="os">{vm.guestOs.name}</span>
            {:else if vm.state === "running" && vm.guestOs.family === "unknown"}
              {#if osDetectionTimedOut(vm)}
                <span
                  class="os"
                  title="The VM has not reported its operating system through Hyper-V data exchange. This is normal while an OS is being installed; use Console to see the screen. Once Windows or Linux with Hyper-V integration services is running, it is detected automatically."
                  >OS not detected</span
                >
              {:else}
                <span class="os" title="Waiting for the VM to report its operating system through Hyper-V data exchange.">Detecting OS…</span>
              {/if}
            {/if}
          </td>
          <td>
            <span class="badge {stateTone(vm.state)}">{stateLabels[vm.state]}</span>
          </td>
          <td class="num">{vm.cpuUsagePercent == null ? "" : `${vm.cpuUsagePercent}%`}</td>
          <td class="num">{formatMemory(vm.memoryAssignedMb)}</td>
          <td class="num">{formatUptime(vm.uptimeSeconds)}</td>
          <td class="address">{vm.ipAddresses[0] ?? ""}</td>
          <td class="actions">
            {#if primary === "start"}
              <button type="button" class="primary" disabled={actionVmId !== null} onclick={() => onaction?.(vm, "start")}>
                {actionVmId === vm.id ? "Starting…" : vm.state === "paused" ? "Resume" : "Start"}
              </button>
            {:else if primary === "connect"}
              <button
                type="button"
                class="primary"
                disabled={!vm.remoteDesktop.address || busyVmId !== null}
                title={!vm.remoteDesktop.address ? "Waiting for the VM to report an address" : ""}
                onclick={() => onconnect?.(vm)}>{busyVmId === vm.id ? "Connecting…" : "Connect"}</button
              >
            {:else if primary === "setup"}
              <button type="button" class="primary" disabled={busyVmId !== null} onclick={() => onprovision?.(vm)}>Set up…</button>
            {:else if primary === "console"}
              <button
                type="button"
                class="primary"
                disabled={consoleVmId !== null}
                title="Show the VM's screen, also while its operating system is being installed"
                onclick={() => onconsole?.(vm)}>{consoleVmId === vm.id ? "Opening…" : "Console"}</button
              >
            {/if}
            <Menu label="⋮" ariaLabel="Actions for {vm.name}" title="More actions">
              {#if (vm.state === "running" || vm.state === "paused") && primary !== "console"}
                <button
                  type="button"
                  disabled={consoleVmId !== null}
                  onclick={() => onconsole?.(vm)}>{consoleVmId === vm.id ? "Opening console…" : "Console"}</button
                >
              {/if}
              {#if vm.state === "running"}
                <button type="button" disabled={actionVmId !== null} onclick={() => onaction?.(vm, "shutdown")}>
                  Shut down
                </button>
                <button type="button" disabled={actionVmId !== null} onclick={() => onaction?.(vm, "restart")}>
                  Restart
                </button>
              {/if}
              {#if vm.state === "running" || vm.state === "paused"}
                <button type="button" disabled={actionVmId !== null} onclick={() => onaction?.(vm, "save")}>
                  Save state
                </button>
              {/if}
              {#if canForceOff(vm.state)}
                <button
                  type="button"
                  class="danger"
                  disabled={actionVmId !== null}
                  onclick={() => onaction?.(vm, "turnOff")}>Force shut off…</button
                >
              {/if}
              <hr />
              <button type="button" onclick={() => onsettings?.(vm)}>Settings…</button>
              {#if vm.guestOs.family !== "linux"}
                <button type="button" onclick={() => onperformance?.(vm)}>Performance mode…</button>
              {/if}
              <button type="button" onclick={() => onmonitors?.(vm)}>Monitors…</button>
              {#if oncapture && vm.state === "running" && vm.guestOs.family === "windows"}
                <button type="button" onclick={() => oncapture(vm)}>Capture setup profile…</button>
              {/if}
              <button type="button" class="danger" onclick={() => ondelete?.(vm)}>Delete…</button>
            </Menu>
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

  .gpu {
    display: inline-block;
    margin-left: 0.35rem;
    padding: 0 0.4rem;
    border-radius: 999px;
    font-size: 0.7rem;
    font-weight: 600;
    vertical-align: middle;
    background: var(--accent);
    color: var(--accent-fg);
  }

  .os {
    display: block;
    font-size: 0.8rem;
    font-weight: 400;
    color: var(--muted);
  }

  .install {
    color: var(--accent);
  }

  .install.failed {
    color: var(--danger);
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

  .actions > button {
    padding: 0.3rem 0.8rem;
    border: 1px solid var(--border);
    border-radius: 6px;
    background: var(--surface);
    color: inherit;
    cursor: pointer;
  }

  .actions > button.primary {
    background: var(--accent);
    border-color: var(--accent);
    color: var(--accent-fg);
  }

  .actions > button:disabled {
    opacity: 0.5;
    cursor: default;
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
