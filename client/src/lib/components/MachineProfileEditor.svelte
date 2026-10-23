<script lang="ts">
  import {
    errorMessage,
    getSetupProfile,
    hostSupports,
    isClientError,
    ProfilesConsolidatedApiVersion,
    listSetupProfiles,
    saveMachineProfile,
    type HostEntry,
    type MachineProfile,
    type SetupProfileSummary,
    type StoredMachineProfile,
    type ValidationIssue,
    type Vm,
  } from "$lib/api/client";
  import type { Detail } from "$lib/catalogBrowser";
  import { ElevationCancelled, withElevation } from "$lib/lifecycle.svelte";
  import { MachineDraft } from "$lib/machineDraft.svelte";
  import { SOFTWARE_TABS } from "$lib/softwareDraft.svelte";
  import { onMount, untrack } from "svelte";
  import AccountSection from "./AccountSection.svelte";
  import BackupSection from "./BackupSection.svelte";
  import Choice from "./Choice.svelte";
  import ComputeSection from "./ComputeSection.svelte";
  import DisplaySection from "./DisplaySection.svelte";
  import FirmwareSection from "./FirmwareSection.svelte";
  import GpuSection from "./GpuSection.svelte";
  import NetworkSection from "./NetworkSection.svelte";
  import ProfileWorkspace from "./ProfileWorkspace.svelte";
  import SoftwarePanel from "./SoftwarePanel.svelte";

  interface Props {
    host: HostEntry;
    /** The profile being edited, or null for a new one. */
    profileId: string | null;
    /** Null for a new, empty profile. */
    initial: MachineProfile | null;
    /** Running Windows VMs, for reading provisioned packages. */
    vms: Vm[];
    onclose: (saved: StoredMachineProfile | null) => void;
    /**
     * Saves somewhere other than the host (the library on this device). The host then only supplies the catalogs,
     * and onclose gets null after a save.
     */
    onsave?: (profile: MachineProfile) => Promise<void>;
  }

  let { host, profileId, initial, vms, onclose, onsave }: Props = $props();

  const start = untrack(() => initial);
  const draft = new MachineDraft(start ?? undefined);

  let tab = $state("account");
  let detail = $state<Detail | null>(null);
  let busy = $state(false);
  let error = $state<string | null>(null);
  let issues = $state<ValidationIssue[]>([]);
  let setupProfiles = $state<SetupProfileSummary[]>([]);

  onMount(async () => {
    error = await draft.software.load(host.key);
    // A host at 1.32.0 converted its setup profiles into machine profiles, so there is nothing to fill from.
    if (hostSupports(host, ProfilesConsolidatedApiVersion)) return;
    try {
      setupProfiles = (await listSetupProfiles(host.key)).filter((profile) => !profile.error);
    } catch {
      // Filling from a setup profile is a convenience; the editor works without the list.
    }
  });

  async function fillSoftware(id: string) {
    try {
      const stored = await getSetupProfile(host.key, id);
      draft.software.fill(stored.profile);
    } catch (e) {
      error = errorMessage(e);
    }
  }

  async function save() {
    busy = true;
    error = null;
    issues = [];
    try {
      if (onsave) {
        await onsave(draft.toProfile());
        onclose(null);
      } else {
        onclose(await withElevation(host.key, () => saveMachineProfile(host.key, profileId, draft.toProfile())));
      }
    } catch (e) {
      if (e instanceof ElevationCancelled) return;
      error = errorMessage(e);
      issues = isClientError(e) ? e.issues : [];
    } finally {
      busy = false;
    }
  }
</script>

<ProfileWorkspace
  title={start?.name && (profileId || onsave) ? `Edit ${start.name}` : "New machine profile"}
  sections={draft.sections()}
  bind:tab
  bind:detail
  controls={draft.controls}
  hud={draft.hud(tab)}
  {error}
  {issues}
  {busy}
  canSave={draft.name.trim() !== ""}
  onsave={save}
  oncancel={() => onclose(null)}
>
  {#snippet header()}
    <label>Name <input bind:value={draft.name} maxlength="60" required /></label>
    <label>Description <input bind:value={draft.description} maxlength="500" placeholder="Optional" /></label>
    <Choice
      label="Operating system"
      options={[
        { value: "windows", label: "Windows" },
        { value: "linux", label: "Linux" },
      ]}
      bind:value={draft.os}
    />
  {/snippet}

  {#if tab === "account"}
    <AccountSection {host} {draft} />
  {:else if tab === "display"}
    <DisplaySection {host} {draft} />
  {:else if tab === "firmware"}
    <FirmwareSection {draft} />
  {:else if tab === "compute"}
    <ComputeSection {draft} />
  {:else if tab === "gpu"}
    <GpuSection {draft} />
  {:else if tab === "network"}
    <NetworkSection {host} {draft} />
  {:else if tab === "backup"}
    <BackupSection {host} {draft} />
  {:else if SOFTWARE_TABS.includes(tab)}
    {#if draft.os !== "windows"}
      <p class="muted">Packages, removals, tweaks, and browsers are for Windows VMs. For Linux, list apt packages in Account.</p>
    {:else}
      {#if setupProfiles.length > 0}
        <label class="fill">
          Fill the software sections from a setup profile
          <select value="" onchange={(event) => fillSoftware(event.currentTarget.value)}>
            <option value="" disabled>Choose…</option>
            {#each setupProfiles as profile (profile.id)}<option value={profile.id}>{profile.name}</option>{/each}
          </select>
        </label>
      {/if}
      <SoftwarePanel {host} {vms} draft={draft.software} {tab} selected={detail?.key ?? null} onselect={(next) => (detail = next)} />
    {/if}
  {/if}
</ProfileWorkspace>

<style>
  .fill {
    display: flex;
    flex-direction: column;
    gap: 0.2rem;
    margin-bottom: 0.6rem;
  }
</style>
