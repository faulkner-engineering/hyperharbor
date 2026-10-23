<script lang="ts">
  import {
    deleteMachineProfile,
    errorMessage,
    exportMachineProfile,
    getMachineProfile,
    importMachineProfile,
    isClientError,
    listMachineProfiles,
    type HostEntry,
    type MachineProfile,
    type MachineProfileSummary,
    type StoredMachineProfile,
    type Vm,
  } from "$lib/api/client";
  import { ElevationCancelled, withElevation } from "$lib/lifecycle.svelte";
  import { machineSummary } from "$lib/machineDraft.svelte";
  import { toasts } from "$lib/toasts.svelte";
  import { onMount } from "svelte";
  import ConfirmDialog from "./ConfirmDialog.svelte";
  import MachineProfileEditor from "./MachineProfileEditor.svelte";

  interface Props {
    host: HostEntry;
    /** Running Windows VMs, for reading provisioned packages in the editor. */
    vms: Vm[];
  }

  let { host, vms }: Props = $props();

  let profiles = $state<MachineProfileSummary[] | null>(null);
  let error = $state<string | null>(null);
  let editing = $state<{ profileId: string | null; initial: MachineProfile | null } | null>(null);
  let confirmDelete = $state<MachineProfileSummary | null>(null);
  let working = $state(false);

  async function refresh() {
    try {
      profiles = await listMachineProfiles(host.key);
      error = null;
    } catch (e) {
      error = errorMessage(e);
      profiles ??= [];
    }
  }

  onMount(refresh);

  async function edit(summary: MachineProfileSummary) {
    try {
      const stored = await getMachineProfile(host.key, summary.id);
      editing = { profileId: stored.id, initial: stored.profile };
    } catch (e) {
      toasts.error(errorMessage(e));
    }
  }

  function edited(saved: StoredMachineProfile | null) {
    editing = null;
    if (saved) {
      toasts.show(`Saved ${saved.profile.name}.`);
      refresh();
    }
  }

  async function importFile() {
    working = true;
    try {
      const imported = await withElevation(host.key, () => importMachineProfile(host.key));
      if (imported) {
        toasts.show(`Imported ${imported.profile.name}.`);
        await refresh();
      }
    } catch (e) {
      if (e instanceof ElevationCancelled) return;
      const details = isClientError(e) && e.issues.length > 0 ? ` ${e.issues.map((issue) => issue.message).join(" ")}` : "";
      toasts.error(`${errorMessage(e)}${details}`);
    } finally {
      working = false;
    }
  }

  async function exportFile(summary: MachineProfileSummary) {
    try {
      const path = await exportMachineProfile(host.key, summary.id);
      if (path) toasts.show(`Saved ${summary.name} to ${path}.`);
    } catch (e) {
      toasts.error(errorMessage(e));
    }
  }

  async function deleteConfirmed(confirmed: boolean) {
    const profile = confirmDelete;
    confirmDelete = null;
    if (!confirmed || !profile) return;
    try {
      await withElevation(host.key, () => deleteMachineProfile(host.key, profile.id));
      await refresh();
    } catch (e) {
      if (!(e instanceof ElevationCancelled)) toasts.error(errorMessage(e));
    }
  }
</script>

<div class="toolbar">
  <p class="muted">
    A machine profile describes a whole VM: its account, display, firmware, processors and memory, GPU share, network,
    backups, and software. They are YAML files on the host; export one to edit it in a text editor or take it to another
    host. Saving, importing, or deleting asks for the admin passphrase.
  </p>
  <div class="buttons">
    <button type="button" onclick={() => (editing = { profileId: null, initial: null })}>New profile</button>
    <button type="button" onclick={importFile} disabled={working}>Import…</button>
  </div>
</div>

{#if error}<p class="error" role="alert">{error}</p>{/if}

{#if profiles === null}
  <p class="muted">Loading profiles…</p>
{:else if profiles.length === 0}
  <p class="muted">No machine profiles yet. Make one here, or convert a setup profile from the Setup profiles tab.</p>
{:else}
  <table>
    <thead>
      <tr>
        <th scope="col">Name</th>
        <th scope="col">Machine</th>
        <th scope="col">Changed</th>
        <th scope="col" class="actions"><span class="visually-hidden">Actions</span></th>
      </tr>
    </thead>
    <tbody>
      {#each profiles as profile (profile.id)}
        <tr>
          <td class="name">
            {profile.name}
            {#if profile.description}<div class="details">{profile.description}</div>{/if}
          </td>
          <td class="details">
            {#if profile.error}<span class="error">The file on the host does not read: {profile.error}</span>{:else}{machineSummary(profile)}{/if}
          </td>
          <td class="details">{new Date(profile.updatedAt).toLocaleString()}</td>
          <td class="actions">
            {#if !profile.error}<button type="button" onclick={() => edit(profile)}>Edit</button>{/if}
            <button type="button" onclick={() => exportFile(profile)}>Export…</button>
            <button type="button" class="danger" onclick={() => (confirmDelete = profile)}>Delete</button>
          </td>
        </tr>
      {/each}
    </tbody>
  </table>
{/if}

{#if editing}
  <MachineProfileEditor {host} {vms} profileId={editing.profileId} initial={editing.initial} onclose={edited} />
{/if}
{#if confirmDelete}
  <ConfirmDialog
    title="Delete {confirmDelete.name}?"
    message="The profile's file is removed from the host. VMs made from it are not affected."
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

  .buttons {
    display: flex;
    gap: 0.35rem;
    flex-shrink: 0;
  }

  .muted {
    margin: 0;
    color: var(--muted);
  }

  .error {
    color: var(--danger);
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
  }

  .details {
    color: var(--muted);
    font-size: 0.85rem;
    font-weight: 400;
  }

  .actions {
    text-align: right;
    white-space: nowrap;
  }

  button {
    padding: 0.3rem 0.8rem;
    border: 1px solid var(--border);
    border-radius: 6px;
    background: var(--surface);
    color: inherit;
    cursor: pointer;
  }

  .actions button + button {
    margin-left: 0.35rem;
  }

  button.danger {
    color: var(--danger);
  }

  .visually-hidden {
    position: absolute;
    width: 1px;
    height: 1px;
    overflow: hidden;
    clip: rect(0 0 0 0);
  }
</style>
