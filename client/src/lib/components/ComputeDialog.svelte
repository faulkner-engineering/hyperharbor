<script lang="ts">
  import { onMount } from "svelte";
  import {
    errorMessage,
    getHostResources,
    getVmCompute,
    hasProblemCode,
    isClientError,
    ProblemCodes,
    updateVmCompute,
    type ComputeSetting,
    type HostEntry,
    type HostResources,
    type UpdateComputeRequest,
    type ValidationIssue,
    type Vm,
    type VmComputeSettings,
    type VmJob,
  } from "$lib/api/client";
  import { ElevationCancelled, toGb, toMb, waitForJob, withElevation } from "$lib/lifecycle.svelte";
  import Dialog from "./Dialog.svelte";
  import JobProgress from "./JobProgress.svelte";

  interface Props {
    host: HostEntry;
    vm: Vm;
    onclose: (changed: boolean) => void;
  }

  let { host, vm, onclose }: Props = $props();

  const settingNames: Record<ComputeSetting, string> = {
    processorCount: "Virtual processors",
    startupMemoryMb: "Startup memory",
    maximumMemoryMb: "Maximum memory",
    dynamicMemory: "Dynamic memory",
    nestedVirtualization: "Nested virtualization",
    macAddressSpoofing: "MAC address spoofing",
  };

  let current = $state<VmComputeSettings | null>(null);
  let resources = $state<HostResources | null>(null);
  let loadError = $state<string | null>(null);

  let processorCount = $state(1);
  let startupMemoryGb = $state(1);
  let maximumMemoryGb = $state(1);
  let dynamicMemory = $state(false);
  let nestedVirtualization = $state(false);
  let macAddressSpoofing = $state(false);
  let shutDownToApply = $state(false);

  let busy = $state(false);
  let error = $state<string | null>(null);
  let issues = $state<ValidationIssue[]>([]);
  let warnings = $state<ValidationIssue[]>([]);
  let job = $state<VmJob | null>(null);
  let saved = $state(false);

  onMount(async () => {
    try {
      [current, resources] = await Promise.all([getVmCompute(host.key, vm.id), getHostResources(host.key)]);
      reset(current);
    } catch (e) {
      loadError = errorMessage(e);
    }
  });

  function reset(settings: VmComputeSettings) {
    processorCount = settings.processorCount;
    // Exact, so an untouched value converts back to the same megabytes.
    startupMemoryGb = settings.startupMemoryMb / 1024;
    maximumMemoryGb = settings.maximumMemoryMb / 1024;
    dynamicMemory = settings.dynamicMemory;
    nestedVirtualization = settings.nestedVirtualization;
    macAddressSpoofing = settings.macAddressSpoofing;
  }

  /** Only the settings that differ from the VM's, so the host leaves the rest alone. */
  const request = $derived.by((): UpdateComputeRequest => {
    if (current === null) return {};
    const changes: UpdateComputeRequest = {};
    if (processorCount !== current.processorCount) changes.processorCount = processorCount;
    if (toMb(startupMemoryGb) !== current.startupMemoryMb) changes.startupMemoryMb = toMb(startupMemoryGb);
    if (dynamicMemory && toMb(maximumMemoryGb) !== current.maximumMemoryMb) changes.maximumMemoryMb = toMb(maximumMemoryGb);
    if (dynamicMemory !== current.dynamicMemory) changes.dynamicMemory = dynamicMemory;
    if (nestedVirtualization !== current.nestedVirtualization) changes.nestedVirtualization = nestedVirtualization;
    if (macAddressSpoofing !== current.macAddressSpoofing) changes.macAddressSpoofing = macAddressSpoofing;
    return changes;
  });

  const changed = $derived(Object.keys(request) as ComputeSetting[]);
  const needOff = $derived(changed.filter((setting) => current?.requiresOff.includes(setting)));
  const running = $derived(current?.state === "running");
  const locked = $derived(current !== null && current.state !== "running" && current.state !== "off");
  const finished = $derived(saved || (job !== null && job.state !== "running"));
  const canSave = $derived(
    changed.length > 0 && !busy && job === null && !saved && !locked && (!running || needOff.length === 0 || shutDownToApply),
  );

  function developerMode() {
    nestedVirtualization = true;
    dynamicMemory = false;
    if ((current?.networkAdapterCount ?? 0) > 0) macAddressSpoofing = true;
    warnings = [];
  }

  function issueFor(field: string): string | undefined {
    return issues.find((issue) => issue.field === field)?.message;
  }

  async function submit(event: SubmitEvent) {
    event.preventDefault();
    await save(false);
  }

  async function save(acknowledgeWarnings: boolean) {
    busy = true;
    error = null;
    issues = [];
    const body = { ...request, shutDownToApply: running && needOff.length > 0 && shutDownToApply, acknowledgeWarnings };
    try {
      const update = await withElevation(host.key, () => updateVmCompute(host.key, vm.id, body));
      warnings = [];
      if (update.settings) {
        current = update.settings;
        reset(update.settings);
        saved = true;
      } else if (update.job) {
        await waitForJob(host.key, update.job, (next) => (job = next));
      }
    } catch (e) {
      if (e instanceof ElevationCancelled) return;
      if (hasProblemCode(e, ProblemCodes.resourceWarnings) && isClientError(e)) {
        warnings = e.issues;
      } else {
        error = errorMessage(e);
        issues = isClientError(e) ? e.issues : [];
      }
    } finally {
      busy = false;
    }
  }
