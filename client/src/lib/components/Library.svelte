<script lang="ts">
  import { onMount } from "svelte";
  import { SvelteMap } from "svelte/reactivity";
  import {
    addLibraryIso,
    cancelLibraryTask,
    errorMessage,
    getLibraryProfile,
    hostSupports,
    isClientError,
    libraryStatus,
    listLibrary,
    MachineProfilesApiVersion,
    newTaskId,
    onLibraryProgress,
    pickIsoFile,
    ProfileImagesApiVersion,
    removeLibraryItem,
    renameLibraryIso,
    saveHostProfileToLibrary,
    saveLibraryProfile,
    sendLibraryIso,
    sendLibraryProfile,
    unlinkLibraryItem,
    type HostEntry,
    type Library,
    type LibraryIso,
    type LibraryProfile,
    type LinkState,
    type MachineProfile,
  } from "$lib/api/client";
  import { ElevationCancelled, ensureElevated, withElevation } from "$lib/lifecycle.svelte";
  import { toasts } from "$lib/toasts.svelte";
  import ConfirmDialog from "./ConfirmDialog.svelte";
  import MachineProfileEditor from "./MachineProfileEditor.svelte";

  interface Props {
    /** Every known host; items are sent to the paired ones. */
    hosts: HostEntry[];
  }

  let { hosts }: Props = $props();

  let library = $state<Library | null>(null);
  let error = $state<string | null>(null);
  /** Link state by item ID and host ID, from each linked host that answered. */
  const states = new SvelteMap<string, LinkState>();
  /** Linked hosts that could not be asked. */
  let unreachable = $state<string[]>([]);
  /** A running copy, download, or upload, with its progress. */
  let task = $state<{ id: string; label: string; done: number; total: number; cancelling: boolean } | null>(null);
  let busy = $state<string | null>(null);
  let renaming = $state<{ id: string; value: string } | null>(null);
  let confirmRemove = $state<{ id: string; name: string } | null>(null);

  const pairedHosts = $derived(hosts.filter((host) => host.paired && host.hostId));
  /** Hosts whose catalogs (packages, settings, switches, images) the editor can use. */
  const catalogHosts = $derived(pairedHosts.filter((host) => hostSupports(host, MachineProfilesApiVersion)));
  let catalogKey = $state<string | null>(null);
  const catalogHost = $derived(catalogHosts.find((host) => host.key === catalogKey) ?? catalogHosts[0] ?? null);
  /** The profile open in the editor: a library item, or a new one (null ID). */
  let editing = $state<{ itemId: string | null; initial: MachineProfile | null } | null>(null);
  const percent = $derived(task && task.total > 0 ? Math.floor((task.done / task.total) * 100) : 0);

  const stateKey = (itemId: string, hostId: string) => `${itemId}/${hostId}`;
  const hostFor = (hostId: string) => pairedHosts.find((host) => host.hostId === hostId) ?? null;

  async function refresh() {
    try {
      library = await listLibrary();
      error = null;
    } catch (e) {
      error = errorMessage(e);
      library ??= { version: 1, isos: [], profiles: [] };
      return;
    }
    const linked = new Set([...library.isos, ...library.profiles].flatMap((item) => item.links.map((link) => link.hostId)));
    const missing: string[] = [];
    await Promise.all(
      [...linked].map(async (hostId) => {
        const host = hostFor(hostId);
        if (!host) {
          missing.push(hostId);
          return;
        }
        try {
          for (const status of await libraryStatus(host.key)) states.set(stateKey(status.itemId, status.hostId), status.state);
        } catch {
          missing.push(hostId);
        }
      }),
    );
    unreachable = missing;
  }

  onMount(() => {
    refresh();
    const unlisten = onLibraryProgress((progress) => {
      if (task && progress.taskId === task.id) {
        task.done = progress.done;
        task.total = progress.total;
      }
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

  function stateLabel(itemId: string, hostId: string): string {
    if (unreachable.includes(hostId)) return "Not reachable";
    switch (states.get(stateKey(itemId, hostId))) {
      case "inSync":
        return "In sync";
      case "libraryNewer":
        return "Library newer";
      case "hostNewer":
        return "Changed on host";
      case "bothChanged":
        return "Both changed";
      case "missingOnHost":
        return "Missing on host";
      default:
        return "Checking…";
    }
  }

  function failed(e: unknown, stopped: string) {
    if (e instanceof ElevationCancelled) return;
    toasts.error(isClientError(e) && e.code === "cancelled" ? stopped : errorMessage(e));
  }

  async function addIso() {
    let picked;
    try {
      picked = await pickIsoFile();
    } catch (e) {
      toasts.error(errorMessage(e));
      return;
    }
    if (!picked) return;
    task = { id: picked.pickId, label: `Adding ${picked.fileName}`, done: 0, total: picked.sizeBytes, cancelling: false };
    try {
      await addLibraryIso(picked.pickId, picked.fileName);
      toasts.show(`Added ${picked.fileName} to the library.`);
    } catch (e) {
      failed(e, "Adding the image was cancelled. Nothing was added.");
    } finally {
      task = null;
      await refresh();
    }
  }

  async function sendIso(iso: LibraryIso, host: HostEntry) {
    const linked = iso.links.find((link) => link.hostId === host.hostId);
    const name = linked?.remoteName ?? iso.name;
    try {
      await ensureElevated(host.key);
      task = { id: newTaskId(), label: `Sending ${iso.name} to ${host.displayName}`, done: 0, total: iso.sizeBytes, cancelling: false };
      await sendLibraryIso(host.key, iso.id, name, task.id);
      toasts.show(`Sent ${iso.name} to ${host.displayName}.`);
    } catch (e) {
      failed(e, "Sending was cancelled. The host kept nothing.");
    } finally {
      task = null;
      await refresh();
    }
  }

  async function sendProfile(profile: LibraryProfile, host: HostEntry, asNew = false) {
    if (profile.images.length > 0 && !hostSupports(host, ProfileImagesApiVersion)) {
      toasts.error(`${host.displayName} cannot keep wallpaper images yet. Update the host first.`);
      return;
    }
    busy = profile.id;
    try {
      await withElevation(host.key, () => sendLibraryProfile(host.key, profile.id, asNew));
      toasts.show(`Sent ${profile.name} to ${host.displayName}.`);
    } catch (e) {
      failed(e, "Sending was cancelled.");
    } finally {
      busy = null;
      await refresh();
    }
  }

  async function updateFromHost(profile: LibraryProfile, host: HostEntry) {
    const link = profile.links.find((item) => item.hostId === host.hostId);
    if (!link) return;
    busy = profile.id;
    try {
      await saveHostProfileToLibrary(host.key, link.remoteId);
      toasts.show(`Updated ${profile.name} from ${host.displayName}.`);
    } catch (e) {
      toasts.error(errorMessage(e));
    } finally {
      busy = null;
      await refresh();
    }
  }

  async function forget(itemId: string, hostId: string) {
    try {
      await unlinkLibraryItem(itemId, hostId);
      states.delete(stateKey(itemId, hostId));
      await refresh();
    } catch (e) {
      toasts.error(errorMessage(e));
    }
  }

  async function saveRename(event: SubmitEvent) {
    event.preventDefault();
    if (!renaming) return;
    const { id, value } = renaming;
    busy = id;
    try {
      await renameLibraryIso(id, value.trim());
      renaming = null;
      await refresh();
    } catch (e) {
      toasts.error(errorMessage(e));
    } finally {
      busy = null;
    }
  }

  async function removeConfirmed(confirmed: boolean) {
    const item = confirmRemove;
    confirmRemove = null;
    if (!confirmed || !item) return;
    try {
      await removeLibraryItem(item.id);
      toasts.show(`Removed ${item.name} from this device.`);
      await refresh();
    } catch (e) {
      toasts.error(errorMessage(e));
    }
  }

  async function edit(profile: LibraryProfile) {
    try {
      editing = { itemId: profile.id, initial: await getLibraryProfile(profile.id) };
    } catch (e) {
      toasts.error(errorMessage(e));
    }
  }

  async function saveEdited(profile: MachineProfile) {
    const saved = await saveLibraryProfile(catalogHost?.key ?? null, editing?.itemId ?? null, profile);
    toasts.show(`Saved ${saved.name} to the library.`);
    await refresh();
  }

  async function cancelTask() {
    if (!task) return;
    task.cancelling = true;
    await cancelLibraryTask(task.id);
  }

  /** Hosts an item can go to: paired hosts it is not on yet, or every paired host when sending again. */
  function targets(links: { hostId: string }[], profiles: boolean): HostEntry[] {
    return pairedHosts.filter(
      (host) => !links.some((link) => link.hostId === host.hostId) && (!profiles || hostSupports(host, MachineProfilesApiVersion)),
    );
  }

  function chooseTarget(event: Event & { currentTarget: HTMLSelectElement }, send: (host: HostEntry) => void) {
    const host = pairedHosts.find((item) => item.key === event.currentTarget.value);
    event.currentTarget.value = "";
    if (host) send(host);
  }
</script>

<section>
  <header>
    <h2>Library</h2>
    <p class="muted">
      Installation images and machine profiles kept on this device. Send them to any paired host, or save them here from
      a host's ISO library or Profiles tab. Each copy shows whether it still matches the library.
    </p>
  </header>

  {#if task}
    <div class="progress" role="status" aria-live="polite">
      <div class="progress-text">
        <span>{task.label}</span>
        <span class="muted">{formatSize(task.done)} of {formatSize(task.total)} ({percent}%)</span>
      </div>
      <progress max="100" value={percent} aria-label="Progress">{percent}%</progress>
      <button type="button" onclick={cancelTask} disabled={task.cancelling}>{task.cancelling ? "Cancelling…" : "Cancel"}</button>
    </div>
  {/if}

  {#if error}<p class="error" role="alert">{error}</p>{/if}

  {#if library === null}
    <p class="muted">Loading the library…</p>
  {:else}
    <div class="section-head">
      <h3>Installation images</h3>
      <button type="button" class="primary" onclick={addIso} disabled={task !== null}>+ Add ISO</button>
    </div>
    {#if library.isos.length === 0}
      <p class="muted">No images yet. Add one from this device, or use Save to library on a host's ISO library.</p>
    {:else}
      <table>
        <thead>
          <tr>
            <th scope="col">Name</th>
            <th scope="col" class="num">Size</th>
            <th scope="col">On hosts</th>
            <th scope="col" class="actions"><span class="visually-hidden">Actions</span></th>
          </tr>
        </thead>
        <tbody>
          {#each library.isos as iso (iso.id)}
            <tr>
              <td class="name">
                {#if renaming?.id === iso.id}
                  <form class="rename" onsubmit={saveRename}>
                    <input aria-label="New name for {iso.name}" bind:value={renaming.value} spellcheck="false" disabled={busy !== null} />
                    <button type="submit" class="primary" disabled={busy !== null || !renaming.value.trim().toLowerCase().endsWith(".iso")}
                      >Save</button
                    >
                    <button type="button" onclick={() => (renaming = null)}>Cancel</button>
                  </form>
                {:else}
                  {iso.name}
                {/if}
              </td>
              <td class="num">{formatSize(iso.sizeBytes)}</td>
              <td>
                <ul class="links">
                  {#each iso.links as link (link.hostId)}
                    {@const host = hostFor(link.hostId)}
                    {@const state = states.get(stateKey(iso.id, link.hostId))}
                    <li class="chip" data-state={unreachable.includes(link.hostId) ? "unreachable" : state}>
                      <span>{link.hostName}: {stateLabel(iso.id, link.hostId)}</span>
                      {#if host && state === "missingOnHost"}
                        <button type="button" class="link" disabled={task !== null} onclick={() => sendIso(iso, host)}>Send again</button>
                      {/if}
                      <button type="button" class="link" aria-label="Forget the copy on {link.hostName}" title="Forget this copy (the host keeps it)"
                        onclick={() => forget(iso.id, link.hostId)}>×</button
                      >
                    </li>
                  {:else}
                    <li class="muted">Only on this device</li>
                  {/each}
                </ul>
              </td>
              <td class="actions">
                {#if targets(iso.links, false).length > 0}
                  <select aria-label="Send {iso.name} to a host" value="" disabled={task !== null}
                    onchange={(event) => chooseTarget(event, (host) => sendIso(iso, host))}>
                    <option value="" disabled>Send to…</option>
                    {#each targets(iso.links, false) as host (host.key)}<option value={host.key}>{host.displayName}</option>{/each}
                  </select>
                {/if}
                <button type="button" disabled={task !== null} onclick={() => (renaming = { id: iso.id, value: iso.name })}>Rename</button>
                <button type="button" class="danger" disabled={task !== null} onclick={() => (confirmRemove = { id: iso.id, name: iso.name })}
                  >Remove</button
                >
              </td>
            </tr>
          {/each}
        </tbody>
      </table>
    {/if}

    <div class="section-head">
      <h3>Machine profiles</h3>
      {#if catalogHost}
        <div class="buttons">
          {#if catalogHosts.length > 1}
            <label class="catalogs">
              Catalogs from
              <select value={catalogHost.key} onchange={(event) => (catalogKey = event.currentTarget.value)}>
                {#each catalogHosts as host (host.key)}<option value={host.key}>{host.displayName}</option>{/each}
              </select>
            </label>
          {/if}
          <button type="button" class="primary" onclick={() => (editing = { itemId: null, initial: null })}>+ New profile</button>
        </div>
      {/if}
    </div>
    {#if !catalogHost}
      <p class="muted">Pair a host to make profiles here: the editor uses its package, setting, and network lists.</p>
    {/if}
    {#if library.profiles.length === 0}
      <p class="muted">No profiles yet. Use + New profile, or Save to library on a host's Profiles tab.</p>
    {:else}
      <table>
        <thead>
          <tr>
            <th scope="col">Name</th>
            <th scope="col">System</th>
            <th scope="col">On hosts</th>
            <th scope="col" class="actions"><span class="visually-hidden">Actions</span></th>
          </tr>
        </thead>
        <tbody>
          {#each library.profiles as profile (profile.id)}
            <tr>
              <td class="name">
                {profile.name}
                {#if profile.description}<div class="details">{profile.description}</div>{/if}
              </td>
              <td>{profile.os === "linux" ? "Linux" : "Windows"}</td>
              <td>
                <ul class="links">
                  {#each profile.links as link (link.hostId)}
                    {@const host = hostFor(link.hostId)}
                    {@const state = states.get(stateKey(profile.id, link.hostId))}
                    <li class="chip" data-state={unreachable.includes(link.hostId) ? "unreachable" : state}>
                      <span>{link.hostName}: {stateLabel(profile.id, link.hostId)}</span>
                      {#if host && !unreachable.includes(link.hostId)}
                        {#if state === "libraryNewer" || state === "missingOnHost"}
                          <button type="button" class="link" disabled={busy !== null} onclick={() => sendProfile(profile, host)}>Send</button>
                        {:else if state === "hostNewer"}
                          <button type="button" class="link" disabled={busy !== null} onclick={() => updateFromHost(profile, host)}
                            >Update library</button
                          >
                        {:else if state === "bothChanged"}
                          <button type="button" class="link" disabled={busy !== null} onclick={() => sendProfile(profile, host)}
                            >Keep library's</button
                          >
                          <button type="button" class="link" disabled={busy !== null} onclick={() => updateFromHost(profile, host)}
                            >Keep host's</button
                          >
                        {/if}
                      {/if}
                      <button type="button" class="link" aria-label="Forget the copy on {link.hostName}" title="Forget this copy (the host keeps it)"
                        onclick={() => forget(profile.id, link.hostId)}>×</button
                      >
                    </li>
                  {:else}
                    <li class="muted">Only on this device</li>
                  {/each}
                </ul>
              </td>
              <td class="actions">
                {#if catalogHost}
                  <button type="button" disabled={busy !== null} onclick={() => edit(profile)}>Edit</button>
                {/if}
                {#if targets(profile.links, true).length > 0}
                  <select aria-label="Send {profile.name} to a host" value="" disabled={busy !== null}
                    onchange={(event) => chooseTarget(event, (host) => sendProfile(profile, host))}>
                    <option value="" disabled>Send to…</option>
                    {#each targets(profile.links, true) as host (host.key)}<option value={host.key}>{host.displayName}</option>{/each}
                  </select>
                {/if}
                <button type="button" class="danger" disabled={busy !== null}
                  onclick={() => (confirmRemove = { id: profile.id, name: profile.name })}>Remove</button
                >
              </td>
            </tr>
          {/each}
        </tbody>
      </table>
    {/if}
  {/if}
</section>

{#if editing && catalogHost}
  <MachineProfileEditor
    host={catalogHost}
    vms={[]}
    profileId={null}
    initial={editing.initial}
    onsave={saveEdited}
    onclose={() => (editing = null)}
  />
{/if}

{#if confirmRemove}
  <ConfirmDialog
    title="Remove {confirmRemove.name} from this device?"
    message="It is deleted from the library on this device. Copies already on hosts stay."
    confirmLabel="Remove"
    danger
    onclose={removeConfirmed}
  />
{/if}

<style>
  header {
    margin-bottom: 1rem;
  }

  h2 {
    margin: 0 0 0.35rem;
  }

  h3 {
    margin: 0;
    font-size: 1rem;
  }

  .section-head {
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 1rem;
    margin: 1.25rem 0 0.5rem;
  }

  .muted {
    margin: 0;
    color: var(--muted);
  }

  .buttons {
    display: flex;
    align-items: center;
    gap: 0.5rem;
  }

  .catalogs {
    display: flex;
    align-items: center;
    gap: 0.4rem;
    font-size: 0.85rem;
    color: var(--muted);
  }

  .actions > * + * {
    margin-left: 0.35rem;
  }

  .error {
    color: var(--danger);
  }

  .details {
    font-size: 0.85rem;
    font-weight: 400;
    color: var(--muted);
  }

  .progress {
    display: grid;
    grid-template-columns: 1fr auto;
    gap: 0.35rem 1rem;
    align-items: center;
    margin-bottom: 0.75rem;
    padding: 0.75rem 1rem;
    border-radius: 6px;
    background: var(--notice-bg);
  }

  .progress-text {
    display: flex;
    justify-content: space-between;
    gap: 1rem;
    grid-column: 1;
  }

  .progress progress {
    grid-column: 1;
    width: 100%;
    height: 0.6rem;
    accent-color: var(--accent);
  }

  .progress button {
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
    vertical-align: top;
  }

  .name {
    font-weight: 600;
    word-break: break-word;
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

  .links {
    list-style: none;
    margin: 0;
    padding: 0;
    display: flex;
    flex-wrap: wrap;
    gap: 0.3rem;
  }

  .chip {
    display: inline-flex;
    align-items: center;
    gap: 0.3rem;
    padding: 0.1rem 0.25rem 0.1rem 0.5rem;
    border-radius: 999px;
    font-size: 0.8rem;
    background: var(--idle-bg);
  }

  .chip[data-state="inSync"] {
    background: var(--ok-bg);
    color: var(--ok-fg);
  }

  .chip[data-state="libraryNewer"],
  .chip[data-state="hostNewer"],
  .chip[data-state="bothChanged"],
  .chip[data-state="missingOnHost"] {
    background: var(--notice-bg);
  }

  .link {
    padding: 0 0.3rem;
    border: 0;
    background: transparent;
    color: var(--accent);
    font-size: 0.8rem;
    cursor: pointer;
  }

  .link:disabled {
    color: var(--muted);
    cursor: default;
  }

  .rename {
    display: flex;
    gap: 0.35rem;
  }

  .rename input {
    flex: 1;
    min-width: 0;
  }
</style>
