<script lang="ts">
  import { onMount } from "svelte";
  import { getHostGpu, type GpuDriverWarning, type HostEntry } from "$lib/api/client";

  interface Props {
    host: HostEntry;
  }

  let { host }: Props = $props();

  let warnings = $state<GpuDriverWarning[]>([]);

  onMount(async () => {
    try {
      warnings = (await getHostGpu(host.key)).warnings;
    } catch {
      // Older hosts have no GPU endpoint, and the notice is informational; leave it out.
      warnings = [];
    }
  });

  const total = $derived(warnings.reduce((sum, warning) => sum + warning.count, 0));

  function when(iso: string): string {
    return new Date(iso).toLocaleString();
  }
</script>

{#if warnings.length > 0}
  <details class="gpu-warning" role="status">
    <summary>
      The host's GPU driver reported {total === 1 ? "an error" : `${total} errors`} in the last seven days. GPU VMs may stutter or
      lose their display.
    </summary>
    <ul>
      {#each warnings as warning (warning.provider + warning.eventId)}
        <li>
          <strong>{warning.provider} {warning.eventId}</strong>
          ({warning.count}×, last {when(warning.lastSeen)}): {warning.message}
        </li>
      {/each}
    </ul>
    <p class="muted">Updating the host's GPU driver often helps. Re-sync the driver in each GPU VM afterwards.</p>
  </details>
{/if}

<style>
  details {
    margin-bottom: 0.75rem;
    padding: 0.75rem 1rem;
    border-radius: 6px;
    background: var(--warn-bg);
    color: var(--warn-fg);
  }

  summary {
    cursor: pointer;
  }

  ul {
    margin: 0.5rem 0;
    padding-left: 1.25rem;
  }

  li {
    margin: 0.2rem 0;
  }

  .muted {
    margin: 0;
  }
</style>
