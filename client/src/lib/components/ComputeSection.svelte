<script lang="ts">
  import type { MachineDraft } from "$lib/machineDraft.svelte";
  import Choice from "./Choice.svelte";

  interface Props {
    draft: MachineDraft;
  }

  let { draft }: Props = $props();

  const compute = $derived(draft.compute);

  /** A whole number from a field, or null when empty. */
  function whole(text: string): number | null {
    const value = Number.parseInt(text, 10);
    return Number.isFinite(value) ? value : null;
  }

  /** Gigabytes typed by the user, as megabytes Hyper-V accepts (even). */
  function megabytes(text: string): number | null {
    const value = Number.parseFloat(text);
    return Number.isFinite(value) && value > 0 ? Math.round((value * 1024) / 2) * 2 : null;
  }

  function setMemory(on: boolean) {
    compute.memory = on ? { startupMb: 4096, maximumMb: 8192, dynamic: true } : null;
  }
</script>

<p class="muted">Empty fields use the host's defaults when creating a VM, and leave an existing VM as it is.</p>
<div class="grid">
  <label>
    Virtual processors
    <input type="number" min="1" max="240" value={compute.processors ?? ""} oninput={(event) => (compute.processors = whole(event.currentTarget.value))} placeholder="Host default" />
  </label>
  <label>
    Disk size (GB)
    <input type="number" min="1" max="65536" value={compute.diskSizeGb ?? ""} oninput={(event) => (compute.diskSizeGb = whole(event.currentTarget.value))} placeholder="Host default" />
  </label>
</div>
<small class="muted">The disk size is used when creating a VM; on an existing VM only a larger size is offered.</small>

<label class="check memory">
  <input type="checkbox" checked={compute.memory != null} onchange={(event) => setMemory(event.currentTarget.checked)} />
  Set memory
</label>
{#if compute.memory}
  {@const memory = compute.memory}
  <div class="grid">
    <label>
      {memory.dynamic === false ? "Memory (GB)" : "Startup memory (GB)"}
      <input type="number" min="0.5" step="0.5" value={memory.startupMb / 1024} oninput={(event) => (memory.startupMb = megabytes(event.currentTarget.value) ?? memory.startupMb)} />
    </label>
    {#if memory.dynamic !== false}
      <label>
        Maximum memory (GB)
        <input type="number" min="0.5" step="0.5" value={memory.maximumMb != null ? memory.maximumMb / 1024 : ""} oninput={(event) => (memory.maximumMb = megabytes(event.currentTarget.value))} placeholder="No limit" />
      </label>
    {/if}
  </div>
  <label class="check"><input type="checkbox" checked={memory.dynamic !== false} onchange={(event) => (memory.dynamic = event.currentTarget.checked)} /> Dynamic memory</label>
  {#if draft.gpu?.enabled && memory.dynamic !== false}
    <p class="warning">Performance mode needs fixed memory. Turn dynamic memory off.</p>
  {/if}
{/if}

<Choice
  label="Nested virtualization"
  options={[
    { value: null, label: "As it is" },
    { value: true, label: "On" },
    { value: false, label: "Off" },
  ]}
  bind:value={compute.nestedVirtualization}
  hint="Lets the VM run its own VMs, WSL 2, or Docker Desktop. Needs the VM off to change."
/>

<style>
  .grid {
    display: grid;
    grid-template-columns: repeat(auto-fit, minmax(10rem, 1fr));
    gap: 0 0.75rem;
  }

  label:not(.check) {
    display: flex;
    flex-direction: column;
    gap: 0.2rem;
    margin-bottom: 0.4rem;
  }

  .memory {
    margin-top: 0.8rem !important;
  }
</style>
