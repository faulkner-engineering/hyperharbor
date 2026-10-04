<script lang="ts">
  import { onMount } from "svelte";
  import {
    deleteUnattendProfile,
    errorMessage,
    listUnattendProfiles,
    type HostEntry,
    type InstallOs,
    type UnattendProfile,
    type UnattendProfileRequest,
  } from "$lib/api/client";
  import { ElevationCancelled, withElevation } from "$lib/lifecycle.svelte";
  import { toasts } from "$lib/toasts.svelte";
  import ConfirmDialog from "./ConfirmDialog.svelte";
  import ProfileEditorDialog from "./ProfileEditorDialog.svelte";

  interface Props {
    host: HostEntry;
  }

  let { host }: Props = $props();

  let profiles = $state<UnattendProfile[] | null>(null);
  let error = $state<string | null>(null);
  let editing = $state<{ profileId: string | null; initial: UnattendProfileRequest } | null>(null);
  let confirmDelete = $state<UnattendProfile | null>(null);

  async function refresh() {
    try {
      profiles = await listUnattendProfiles(host.key);
      error = null;
    } catch (e) {
      error = errorMessage(e);
      profiles ??= [];
    }
  }

  onMount(refresh);

  function asRequest(profile: UnattendProfile): UnattendProfileRequest {
    return {
      name: profile.name,
      os: profile.os,
      adminAccountName: profile.adminAccountName,
      timeZone: profile.timeZone,
      locale: profile.locale,
      windows: profile.windows,
      linux: profile.linux,
    };
  }

  function newProfile(os: InstallOs) {
    editing = {
      profileId: null,
      initial:
        os === "windows"
          ? {
              name: "",
              os,
              adminAccountName: "hhadmin",
              timeZone: null,
              locale: "en-US",
              windows: {
                defaultEdition: "Windows 11 Pro",
                bypassHardwareChecks: false,
                disableTelemetry: true,
                disableAdvertisingId: true,
                disableLocation: true,
                disableConsumerFeatures: true,
              },
              linux: null,
            }
          : {
              name: "",
              os,
              adminAccountName: "hhadmin",
              timeZone: null,
              locale: "en-US",
              windows: null,
              linux: { sshAuthorizedKeys: [], packages: [], installDesktop: true },
            },
    };
  }

  function copy(profile: UnattendProfile) {
    editing = { profileId: null, initial: { ...asRequest(profile), name: `${profile.name} copy` } };
  }

  function edited(saved: UnattendProfile | null) {
    editing = null;
    if (saved) {
      toasts.show(`Saved ${saved.name}.`);
      refresh();
    }
  }

  async function deleteConfirmed(confirmed: boolean) {
    const profile = confirmDelete;
    confirmDelete = null;
    if (!confirmed || !profile) return;
    try {
      await withElevation(host.key, () => deleteUnattendProfile(host.key, profile.id));
      toasts.show(`Deleted ${profile.name}.`);
      await refresh();
    } catch (e) {
      if (!(e instanceof ElevationCancelled)) toasts.error(errorMessage(e));
    }
  }

  function summary(profile: UnattendProfile): string {
    if (profile.windows) {
      return `${profile.windows.defaultEdition}${profile.windows.bypassHardwareChecks ? ", skips hardware checks" : ""}`;
    }

    const keys = profile.linux?.sshAuthorizedKeys?.length ?? 0;
    const desktop = profile.linux?.installDesktop ? "desktop" : "no desktop";
    return `${desktop}, ${keys === 1 ? "1 SSH key" : `${keys} SSH keys`}`;
  }
</script>

<div class="toolbar">
  <p class="muted">
    Profiles install Windows or Ubuntu without anyone at the console when you create a VM. Built-in profiles cannot
    change; copy one to make your own. Saving or deleting asks for the admin passphrase.
  </p>
  <div class="buttons">
    <button type="button" onclick={() => newProfile("windows")}>New Windows profile</button>
    <button type="button" onclick={() => newProfile("linux")}>New Linux profile</button>
  </div>
</div>

{#if error}<p class="error" role="alert">{error}</p>{/if}

{#if profiles === null}
  <p class="muted">Loading profiles…</p>
{:else}
  <table>
    <thead>
      <tr>
        <th scope="col">Name</th>
        <th scope="col">OS</th>
        <th scope="col">Administrator</th>
        <th scope="col">Details</th>
        <th scope="col" class="actions"><span class="visually-hidden">Actions</span></th>
      </tr>
    </thead>
    <tbody>
      {#each profiles as profile (profile.id)}
        <tr>
          <td class="name">
            {profile.name}
            {#if profile.builtIn}<span class="tag">Built in</span>{/if}
          </td>
          <td>{profile.os === "windows" ? "Windows" : "Linux"}</td>
          <td>{profile.adminAccountName}</td>
          <td class="details">{summary(profile)}</td>
          <td class="actions">
            <button type="button" onclick={() => copy(profile)}>Copy</button>
            {#if !profile.builtIn}
              <button type="button" onclick={() => (editing = { profileId: profile.id, initial: asRequest(profile) })}>Edit</button>
              <button type="button" class="danger" onclick={() => (confirmDelete = profile)}>Delete</button>
            {/if}
          </td>
        </tr>
      {/each}
    </tbody>
  </table>
{/if}

{#if editing}
  <ProfileEditorDialog {host} profileId={editing.profileId} initial={editing.initial} onclose={edited} />
{/if}
{#if confirmDelete}
  <ConfirmDialog
    title="Delete {confirmDelete.name}?"
    message="VMs already installed with it are not affected."
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
    margin: 0.5rem 0;
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
  }

  .name {
    font-weight: 600;
  }

  .tag {
    margin-left: 0.4rem;
    padding: 0.05rem 0.4rem;
    border-radius: 999px;
    background: var(--idle-bg);
    color: var(--muted);
    font-size: 0.75rem;
    font-weight: 400;
  }

  .details {
    color: var(--muted);
    font-size: 0.85rem;
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
