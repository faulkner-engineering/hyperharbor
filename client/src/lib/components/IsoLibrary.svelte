<script lang="ts" module>
  import type { IsoImage as CachedImage } from "$lib/api/client";

  /** The last library seen for each host, so returning to the screen shows it at once. */
  const libraryCache = new Map<string, { images: CachedImage[]; folder: string | null }>();
</script>

<script lang="ts">
  import { onMount, untrack } from "svelte";
  import { fade } from "svelte/transition";
  import {
    cancelIsoUpload,
    deleteIso,
    errorMessage,
    getHostResources,
    isClientError,
    listIsos,
    onIsoUploadProgress,
    pickIsoFile,
    renameIso,
    uploadIso,
    type HostEntry,
    type IsoImage,
    type PickedIso,
  } from "$lib/api/client";
  import { ElevationCancelled, ensureElevated, withElevation } from "$lib/lifecycle.svelte";
  import ConfirmDialog from "./ConfirmDialog.svelte";

  interface Props {
    host: HostEntry;
  }

  let { host }: Props = $props();

  // Shown at once from the last visit, then refreshed; the first visit shows nothing until both arrive.
  const cached = libraryCache.get(untrack(() => host.key));
  let images = $state<IsoImage[] | null>(cached?.images ?? null);
  let folder = $state<string | null>(cached?.folder ?? null);
  let error = $state<string | null>(null);
  let status = $state<string | null>(null);
  let upload = $state<{ picked: PickedIso; sent: number; cancelling: boolean } | null>(null);
  let renaming = $state<{ name: string; value: string } | null>(null);
  let confirmDelete = $state<IsoImage | null>(null);
  let busyName = $state<string | null>(null);

  const percent = $derived(
    upload && upload.picked.sizeBytes > 0 ? Math.floor((upload.sent / upload.picked.sizeBytes) * 100) : 0,
  );

  /** Loads the list and the folder together, so the screen changes once rather than piece by piece. */
  async function refresh() {
    const key = host.key;
    try {
      const [list, resources] = await Promise.all([
        listIsos(key),
        getHostResources(key).catch(() => null),
      ]);
      images = list;
      folder = resources?.isoFolder ?? folder;
      libraryCache.set(key, { images: list, folder });
      error = null;
    } catch (e) {
      error = errorMessage(e);
      images ??= [];
    }
  }

  onMount(() => {
    refresh();
    const unlisten = onIsoUploadProgress((progress) => {
      if (upload && progress.pickId === upload.picked.pickId) upload.sent = progress.sent;
    });
    return () => {
      unlisten.then((stop) => stop());
    };
  });

  function formatSize(bytes: number): string {
    if (bytes >= 1024 ** 3) return `${(bytes / 1024 ** 3).toFixed(1)} GB`;
    if (bytes >= 1024 ** 2) return `${Math.round(bytes / 1024 ** 2)} MB`;
    return `${Math.max(1, Math.round(bytes / 1024))} KB`;
  }

  function formatDate(value: string): string {
    return new Date(value).toLocaleDateString(undefined, { year: "numeric", month: "short", day: "numeric" });
  }

  async function add() {
    error = null;
    status = null;
    let picked: PickedIso | null;
    try {
      picked = await pickIsoFile();
    } catch (e) {
      error = errorMessage(e);
      return;
    }
    if (!picked) return;

    const name = picked.fileName;
    if (images?.some((image) => image.name.toLowerCase() === name.toLowerCase())) {
      error = `"${name}" is already in the library. Rename or delete that image first.`;
      return;
    }

    try {
      await ensureElevated(host.key);
      upload = { picked, sent: 0, cancelling: false };
      await uploadIso(host.key, picked.pickId, name);
      status = `Added ${name}.`;
    } catch (e) {
      if (e instanceof ElevationCancelled) return;
      error = isClientError(e) && e.code === "cancelled" ? "The upload was cancelled. Nothing was added." : errorMessage(e);
    } finally {
      upload = null;
      await refresh();
    }
  }

  async function cancelUpload() {
    if (!upload) return;
    upload.cancelling = true;
    await cancelIsoUpload(upload.picked.pickId);
  }

  async function saveRename(event: SubmitEvent) {
    event.preventDefault();
    if (!renaming) return;
    const { name, value } = renaming;
    const newName = value.trim();
    if (newName === name) {
      renaming = null;
      return;
    }
    busyName = name;
    error = null;
    try {
      await withElevation(host.key, () => renameIso(host.key, name, newName));
      renaming = null;
      status = `Renamed ${name} to ${newName}.`;
      await refresh();
    } catch (e) {
      if (!(e instanceof ElevationCancelled)) error = errorMessage(e);
    } finally {
      busyName = null;
    }
  }

  async function deleteConfirmed(confirmed: boolean) {
    const image = confirmDelete;
    confirmDelete = null;
    if (!confirmed || !image) return;
    busyName = image.name;
    error = null;
    try {
      await withElevation(host.key, () => deleteIso(host.key, image.name));
      status = `Deleted ${image.name}.`;
      await refresh();
    } catch (e) {
      if (!(e instanceof ElevationCancelled)) error = errorMessage(e);
    } finally {
      busyName = null;
    }
  }
