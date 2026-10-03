<script lang="ts">
  import { onMount } from "svelte";
  import {
    deleteVm,
    errorMessage,
    getDeletePreview,
    type DeleteBlocker,
    type HostEntry,
    type Vm,
    type VmDeletePreview,
    type VmJob,
  } from "$lib/api/client";
  import { ElevationCancelled, waitForJob, withElevation } from "$lib/lifecycle.svelte";
  import Dialog from "./Dialog.svelte";
  import JobProgress from "./JobProgress.svelte";

  interface Props {
    host: HostEntry;
    vm: Vm;
    onclose: (deleted: boolean) => void;
  }

  let { host, vm, onclose }: Props = $props();

  let preview = $state<VmDeletePreview | null>(null);
  let loadError = $state<string | null>(null);
  let deleteDisks = $state(false);
  let deleteCheckpoints = $state(false);
  let confirmName = $state("");
  let busy = $state(false);
  let error = $state<string | null>(null);
  let job = $state<VmJob | null>(null);

  function applies(blocker: DeleteBlocker): boolean {
    switch (blocker.scope) {
      case "always":
        return true;
      case "deleteDisks":
        return deleteDisks;
      default:
        return deleteDisks || (deleteCheckpoints && (preview?.checkpointCount ?? 0) > 0);
    }
  }

  const blockers = $derived(preview?.blockers.filter(applies) ?? []);
  const needsCheckpoints = $derived((preview?.checkpointCount ?? 0) > 0);
  const finished = $derived(job !== null && job.state !== "running");
  const canDelete = $derived(
    preview !== null &&
      confirmName === preview.vmName &&
      blockers.length === 0 &&
      (!needsCheckpoints || deleteCheckpoints) &&
      !busy &&
      job === null,
  );

  onMount(async () => {
    try {
      preview = await getDeletePreview(host.key, vm.id);
    } catch (e) {
      loadError = errorMessage(e);
    }
  });

  async function submit(event: SubmitEvent) {
    event.preventDefault();
    if (!canDelete || preview === null) return;
    busy = true;
    error = null;
    try {
      const started = await withElevation(host.key, () =>
        deleteVm(host.key, vm.id, { deleteDisks, deleteCheckpoints, confirmName }),
      );
      await waitForJob(host.key, started, (update) => (job = update));
    } catch (e) {
      if (!(e instanceof ElevationCancelled)) error = errorMessage(e);
    } finally {
      busy = false;
    }
  }
</script>

<Dialog title="Delete {vm.name}">
  <form onsubmit={submit}>
    {#if loadError}
      <p class="error" role="alert">{loadError}</p>
    {:else if preview === null}
      <p class="muted">Checking what would be deleted…</p>
    {:else}
      <p class="muted">
        This removes the virtual machine from {host.displayName}. It cannot be undone.
      </p>

      {#if preview.disks.length > 0}
        <label class="check">
          <input type="checkbox" bind:checked={deleteDisks} disabled={busy || job !== null} />
          Also delete its virtual hard {preview.disks.length === 1 ? "disk" : "disks"}
        </label>
        <p class="muted list-label">{deleteDisks ? "These files will be deleted:" : "These files will be kept:"}</p>
        <ul class="files">
          {#each preview.disks as disk (disk)}<li>{disk}</li>{/each}
        </ul>
      {:else}
        <p class="muted">It has no virtual hard disk files to delete.</p>
      {/if}

      {#if needsCheckpoints}
        <label class="check">
          <input type="checkbox" bind:checked={deleteCheckpoints} disabled={busy || job !== null} />
          Delete its {preview.checkpointCount} {preview.checkpointCount === 1 ? "checkpoint" : "checkpoints"} (merged into its disks first; required)
        </label>
      {/if}

      {#each blockers as blocker (blocker.message)}
        <p class="warning" role="alert">{blocker.message}</p>
      {/each}

      <label for="confirm-name">Type <strong>{preview.vmName}</strong> to confirm</label>
      <input id="confirm-name" bind:value={confirmName} autocomplete="off" spellcheck="false" disabled={busy || job !== null} />
    {/if}

    {#if job}<JobProgress {job} />{/if}
    {#if error}<p class="error" role="alert">{error}</p>{/if}

    <div class="actions">
      {#if finished}
        <button type="button" class="primary" onclick={() => onclose(job?.state === "succeeded")}>Close</button>
      {:else}
        <button type="button" onclick={() => onclose(false)} disabled={busy}>Cancel</button>
        <button type="submit" class="danger" disabled={!canDelete}>{busy ? "Deleting…" : "Delete"}</button>
      {/if}
    </div>
  </form>
</Dialog>

<style>
  .files {
    margin: 0 0 0.5rem 1.6rem;
    padding: 0;
    font-family: ui-monospace, "Cascadia Mono", Consolas, monospace;
    font-size: 0.8rem;
    word-break: break-all;
  }

  .list-label {
    margin: 0 0 0.2rem 1.6rem;
  }
</style>
