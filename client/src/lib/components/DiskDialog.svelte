<script lang="ts">
  import { onDestroy, onMount } from "svelte";
  import {
    errorMessage,
    growVmDisk,
    hasProblemCode,
    isClientError,
    listVmDisks,
    ProblemCodes,
    type HostEntry,
    type ValidationIssue,
    type Vm,
    type VmDisk,
    type VmJob,
  } from "$lib/api/client";
  import { ElevationCancelled, waitForJob, withElevation } from "$lib/lifecycle.svelte";
  import Dialog from "./Dialog.svelte";
  import JobProgress from "./JobProgress.svelte";

  interface Props {
    host: HostEntry;
    vm: Vm;
    onclose: (changed: boolean) => void;
  }

  let { host, vm, onclose }: Props = $props();

  const GB = 1024 ** 3;
  /** Reading the disks normally takes a second or two. */
  const SLOW_MS = 20_000;

  let disks = $state<VmDisk[] | null>(null);
  let loadError = $state<string | null>(null);
  let slow = $state(false);
  let selectedPath = $state("");
  let sizeGb = $state(0);
  let extendPartition = $state(true);
  let shutDownToApply = $state(false);

  let busy = $state(false);
  let error = $state<string | null>(null);
  let issues = $state<ValidationIssue[]>([]);
  let warnings = $state<ValidationIssue[]>([]);
  let job = $state<VmJob | null>(null);
  /** Set when the dialog closes, so a late answer does nothing. */
  let dismissed = false;
  const slowTimer = setTimeout(() => (slow = true), SLOW_MS);

  onMount(async () => {
    try {
      const list = await listVmDisks(host.key, vm.id);
      if (dismissed) return;
      disks = list.disks;
      if (list.disks.length > 0) select(list.disks[0].path);
    } catch (e) {
      if (!dismissed) loadError = errorMessage(e);
    } finally {
      clearTimeout(slowTimer);
    }
  });

  onDestroy(() => {
    dismissed = true;
    clearTimeout(slowTimer);
  });

  const gb = (bytes: number) => Math.round((bytes / GB) * 10) / 10;
  const disk = $derived(disks?.find((item) => item.path === selectedPath) ?? null);
  const currentGb = $derived(disk ? gb(disk.maximumSizeBytes) : 0);
  const running = $derived(vm.state === "running");
  const needsOff = $derived(running && disk !== null && !disk.growWhileRunning);
  const finished = $derived(job !== null && job.state !== "running");
  const canGrow = $derived(
    disk !== null && disk.growBlocker === null && sizeGb > currentGb && !busy && job === null && (!needsOff || shutDownToApply),
  );

  const blockers = $derived<Record<NonNullable<VmDisk["growBlocker"]>, string>>({
    checkpoints: "This VM has checkpoints, and Hyper-V cannot grow a disk that has them. Merge its checkpoints first.",
    differencing: "This is a differencing disk; only the disk it is based on can grow.",
    notFound: "Hyper-V cannot read this disk file.",
    vmState: `${vm.name} is ${vm.state}. A disk can grow while the VM is off or running.`,
  });

  function select(path: string) {
    selectedPath = path;
    const chosen = disks?.find((item) => item.path === path);
    sizeGb = chosen ? Math.ceil(chosen.maximumSizeBytes / GB) * 2 : 0;
    warnings = [];
  }

  function close() {
    dismissed = true;
    onclose(job !== null);
  }

  async function submit(event: SubmitEvent) {
    event.preventDefault();
    await grow(false);
  }

  async function grow(acknowledgeWarnings: boolean) {
    if (!disk) return;
    busy = true;
    error = null;
    issues = [];
    const request = { path: disk.path, sizeGb, extendPartition, shutDownToApply: needsOff && shutDownToApply, acknowledgeWarnings };
    try {
      const started = await withElevation(host.key, () => growVmDisk(host.key, vm.id, request));
      if (dismissed) return;
      warnings = [];
      // The host keeps the job running if the dialog closes; it is listed again like any other job.
      await waitForJob(host.key, started, (next) => {
        if (!dismissed) job = next;
      });
    } catch (e) {
      if (dismissed || e instanceof ElevationCancelled) return;
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

<Dialog title="Disk size for {vm.name}">
  <form onsubmit={submit} oninput={() => (warnings = [])}>
    {#if loadError}
      <p class="error" role="alert">{loadError}</p>
    {:else if disks === null}
      <p class="muted">Reading the VM's disks…</p>
      {#if slow}<p class="muted">This is taking longer than usual; the host may be busy. You can close this.</p>{/if}
    {:else if disks.length === 0}
      <p class="muted">{vm.name} has no virtual hard disks.</p>
    {:else}
      {#if disks.length > 1}
        <label for="disk-path">Disk</label>
        <select id="disk-path" value={selectedPath} onchange={(event) => select(event.currentTarget.value)} disabled={job !== null}>
          {#each disks as item (item.path)}
            <option value={item.path}>{item.path} ({gb(item.maximumSizeBytes)} GB)</option>
          {/each}
        </select>
      {/if}
      {#if disk}
        <p class="muted path">{disk.path}</p>
        <p>
          Maximum size {currentGb} GB; the file takes {gb(disk.fileSizeBytes)} GB now.
        </p>
        {#if disk.growBlocker}
          <p class="warning">{blockers[disk.growBlocker]}</p>
        {:else}
          <label for="disk-size">New maximum size (GB)</label>
          <input id="disk-size" type="number" min={Math.floor(currentGb) + 1} max="65536" step="1" bind:value={sizeGb} disabled={job !== null} />
          <p class="muted">A disk can only grow. A dynamic disk takes space on the host's drive only as the VM fills it.</p>
          {#each ["sizeGb", "path"] as field (field)}
            {#each issues.filter((issue) => issue.field === field) as issue (issue.message)}<p class="error">{issue.message}</p>{/each}
          {/each}

          <label class="check">
            <input type="checkbox" bind:checked={extendPartition} disabled={job !== null} />
            Also extend the partition at the end of the disk (for Windows, usually C:)
          </label>
          {#if vm.guestOs.family === "linux"}
            <p class="muted">For a Linux VM, the host says afterwards which commands extend the file system.</p>
          {/if}

          {#if needsOff && job === null}
            <p class="warning">This disk can grow only while {vm.name} is off.</p>
            <label class="check">
              <input type="checkbox" bind:checked={shutDownToApply} disabled={busy} />
              Shut {vm.name} down, grow the disk, and start it again
            </label>
          {/if}
        {/if}
      {/if}
    {/if}

    {#if warnings.length > 0}
      <div class="warning" role="alert">
        {#each warnings as warning (warning.field + warning.message)}<p>{warning.message}</p>{/each}
      </div>
    {/if}
    {#if job}<JobProgress {job} />{/if}
    {#if finished && job?.state === "succeeded" && job.diskGrowResult}
      <p role="status">
        The disk is now {gb(job.diskGrowResult.maximumSizeBytes)} GB{job.diskGrowResult.partitionExtended ? ", and the partition uses the new space." : "."}
      </p>
      {#if job.diskGrowResult.note}<p class="warning">{job.diskGrowResult.note}</p>{/if}
    {/if}
    {#if error}<p class="error" role="alert">{error}</p>{/if}

    <div class="actions">
      {#if finished}
        <button type="button" class="primary" onclick={close}>Close</button>
      {:else}
        <button type="button" onclick={close}>{job ? "Close" : "Cancel"}</button>
        {#if warnings.length > 0}
          <button type="button" class="primary" disabled={busy} onclick={() => grow(true)}>Grow anyway</button>
        {:else if job === null}
          <button type="submit" class="primary" disabled={!canGrow}>{busy ? "Growing…" : "Grow disk"}</button>
        {/if}
      {/if}
    </div>
  </form>
</Dialog>

<style>
  .path {
    word-break: break-all;
    margin-bottom: 0.25rem;
  }

  .warning p {
    margin: 0.2rem 0;
  }
</style>
