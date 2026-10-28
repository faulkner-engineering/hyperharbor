<script lang="ts">
  import { onDestroy, onMount } from "svelte";
  import {
    errorMessage,
    getVmDiskEncryption,
    turnOffVmDiskEncryption,
    type GuestVolumeEncryption,
    type HostEntry,
    type Vm,
    type VmJob,
  } from "$lib/api/client";
  import { ElevationCancelled, waitForJob, withElevation } from "$lib/lifecycle.svelte";
  import { canTurnOff, inProgress, volumeLine } from "$lib/diskEncryption";
  import Dialog from "./Dialog.svelte";
  import JobProgress from "./JobProgress.svelte";

  interface Props {
    host: HostEntry;
    vm: Vm;
    onclose: (changed: boolean) => void;
  }

  let { host, vm, onclose }: Props = $props();

  /** Reading the state opens a PowerShell Direct session, which takes a few seconds. */
  const SLOW_MS = 20_000;
  /** While Windows converts a volume, the state is read again this often. */
  const POLL_MS = 10_000;

  let volumes = $state<GuestVolumeEncryption[] | null>(null);
  let loadError = $state<string | null>(null);
  let slow = $state(false);
  let busy = $state(false);
  let error = $state<string | null>(null);
  let job = $state<VmJob | null>(null);
  /** Set when the dialog closes, so a late answer does nothing. */
  let dismissed = false;
  let pollTimer: ReturnType<typeof setTimeout> | undefined;
  const slowTimer = setTimeout(() => (slow = true), SLOW_MS);

  onMount(read);

  onDestroy(() => {
    dismissed = true;
    clearTimeout(slowTimer);
    clearTimeout(pollTimer);
  });

  async function read() {
    try {
      const state = await getVmDiskEncryption(host.key, vm.id);
      if (dismissed) return;
      volumes = state.volumes;
      loadError = null;
    } catch (e) {
      // A failed refresh keeps the last state on screen; only the first read shows the error alone.
      if (!dismissed) loadError = errorMessage(e);
    } finally {
      clearTimeout(slowTimer);
      slow = false;
    }
    if (!dismissed && volumes && inProgress(volumes)) pollTimer = setTimeout(read, POLL_MS);
  }

  async function turnOff() {
    busy = true;
    error = null;
    try {
      const started = await withElevation(host.key, () => turnOffVmDiskEncryption(host.key, vm.id));
      if (dismissed) return;
      // The host keeps the job running if the dialog closes; it is listed again like any other job.
      const done = await waitForJob(host.key, started, (next) => {
        if (!dismissed) job = next;
      });
      if (dismissed) return;
      if (done.diskEncryption) volumes = done.diskEncryption.volumes;
      clearTimeout(pollTimer);
      if (volumes && inProgress(volumes)) pollTimer = setTimeout(read, POLL_MS);
    } catch (e) {
      if (dismissed || e instanceof ElevationCancelled) return;
      error = errorMessage(e);
    } finally {
      busy = false;
    }
  }

  function close() {
    dismissed = true;
    onclose(job !== null);
  }

  const problems = $derived(job?.state === "succeeded" ? (job.diskEncryption?.problems ?? []) : []);
</script>

<Dialog title="Disk encryption in {vm.name}">
  {#if volumes === null && loadError}
    <p class="error" role="alert">{loadError}</p>
  {:else if volumes === null}
    <p class="muted">Asking Windows in {vm.name}…</p>
    {#if slow}<p class="muted">This is taking longer than usual; the VM may still be starting. You can close this.</p>{/if}
  {:else}
    {#if volumes.length === 0}
      <p class="muted">Windows in {vm.name} reported no fixed volumes.</p>
    {:else}
      <ul class="volumes">
        {#each volumes as volume (volume.mountPoint)}<li>{volumeLine(volume)}</li>{/each}
      </ul>
    {/if}

    {#if canTurnOff(volumes) && job === null}
      <p>
        BitLocker makes every backup of this VM larger, because encrypted data does not compress, and a restored or moved
        copy can ask for its recovery key. Turning it off decrypts the volumes in the background: the VM stays usable, and
        decrypting continues after a restart. It takes from a few minutes to an hour, depending on the disk.
      </p>
      <p class="muted">The next backup uploads the whole disk once; later backups are smaller.</p>
    {:else if inProgress(volumes)}
      <p class="muted">Windows is converting the volumes; this updates every few seconds. You can close this, and it goes on.</p>
    {:else if volumes.length > 0}
      <p class="muted">No volume is encrypted.</p>
    {/if}
    {#if loadError}<p class="warning">The latest refresh failed: {loadError}</p>{/if}
  {/if}

  {#if job}<JobProgress {job} />{/if}
  {#each problems as problem (problem)}<p class="warning">{problem}</p>{/each}
  {#if error}<p class="error" role="alert">{error}</p>{/if}

  <div class="actions">
    <button type="button" class={job || !canTurnOff(volumes ?? []) ? "primary" : ""} onclick={close}>Close</button>
    {#if volumes && canTurnOff(volumes) && job === null}
      <button type="button" class="primary" disabled={busy} onclick={turnOff}>{busy ? "Turning off…" : "Turn off encryption"}</button>
    {/if}
  </div>
</Dialog>

<style>
  .volumes {
    margin: 0 0 0.75rem;
    padding-left: 1.2rem;
  }
</style>
