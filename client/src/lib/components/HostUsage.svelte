<script lang="ts">
  import { onDestroy, onMount } from "svelte";
  import { getHostResources, type HostEntry, type HostResources } from "#lib/api/client.ts";
  import { formatBytes } from "#lib/backup.ts";

  interface Props {
    host: HostEntry;
  }

  let { host }: Props = $props();

  const POLL_MS = 5_000;
  const MB = 1024 * 1024;

  let resources = $state<HostResources | null>(null);
  let timer: ReturnType<typeof setInterval> | undefined;
  let loading = false;

  const cpu = $derived(resources?.cpuUsagePercent ?? null);
  const usedMb = $derived(resources ? Math.max(0, resources.totalMemoryMb - resources.availableMemoryMb) : 0);
  const memoryPercent = $derived(resources && resources.totalMemoryMb > 0 ? Math.round((100 * usedMb) / resources.totalMemoryMb) : 0);
  const lowMemory = $derived(resources !== null && resources.availableMemoryMb < resources.memoryReserveMb);

  const processors = $derived.by(() => {
    if (!resources) return "";
    const logical = `${resources.logicalProcessorCount} logical ${resources.logicalProcessorCount === 1 ? "processor" : "processors"}`;
    const cores = resources.physicalCoreCount;
    return cores ? `${cores} ${cores === 1 ? "core" : "cores"}, ${logical}` : logical;
  });

  async function load() {
    if (loading || document.hidden) return;
    loading = true;
    try {
      resources = await getHostResources(host.key);
    } catch {
      // The figures are information only: a host that cannot answer shows nothing rather than stale numbers.
      resources = null;
    } finally {
      loading = false;
    }
  }

  onMount(() => {
    void load();
    timer = setInterval(() => void load(), POLL_MS);
  });
  onDestroy(() => clearInterval(timer));
</script>

{#if resources}
  <section class="usage" aria-label="Host usage">
    <div class="stat">
      <div class="label">
        <span>Processor</span>
        {#if cpu !== null}<strong>{Math.round(cpu)}%</strong>{/if}
      </div>
      {#if cpu !== null}
        <progress max="100" value={cpu} aria-label="Processor use"></progress>
      {/if}
      <span class="detail">{processors}</span>
    </div>
    <div class="stat">
      <div class="label">
        <span>Memory</span>
        <strong>{memoryPercent}%</strong>
      </div>
      <progress class:low={lowMemory} max="100" value={memoryPercent} aria-label="Memory use"></progress>
      <span class="detail" class:warn={lowMemory}>
        {formatBytes(usedMb * MB)} of {formatBytes(resources.totalMemoryMb * MB)} in use, {formatBytes(resources.availableMemoryMb * MB)} free{#if lowMemory}
          (below the {formatBytes(resources.memoryReserveMb * MB)} the host keeps for itself){/if}
      </span>
    </div>
  </section>
{/if}

<style>
  .usage {
    display: grid;
    grid-template-columns: repeat(auto-fit, minmax(220px, 1fr));
    gap: 0.75rem 1.5rem;
    margin-bottom: 0.75rem;
    padding: 0.75rem 1rem;
    border: 1px solid var(--border);
    border-radius: 8px;
    background: var(--surface);
  }

  .stat {
    display: grid;
    gap: 0.3rem;
    min-width: 0;
  }

  .label {
    display: flex;
    justify-content: space-between;
    font-size: 0.9rem;
  }

  progress {
    width: 100%;
    height: 0.5rem;
    accent-color: var(--accent);
  }

  progress.low {
    accent-color: var(--warn-fg);
  }

  .detail {
    color: var(--muted);
    font-size: 0.85rem;
  }

  .detail.warn {
    color: var(--warn-fg);
  }
</style>
