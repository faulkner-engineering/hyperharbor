<script lang="ts">
  import {
    deleteSetupProfile,
    errorMessage,
    exportSetupProfile,
    getSetupProfile,
    importSetupProfile,
    isClientError,
    listSetupProfiles,
    type HostEntry,
    type SetupProfile,
    type SetupProfileSummary,
    type StoredSetupProfile,
    type Vm,
  } from "$lib/api/client";
  import { ElevationCancelled, withElevation } from "$lib/lifecycle.svelte";
  import { emptyProfile } from "$lib/setupProfiles";
  import { toasts } from "$lib/toasts.svelte";
  import { onMount } from "svelte";
  import ConfirmDialog from "./ConfirmDialog.svelte";
  import InfoTip from "./InfoTip.svelte";
  import SetupProfileEditor from "./SetupProfileEditor.svelte";

  interface Props {
    host: HostEntry;
    /** Running Windows VMs, for reading provisioned packages in the editor. */
    vms: Vm[];
  }

  let { host, vms }: Props = $props();

  let profiles = $state<SetupProfileSummary[] | null>(null);
  let error = $state<string | null>(null);
  let editing = $state<{ profileId: string | null; initial: SetupProfile } | null>(null);
  let confirmDelete = $state<SetupProfileSummary | null>(null);
  let working = $state(false);

  async function refresh() {
    try {
      profiles = await listSetupProfiles(host.key);
      error = null;
    } catch (e) {
      error = errorMessage(e);
      profiles ??= [];
    }
  }

  onMount(refresh);

  async function edit(summary: SetupProfileSummary) {
    try {
      const stored = await getSetupProfile(host.key, summary.id);
      editing = { profileId: stored.id, initial: stored.profile };
    } catch (e) {
      toasts.error(errorMessage(e));
    }
  }

  function edited(saved: StoredSetupProfile | null) {
    editing = null;
    if (saved) {
      toasts.show(`Saved ${saved.profile.name}.`);
      refresh();
    }
  }

  async function importFile() {
    working = true;
    try {
      const imported = await withElevation(host.key, () => importSetupProfile(host.key));
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

  async function exportFile(summary: SetupProfileSummary) {
    try {
      const path = await exportSetupProfile(host.key, summary.id);
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
      await withElevation(host.key, () => deleteSetupProfile(host.key, profile.id));
      await refresh();
    } catch (e) {
      if (!(e instanceof ElevationCancelled)) toasts.error(errorMessage(e));
    }
  }

  function summary(profile: SetupProfileSummary): string {
    const parts = [
      `${profile.installCount} to install`,
      `${profile.removeCount} to remove`,
      `${profile.tweakCount} tweaks`,
    ];
    if (profile.browser) parts.push(`${profile.browser} with ${profile.extensionCount} extensions`);
    return parts.join(", ");
  }
</script>

<div class="toolbar">
  <p class="muted">
    Setup profiles list what to install, remove, and change in a Windows VM. Build one here, or capture one from a VM
    you have set up (Capture setup profile in the VM's menu). They are YAML files on the host; export one to edit it
    in a text editor. Saving, importing, or deleting asks for the admin passphrase.
    <InfoTip label="About installing software that needs administrator approval">
      To install software that needs administrator approval, add it to a profile and use Apply setup profile… in the
      VM's menu. The host installs it as the VM's administrator, so there is no administrator prompt to answer inside
      the VM. Your HyperHarbor account in the VM stays a standard user.
    </InfoTip>
  </p>
  <div class="buttons">
    <button type="button" onclick={() => (editing = { profileId: null, initial: emptyProfile() })}>New profile</button>
    <button type="button" onclick={importFile} disabled={working}>Import…</button>
  </div>
</div>

{#if error}<p class="error" role="alert">{error}</p>{/if}

{#if profiles === null}
  <p class="muted">Loading profiles…</p>
{:else if profiles.length === 0}
  <p class="muted">No setup profiles yet.</p>
{:else}
  <table>
    <thead>
      <tr>
        <th scope="col">Name</th>
        <th scope="col">Contents</th>
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
            {#if profile.error}<span class="error">The file on the host does not read: {profile.error}</span>{:else}{summary(profile)}{/if}
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
  <SetupProfileEditor {host} {vms} profileId={editing.profileId} initial={editing.initial} onclose={edited} />
{/if}
{#if confirmDelete}
  <ConfirmDialog
    title="Delete {confirmDelete.name}?"
    message="The profile's file is removed from the host. VMs it was applied to are not affected."
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