</script>

<section>
  <div class="toolbar">
    <p class="muted">Images are stored on {host.displayName} and shared by every paired device.</p>
    <button type="button" class="primary" onclick={add} disabled={upload !== null}>+ Add ISO</button>
  </div>

  {#if upload}
    <div class="upload" role="status" aria-live="polite">
      <div class="upload-text">
        <span>Uploading {upload.picked.fileName}</span>
        <span class="muted">{formatSize(upload.sent)} of {formatSize(upload.picked.sizeBytes)} ({percent}%)</span>
      </div>
      <progress max="100" value={percent} aria-label="Upload progress">{percent}%</progress>
      <button type="button" onclick={cancelUpload} disabled={upload.cancelling}>
        {upload.cancelling ? "Cancelling…" : "Cancel"}
      </button>
    </div>
  {/if}

  {#if status}<p class="status" role="status">{status}</p>{/if}
  {#if error}<p class="error" role="alert">{error}</p>{/if}

  {#if images === null}
    <!-- Holds the table's space while loading, so nothing below jumps when it arrives. -->
    <div class="placeholder" role="status" aria-label="Loading the library">
      {#each [0, 1, 2] as row (row)}<div class="placeholder-row"></div>{/each}
    </div>
  {:else}
    <div in:fade={{ duration: 150 }}>
      {#if images.length === 0}
        <p class="empty">The library is empty. Use + Add ISO to upload an installation image from this device.</p>
      {:else}
        <table>
          <thead>
            <tr>
              <th scope="col">Name</th>
              <th scope="col" class="num">Size</th>
              <th scope="col">Added</th>
              <th scope="col">Used by</th>
              <th scope="col" class="actions"><span class="visually-hidden">Actions</span></th>
            </tr>
          </thead>
          <tbody>
            {#each images as image (image.name)}
              <tr>
                <td class="name">
                  {#if renaming?.name === image.name}
                    <form class="rename" onsubmit={saveRename}>
                      <input
                        aria-label="New name for {image.name}"
                        bind:value={renaming.value}
                        spellcheck="false"
                        disabled={busyName !== null}
                      />
                      <button type="submit" class="primary" disabled={busyName !== null || !renaming.value.trim().toLowerCase().endsWith(".iso")}>
                        Save
                      </button>
                      <button type="button" onclick={() => (renaming = null)} disabled={busyName !== null}>Cancel</button>
                    </form>
                  {:else}
                    {image.name}
                  {/if}
                </td>
                <td class="num">{formatSize(image.sizeBytes)}</td>
                <td>{formatDate(image.modifiedAt)}</td>
                <td>{image.usedBy.length === 0 ? "Not in use" : image.usedBy.join(", ")}</td>
                <td class="actions">
                  {#if renaming?.name !== image.name}
                    <button
                      type="button"
                      disabled={busyName !== null || image.usedBy.length > 0}
                      title={image.usedBy.length > 0 ? "Remove it from the VM's DVD drive first" : ""}
                      onclick={() => (renaming = { name: image.name, value: image.name })}>Rename</button
                    >
                    <button
                      type="button"
                      class="danger"
                      disabled={busyName !== null || image.usedBy.length > 0}
                      title={image.usedBy.length > 0 ? "Remove it from the VM's DVD drive first" : ""}
                      onclick={() => (confirmDelete = image)}>{busyName === image.name ? "Deleting…" : "Delete"}</button
                    >
                  {/if}
                </td>
              </tr>
            {/each}
          </tbody>
        </table>
      {/if}
      {#if folder}
        <p class="muted folder">Stored in {folder}. The folder can be changed in the HyperHarbor Host window on the host.</p>
      {/if}
    </div>
  {/if}
</section>

{#if confirmDelete}
  <ConfirmDialog
    title="Delete {confirmDelete.name}?"
    message="This deletes the image from {host.displayName} for every device. It cannot be undone."
    confirmLabel="Delete"
    danger
    onclose={deleteConfirmed}
  />
{/if}

<style>
  .toolbar {
    display: flex;
    align-items: flex-start;
    justify-content: space-between;
    gap: 1rem;
    margin-bottom: 0.75rem;
  }

  .muted {
    margin: 0;
    color: var(--muted);
  }

  .empty {
    color: var(--muted);
  }

  .folder {
    margin-top: 0.75rem;
    font-size: 0.85rem;
  }

  .placeholder {
    display: flex;
    flex-direction: column;
    gap: 0.6rem;
    padding-top: 0.6rem;
  }

  .placeholder-row {
    height: 1.6rem;
    border-radius: 6px;
    background: var(--hover);
    animation: pulse 1.2s ease-in-out infinite;
  }

  @keyframes pulse {
    50% {
      opacity: 0.5;
    }
  }

  @media (prefers-reduced-motion: reduce) {
    .placeholder-row {
      animation: none;
    }
  }

  .status {
    margin: 0.5rem 0;
  }

  .error {
    margin: 0.5rem 0;
    color: var(--danger);
  }

  .upload {
    display: grid;
    grid-template-columns: 1fr auto;
    gap: 0.35rem 1rem;
    align-items: center;
    margin-bottom: 0.75rem;
    padding: 0.75rem 1rem;
    border-radius: 6px;
    background: var(--notice-bg);
  }

  .upload-text {
    display: flex;
    justify-content: space-between;
    gap: 1rem;
    grid-column: 1;
  }

  .upload progress {
    grid-column: 1;
    width: 100%;
    height: 0.6rem;
    accent-color: var(--accent);
  }

  .upload button {
    grid-column: 2;
    grid-row: 1 / span 2;
  }

  table {
    width: 100%;
    border-collapse: collapse;
  }

  th {
    text-align: left;
    font-size: 0.75rem;
    font-weight: 600;
    text-transform: uppercase;
    letter-spacing: 0.05em;
    color: var(--muted);
    padding: 0.4rem 0.6rem;
    border-bottom: 1px solid var(--border);
  }

  td {
    padding: 0.55rem 0.6rem;
    border-bottom: 1px solid var(--border);
  }

  .name {
    font-weight: 600;
    word-break: break-all;
  }

  .num {
    text-align: right;
    font-variant-numeric: tabular-nums;
    white-space: nowrap;
  }

  .actions {
    text-align: right;
    white-space: nowrap;
  }

  .rename {
    display: flex;
    gap: 0.4rem;
  }

  .rename input {
    flex: 1;
    padding: 0.3rem 0.5rem;
    border: 1px solid var(--border);
    border-radius: 6px;
    background: var(--bg);
    color: inherit;
    font: inherit;
  }

  button {
    padding: 0.3rem 0.8rem;
    border: 1px solid var(--border);
    border-radius: 6px;
    background: var(--surface);
    color: inherit;
    cursor: pointer;
    font: inherit;
  }

  .actions button + button {
    margin-left: 0.35rem;
  }

  button.primary {
    background: var(--accent);
    border-color: var(--accent);
    color: var(--accent-fg);
    white-space: nowrap;
  }

  button.danger {
    color: var(--danger);
  }

  button:disabled {
    opacity: 0.5;
    cursor: default;
  }

  .visually-hidden {
    position: absolute;
    width: 1px;
    height: 1px;
    overflow: hidden;
    clip: rect(0 0 0 0);
  }
</style>
