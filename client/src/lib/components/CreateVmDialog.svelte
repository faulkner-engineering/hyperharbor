<script lang="ts">
  import { onMount } from "svelte";
  import {
    createVm,
    errorMessage,
    getHostResources,
    hasProblemCode,
    isClientError,
    listIsos,
    listSwitches,
    ProblemCodes,
    type HostEntry,
    type HostResources,
    type IsoImage,
    type ValidationIssue,
    type VirtualSwitch,
    type VmJob,
  } from "$lib/api/client";
  import { ElevationCancelled, toGb, toMb, waitForJob, withElevation } from "$lib/lifecycle.svelte";
  import Dialog from "./Dialog.svelte";
  import JobProgress from "./JobProgress.svelte";

  interface Props {
    host: HostEntry;
    onclose: (created: boolean) => void;
    /** Shown when the library is empty: closes this dialog and opens the ISO library. */
    onopenlibrary?: () => void;
  }

  let { host, onclose, onopenlibrary }: Props = $props();

  let resources = $state<HostResources | null>(null);
  let isos = $state<IsoImage[]>([]);
  let switches = $state<VirtualSwitch[]>([]);
  let loadError = $state<string | null>(null);

  let name = $state("");
  let isoName = $state("");
  let diskSizeGb = $state(64);
  let processorCount = $state(2);
  let startupMemoryGb = $state(4);
  let maximumMemoryGb = $state(8);
  let dynamicMemory = $state(true);
  let switchId = $state<string | null>(null);
  let enableTpm = $state(true);

  let busy = $state(false);
  let error = $state<string | null>(null);
  let issues = $state<ValidationIssue[]>([]);
  let warnings = $state<ValidationIssue[]>([]);
  let job = $state<VmJob | null>(null);

  const finished = $derived(job !== null && job.state !== "running");
  const canSubmit = $derived(name.trim() !== "" && isoName !== "" && !busy && job === null && resources !== null);

  onMount(async () => {
    try {
      [resources, isos, switches] = await Promise.all([
        getHostResources(host.key),
        listIsos(host.key),
        listSwitches(host.key),
      ]);
      processorCount = Math.min(2, resources.logicalProcessorCount);
      isoName = isos[0]?.name ?? "";
      switchId = switches.find((item) => item.isDefault)?.id ?? switches[0]?.id ?? null;
    } catch (e) {
      loadError = errorMessage(e);
    }
  });

  function issueFor(field: string): string | undefined {
    return issues.find((issue) => issue.field === field)?.message;
  }

  function formatSize(bytes: number): string {
    return bytes >= 1024 ** 3 ? `${(bytes / 1024 ** 3).toFixed(1)} GB` : `${Math.round(bytes / 1024 ** 2)} MB`;
  }

  async function submit(event: SubmitEvent) {
    event.preventDefault();
    await create(false);
  }

  async function create(acknowledgeWarnings: boolean) {
    busy = true;
    error = null;
    issues = [];
    const startupMemoryMb = toMb(startupMemoryGb);
    const request = {
      name: name.trim(),
      isoName,
      diskSizeGb,
      processorCount,
      startupMemoryMb,
      maximumMemoryMb: dynamicMemory ? Math.max(toMb(maximumMemoryGb), startupMemoryMb) : startupMemoryMb,
      dynamicMemory,
      switchId,
      enableTpm,
      acknowledgeWarnings,
    };
    try {
      const started = await withElevation(host.key, () => createVm(host.key, request));
      warnings = [];
      await waitForJob(host.key, started, (update) => (job = update));
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

<Dialog title="New virtual machine on {host.displayName}" wide>
  <!-- Any edit after a warning means the next Create checks the new values again. -->
  <form onsubmit={submit} oninput={() => (warnings = [])}>
    {#if loadError}
      <p class="error" role="alert">{loadError}</p>
    {:else if resources === null}
      <p class="muted">Reading the host's resources…</p>
    {:else}
      <p class="muted">
        Generation 2 with Secure Boot, a new dynamic disk, and the ISO first in the boot order. The VM is
        created off; start it and install the operating system from the host's console.
      </p>

      <label for="vm-name">Name</label>
      <input id="vm-name" bind:value={name} maxlength="100" autocomplete="off" spellcheck="false" disabled={job !== null} />
      {#if issueFor("name")}<p class="error">{issueFor("name")}</p>{/if}

      <label for="vm-iso">Installation image</label>
      {#if isos.length === 0}
        <div class="warning">
          <p>The host's ISO library is empty. Add an installation image first.</p>
          {#if onopenlibrary}
            <button type="button" onclick={onopenlibrary}>Open ISO library</button>
          {/if}
        </div>
      {:else}
        <select id="vm-iso" bind:value={isoName} disabled={job !== null}>
          {#each isos as iso (iso.name)}
            <option value={iso.name}>{iso.name} ({formatSize(iso.sizeBytes)})</option>
          {/each}
        </select>
      {/if}
      {#if issueFor("isoName")}<p class="error">{issueFor("isoName")}</p>{/if}

      <div class="grid">
        <div>
          <label for="vm-cpu">Virtual processors (host has {resources.logicalProcessorCount})</label>
          <input id="vm-cpu" type="number" min="1" max={resources.logicalProcessorCount} bind:value={processorCount} disabled={job !== null} />
          {#if issueFor("processorCount")}<p class="error">{issueFor("processorCount")}</p>{/if}
        </div>
        <div>
          <label for="vm-disk">Disk size (GB)</label>
          <input id="vm-disk" type="number" min="1" max="65536" bind:value={diskSizeGb} disabled={job !== null} />
          {#if issueFor("diskSizeGb")}<p class="error">{issueFor("diskSizeGb")}</p>{/if}
        </div>
        <div>
          <label for="vm-startup">{dynamicMemory ? "Startup memory (GB)" : "Memory (GB)"}</label>
          <input id="vm-startup" type="number" min="0.1" step="any" bind:value={startupMemoryGb} disabled={job !== null} />
          {#if issueFor("startupMemoryMb")}<p class="error">{issueFor("startupMemoryMb")}</p>{/if}
        </div>
        <div>
          <label for="vm-maximum">Maximum memory (GB)</label>
          <input id="vm-maximum" type="number" min="0.1" step="any" bind:value={maximumMemoryGb} disabled={!dynamicMemory || job !== null} />
          {#if issueFor("maximumMemoryMb")}<p class="error">{issueFor("maximumMemoryMb")}</p>{/if}
        </div>
      </div>
      <p class="muted">
        The host has {toGb(resources.totalMemoryMb)} GB of memory, {toGb(resources.availableMemoryMb)} GB free now.
      </p>

      <label class="check">
        <input type="checkbox" bind:checked={dynamicMemory} disabled={job !== null} />
        Dynamic memory
      </label>
      <label class="check">
        <input type="checkbox" bind:checked={enableTpm} disabled={job !== null} />
        Virtual TPM (Windows 11 needs one)
      </label>

      <label for="vm-switch">Network</label>
      {#if switches.length === 0}
        <p class="muted">The host has no virtual switches, so the VM will have no network adapter.</p>
      {:else}
        <select id="vm-switch" bind:value={switchId} disabled={job !== null}>
          {#each switches as item (item.id)}
            <option value={item.id}>{item.name}{item.isDefault ? " (reachable only from the host)" : ""}</option>
          {/each}
        </select>
      {/if}
      {#if issueFor("switchId")}<p class="error">{issueFor("switchId")}</p>{/if}
    {/if}

    {#if warnings.length > 0}
      <div class="warning" role="alert">
        {#each warnings as warning (warning.field + warning.message)}<p>{warning.message}</p>{/each}
      </div>
    {/if}
    {#if job}<JobProgress {job} />{/if}
    {#if error}<p class="error" role="alert">{error}</p>{/if}

    <div class="actions">
      {#if finished}
        <button type="button" class="primary" onclick={() => onclose(job?.state === "succeeded")}>Close</button>
      {:else}
        <button type="button" onclick={() => onclose(false)} disabled={busy}>Cancel</button>
        {#if warnings.length > 0}
          <button type="button" class="primary" disabled={busy} onclick={() => create(true)}>Create anyway</button>
        {:else}
          <button type="submit" class="primary" disabled={!canSubmit}>{busy ? "Creating…" : "Create"}</button>
        {/if}
      {/if}
    </div>
  </form>
</Dialog>

<style>
  .grid {
    display: grid;
    grid-template-columns: 1fr 1fr;
    gap: 0.4rem 1rem;
    margin-top: 0.25rem;
  }

  .grid > div {
    display: flex;
    flex-direction: column;
    gap: 0.3rem;
  }

  .warning p {
    margin: 0.2rem 0;
  }
</style>
