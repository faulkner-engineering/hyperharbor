<script lang="ts">
  import { onDestroy, onMount } from "svelte";
  import {
    closeBackupBrowse,
    downloadBackupBrowseFile,
    errorMessage,
    getBackupBrowse,
    listBackupBrowse,
    openBackupBrowse,
    type BackupBrowseEntry,
    type BackupBrowseSession,
    type HostEntry,
  } from "$lib/api/client";
  import { crumbs, formatBytes, joinPath, volumeLabel } from "$lib/backup";
  import { ElevationCancelled, withElevation } from "$lib/lifecycle.svelte";
  import Dialog from "./Dialog.svelte";

  interface Props {
    host: HostEntry;
    targetId: string;
    snapshotId: string;
    /** The VM's name, for the title. */
    vmName: string;
    onclose: () => void;
  }

  let { host, targetId, snapshotId, vmName, onclose }: Props = $props();

  const POLL_MS = 2000;

  let session = $state<BackupBrowseSession | null>(null);
  let volume = $state("");
  let path = $state("");
  let entries = $state<BackupBrowseEntry[]>([]);
  let busy = $state(false);
  let error = $state<string | null>(null);
  let notice = $state<string | null>(null);
  let timer: ReturnType<typeof setInterval> | undefined;
  let closed = false;

  const ready = $derived(session?.state === "ready");

  onMount(() => {
    void start();
  });

  onDestroy(() => {
    clearInterval(timer);
    if (session && !closed) void closeBackupBrowse(host.key, session.id).catch(() => {});
  });

  async function start() {
    busy = true;
    try {
      session = await withElevation(host.key, () => openBackupBrowse(host.key, targetId, snapshotId));
      timer = setInterval(() => void poll(), POLL_MS);
    } catch (e) {
      if (!(e instanceof ElevationCancelled)) error = errorMessage(e);
      else onclose();
    } finally {
      busy = false;
    }
  }

  async function poll() {
    if (!session || session.state !== "preparing") return;
    try {
      session = await getBackupBrowse(host.key, session.id);
      if (session.state !== "preparing") {
        clearInterval(timer);
        if (session.state === "ready") {
          volume = session.volumes[0]?.drive ?? "";
          await open("");
        } else {
          error = session.error ?? "The backup could not be opened.";
        }
      }
    } catch (e) {
      clearInterval(timer);
      error = errorMessage(e);
    }
  }

  async function open(next: string) {
    if (!session) return;
    busy = true;
    error = null;
    try {
      const id = session.id;
      entries = await withElevation(host.key, () => listBackupBrowse(host.key, id, volume, next));
      path = next;
    } catch (e) {
      if (!(e instanceof ElevationCancelled)) error = errorMessage(e);
    } finally {
      busy = false;
    }
  }

  async function save(entry: BackupBrowseEntry) {
    if (!session) return;
    busy = true;
    error = null;
    notice = null;
    try {
      const id = session.id;
      const where = await withElevation(host.key, () => downloadBackupBrowseFile(host.key, id, volume, joinPath(path, entry.name)));
      if (where) notice = `Saved ${entry.name} to ${where}.`;
    } catch (e) {
      if (!(e instanceof ElevationCancelled)) error = errorMessage(e);
    } finally {
      busy = false;
    }
  }

  async function changeVolume() {
    await open("");
  }

  async function finish() {
    clearInterval(timer);
    if (session) {
      closed = true;
      try {
        await closeBackupBrowse(host.key, session.id);
      } catch (e) {
        error = errorMessage(e);
        return;
      }
    }
    onclose();
  }
</script>

<Dialog title="Files in the backup of {vmName}" wide>
  {#if session === null && error === null}
    <p class="muted">Starting…</p>
  {:else if session?.state === "preparing"}
    <p role="status">{session.step}</p>
    <p class="muted">
      The backup's files are restored to a work folder on the host and its disks are opened read-only. This can take several
      minutes for a large disk. The work folder is removed when you close this window.
    </p>
  {:else if ready && session}
    {#if session.volumes.length > 1}
      <label for="bb-volume">Volume</label>
      <select id="bb-volume" bind:value={volume} onchange={changeVolume} disabled={busy}>
        {#each session.volumes as v (v.drive)}<option value={v.drive}>{volumeLabel(v)}</option>{/each}
      </select>
    {/if}
    <nav class="crumbs" aria-label="Folder">
      <button type="button" onclick={() => open("")} disabled={busy}>{volume}</button>
      {#each crumbs(path) as crumb (crumb.path)}
        <span aria-hidden="true">›</span>
        <button type="button" onclick={() => open(crumb.path)} disabled={busy}>{crumb.name}</button>
      {/each}
    </nav>
    {#if entries.length === 0}
      <p class="muted">This folder is empty.</p>
    {:else}
      <ul class="entries">
        {#each entries as entry (entry.name)}
          <li>
            {#if entry.isDirectory}
              <button type="button" class="link" onclick={() => open(joinPath(path, entry.name))} disabled={busy}>📁 {entry.name}</button>
            {:else}
              <span>{entry.name}</span>
              <span class="muted">{formatBytes(entry.size)}</span>
              <button type="button" onclick={() => save(entry)} disabled={busy}>Save…</button>
            {/if}
          </li>
        {/each}
      </ul>
    {/if}
    <p class="muted">Links inside the backup are not shown. The backup closes by itself after 30 minutes without use.</p>
  {/if}

  {#if notice}<p role="status">{notice}</p>{/if}
  {#if error}<p class="error" role="alert">{error}</p>{/if}
  <div class="actions"><button type="button" class="primary" onclick={finish}>Close backup</button></div>
</Dialog>

<style>
  .crumbs {
    display: flex;
    flex-wrap: wrap;
    align-items: center;
    gap: 0.25rem;
    margin: 0.5rem 0;
  }

  .entries {
    list-style: none;
    margin: 0;
    padding: 0;
    max-height: 22rem;
    overflow-y: auto;
  }

  .entries li {
    display: flex;
    align-items: center;
    gap: 0.75rem;
    padding: 0.2rem 0;
  }

  .entries li span:first-child {
    flex: 1;
    overflow-wrap: anywhere;
  }

  .link {
    background: none;
    border: none;
    padding: 0;
    text-align: left;
    cursor: pointer;
    color: inherit;
  }
</style>
