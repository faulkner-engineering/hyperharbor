<script lang="ts">
  import type { Vm, VmState } from "$lib/api/client";

  interface Props {
    vms: Vm[];
  }

  let { vms }: Props = $props();

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
      </tr>
    </thead>
    <tbody>
      {#each vms as vm (vm.id)}
        <tr>
          <td class="name">{vm.name}</td>
          <td>
            <span class="badge {stateTone(vm.state)}">{stateLabels[vm.state]}</span>
          </td>
          <td class="num">{vm.cpuUsagePercent == null ? "" : `${vm.cpuUsagePercent}%`}</td>
          <td class="num">{formatMemory(vm.memoryAssignedMb)}</td>
          <td class="num">{formatUptime(vm.uptimeSeconds)}</td>
          <td class="address">{vm.ipAddresses[0] ?? ""}</td>
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

  .empty {
    margin: 0;
    color: var(--muted);
  }
</style>
