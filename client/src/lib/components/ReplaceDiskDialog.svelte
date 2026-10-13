<script lang="ts">
  import {
    errorMessage,
    replaceVmDisk,
    scanBackups,
    type BackupCatalog,
    type BackupTarget,
    type HostEntry,
    type Vm,
    type VmJob,
  } from "$lib/api/client";
  import { snapshotLabel, targetReady } from "$lib/backup";
  import { ElevationCancelled, waitForJob, withElevation } from "$lib/lifecycle.svelte";
  import Dialog from "./Dialog.svelte";
  import JobProgress from "./JobProgress.svelte";

  interface Props {
    host: HostEntry;
    vm: Vm;
    targets: BackupTarget[];
    onclose: () => void;
  }

  let { host, vm, targets, onclose }: Props = $props();

  const usable = $derived(targets.filter(targetReady));

  let pickedTarget = $state("");
  const targetId = $derived(usable.some((t) => t.id === pickedTarget) ? pickedTarget : (usable[0]?.id ?? ""));
  let catalog = $state<BackupCatalog | null>(null);
  let pickedSnapshot = $state("");
  let typedName = $state("");
  let busy = $state(false);
  let error = $state<string | null>(null);
  let job = $state<VmJob | null>(null);

  // Only this VM's own backups fit its disks.
  const snapshots = $derived(catalog?.vms.find((entry) => entry.vmId === vm.id)?.snapshots ?? []);
  const nameMatches = $derived(typedName === vm.name);
  const finished = $derived(job !== null && job.state !== "running");

  const snapshotId = $derived(snapshots.some((s) => s.id === pickedSnapshot) ? pickedSnapshot : (snapshots[0]?.id ?? ""));

  async function scan() {
    busy = true;
    error = null;
    try {
      catalog = await withElevation(host.key, () => scanBackups(host.key, targetId));
    } catch (e) {
      if (!(e instanceof ElevationCancelled)) error = errorMessage(e);
    } finally {
      busy = false;
    }
  }

  async function replace(event: SubmitEvent) {
    event.preventDefault();
    if (!nameMatches || !snapshotId || busy) return;
    busy = true;
    error = null;
    try {
      const started = await withElevation(host.key, () => replaceVmDisk(host.key, vm.id, targetId, snapshotId, typedName));
      await waitForJob(host.key, started, (update) => (job = update));
    } catch (e) {
      if (!(e instanceof ElevationCancelled)) error = errorMessage(e);
    } finally {
      busy = false;
    }
  }
</script>

<Dialog title="Replace the disk of {vm.name} from a backup">
  {#if vm.state !== "off"}
    <p class="warning">Turn {vm.name} off first. A disk cannot be replaced while the VM runs.</p>
    <div class="actions"><button type="button" onclick={onclose}>Close</button></div>
  {:else if usable.length === 0}
    <p class="muted">No backup target is ready. Add a target and save its recovery key first.</p>
    <div class="actions"><button type="button" onclick={onclose}>Close</button></div>
  {:else if catalog === null}
    <label for="rd-target">Backup target</label>
    <select id="rd-target" bind:value={pickedTarget} disabled={busy}>
      {#each usable as target (target.id)}<option value={target.id}>{target.name}</option>{/each}
    </select>
    {#if error}<p class="error" role="alert">{error}</p>{/if}
    <div class="actions">
      <button type="button" onclick={onclose} disabled={busy}>Cancel</button>
      <button type="button" class="primary" onclick={scan} disabled={busy}>{busy ? "Looking…" : "Look for backups"}</button>
    </div>
  {:else if snapshots.length === 0}
    <p class="muted">This target holds no backup of {vm.name}.</p>
    <div class="actions"><button type="button" onclick={onclose}>Close</button></div>
  {:else}
    <form onsubmit={replace}>
      <label for="rd-snap">Backup to put back</label>
      <select id="rd-snap" bind:value={pickedSnapshot} disabled={busy || job !== null}>
        {#each snapshots as snapshot (snapshot.id)}<option value={snapshot.id}>{snapshotLabel(snapshot)}</option>{/each}
      </select>
      <p class="warning">
        Everything saved in {vm.name} since that backup is lost. The old disk is kept until the new one is in place, and put
        back if anything fails.
      </p>
      <label for="rd-name">Type the VM's name, <strong>{vm.name}</strong>, to confirm</label>
      <input id="rd-name" type="text" bind:value={typedName} disabled={busy || job !== null} autocomplete="off" />

      {#if job}<JobProgress {job} />{/if}
      {#if job?.state === "succeeded"}<p role="status">The disk was replaced.</p>{/if}
      {#if error}<p class="error" role="alert">{error}</p>{/if}
      <div class="actions">
        {#if finished}
          <button type="button" class="primary" onclick={onclose}>Close</button>
        {:else}
          <button type="button" onclick={onclose} disabled={busy}>Cancel</button>
          <button type="submit" class="danger" disabled={!nameMatches || busy || job !== null}>
            {busy ? "Replacing…" : "Replace the disk"}
          </button>
        {/if}
      </div>
    </form>
  {/if}
</Dialog>
