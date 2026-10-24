<script lang="ts">
  import { DEFAULT_SHARE, type MachineDraft } from "$lib/machineDraft.svelte";
  import Choice from "./Choice.svelte";

  interface Props {
    draft: MachineDraft;
  }

  let { draft }: Props = $props();

  const SHARES = [
    { key: "vramPercent", label: "Video memory" },
    { key: "computePercent", label: "Compute" },
    { key: "encodePercent", label: "Video encode" },
    { key: "decodePercent", label: "Video decode" },
  ] as const;

  const mode = $derived(draft.gpu === null ? null : draft.gpu.enabled);

  function setMode(enabled: boolean | null) {
    draft.gpu = enabled === null ? null : { ...(draft.gpu ?? {}), enabled, share: draft.gpu?.share ?? { ...DEFAULT_SHARE } };
    if (enabled && draft.compute.memory) draft.compute.memory.dynamic = false;
  }
</script>

{#if draft.os !== "windows"}
  <p class="muted">Performance mode (a share of the host's GPU) is for Windows VMs.</p>
{:else}
  <Choice
    label="Performance mode"
    options={[
      { value: null, label: "As it is" },
      { value: true, label: "On" },
      { value: false, label: "Off" },
    ]}
    value={mode}
    onchange={setMode}
    hint="A share of the host's GPU, fixed memory, and Remote Desktop tuned for games and video. Turning it on fixes the memory in Compute."
  />
  {#if draft.gpu?.enabled}
    {@const gpu = draft.gpu}
    {@const share = (gpu.share ?? DEFAULT_SHARE) as Required<typeof DEFAULT_SHARE>}
    <label>
      GPU
      <input value={gpu.adapter ?? ""} oninput={(event) => (gpu.adapter = event.currentTarget.value || null)} placeholder="The host's first partitionable GPU" />
    </label>
    {#each SHARES as item (item.key)}
      <label class="share">
        <span>{item.label}<span class="percent">{share[item.key]}%</span></span>
        <input
          type="range"
          min="1"
          max="100"
          value={share[item.key]}
          oninput={(event) => (gpu.share = { ...share, [item.key]: Number(event.currentTarget.value) })}
        />
      </label>
    {/each}
    <label class="check">
      <input type="checkbox" checked={gpu.hardwareEncoding === true} onchange={(event) => (gpu.hardwareEncoding = event.currentTarget.checked)} />
      Prefer the GPU for Remote Desktop's video encoding (experimental)
    </label>
  {/if}
{/if}

<style>
  label:not(.check) {
    display: flex;
    flex-direction: column;
    gap: 0.2rem;
    margin-bottom: 0.6rem;
  }

  .share span {
    display: flex;
    justify-content: space-between;
  }

  .percent {
    font-family: ui-monospace, "Cascadia Mono", Consolas, monospace;
    color: var(--accent);
  }

  input[type="range"] {
    accent-color: var(--accent);
  }
</style>