</script>

<Dialog title="Settings for {vm.name}" wide>
  <form onsubmit={submit} oninput={() => (warnings = [])}>
    {#if loadError}
      <p class="error" role="alert">{loadError}</p>
    {:else if current === null || resources === null}
      <p class="muted">Reading the VM's settings…</p>
    {:else}
      {#if locked}
        <p class="warning">{vm.name} is {current.state}. Settings can change only while it is off or running.</p>
      {:else if running}
        <p class="muted">Settings marked "needs off" can change only while the VM is off.</p>
      {/if}

      <div class="preset">
        <button type="button" onclick={developerMode} disabled={job !== null || saved}>Developer mode</button>
        <span class="muted">Nested virtualization on, dynamic memory off, MAC address spoofing on (for Docker, WSL, and nested VMs).</span>
      </div>

      <div class="grid">
        {@render field("processorCount", "vm-cpu")}
        <input id="vm-cpu" type="number" min="1" max={resources.logicalProcessorCount} bind:value={processorCount} disabled={job !== null || saved} />

        {@render field("startupMemoryMb", "vm-startup")}
        <input id="vm-startup" type="number" min="0.1" step="any" bind:value={startupMemoryGb} disabled={job !== null || saved} />

        {@render field("maximumMemoryMb", "vm-maximum")}
        <input id="vm-maximum" type="number" min="0.1" step="any" bind:value={maximumMemoryGb} disabled={!dynamicMemory || job !== null || saved} />
      </div>
      <p class="muted">
        Memory in GB. The host has {resources.logicalProcessorCount} logical processors and {toGb(resources.availableMemoryMb)} GB
        of memory free.
      </p>
      {#each ["processorCount", "startupMemoryMb", "maximumMemoryMb", "dynamicMemory"] as name (name)}
        {#if issueFor(name)}<p class="error">{issueFor(name)}</p>{/if}
      {/each}

      <label class="check">
        <input type="checkbox" bind:checked={dynamicMemory} disabled={job !== null || saved} />
        Dynamic memory {@render badge("dynamicMemory")}
      </label>
      <label class="check">
        <input type="checkbox" bind:checked={nestedVirtualization} disabled={job !== null || saved} />
        Nested virtualization (needs static memory) {@render badge("nestedVirtualization")}
      </label>
      <label class="check">
        <input type="checkbox" bind:checked={macAddressSpoofing} disabled={current.networkAdapterCount === 0 || job !== null || saved} />
        MAC address spoofing {current.networkAdapterCount === 0 ? "(no connected network adapter)" : ""}
      </label>
      {#if issueFor("macAddressSpoofing")}<p class="error">{issueFor("macAddressSpoofing")}</p>{/if}

      {#if running && needOff.length > 0 && job === null}
        <p class="warning">
          {needOff.map((setting) => settingNames[setting]).join(", ")}
          {needOff.length === 1 ? "needs" : "need"} the VM off.
        </p>
        <label class="check">
          <input type="checkbox" bind:checked={shutDownToApply} disabled={busy} />
          Shut {vm.name} down, apply the changes, and start it again
        </label>
      {/if}
    {/if}

    {#if warnings.length > 0}
      <div class="warning" role="alert">
        {#each warnings as warning (warning.field + warning.message)}<p>{warning.message}</p>{/each}
      </div>
    {/if}
    {#if saved}<p role="status">Saved.</p>{/if}
    {#if job}<JobProgress {job} />{/if}
    {#if error}<p class="error" role="alert">{error}</p>{/if}

    <div class="actions">
      {#if finished}
        <button type="button" class="primary" onclick={() => onclose(true)}>Close</button>
      {:else}
        <button type="button" onclick={() => onclose(false)} disabled={busy}>Cancel</button>
        {#if warnings.length > 0}
          <button type="button" class="primary" disabled={busy} onclick={() => save(true)}>Save anyway</button>
        {:else}
          <button type="submit" class="primary" disabled={!canSave}>{busy ? "Saving…" : "Save"}</button>
        {/if}
      {/if}
    </div>
  </form>
</Dialog>

{#snippet badge(setting: ComputeSetting)}
  {#if running && current?.requiresOff.includes(setting)}<span class="badge">needs off</span>{/if}
{/snippet}

{#snippet field(setting: ComputeSetting, id: string)}
  <label for={id}>{settingNames[setting]} {@render badge(setting)}</label>
{/snippet}

<style>
  .preset {
    display: flex;
    align-items: center;
    gap: 0.75rem;
    margin-bottom: 0.5rem;
  }

  .preset .muted {
    margin: 0;
  }

  .grid {
    display: grid;
    grid-template-columns: max-content 1fr;
    align-items: center;
    gap: 0.4rem 1rem;
  }

  .badge {
    display: inline-block;
    margin-left: 0.35rem;
    padding: 0 0.4rem;
    border-radius: 999px;
    font-size: 0.75rem;
    background: var(--warn-bg);
    color: var(--warn-fg);
  }

  .warning p {
    margin: 0.2rem 0;
  }
</style>
