<script lang="ts">
  import {
    errorMessage,
    isClientError,
    saveSetupProfile,
    type HostEntry,
    type SetupProfile,
    type StoredSetupProfile,
    type ValidationIssue,
    type Vm,
  } from "$lib/api/client";
  import type { Detail } from "$lib/catalogBrowser";
  import { ElevationCancelled, withElevation } from "$lib/lifecycle.svelte";
  import { SoftwareDraft } from "$lib/softwareDraft.svelte";
  import { onMount, untrack } from "svelte";
  import ProfileWorkspace from "./ProfileWorkspace.svelte";
  import SoftwarePanel from "./SoftwarePanel.svelte";

  interface Props {
    host: HostEntry;
    /** The profile being edited, or null for a new one. */
    profileId: string | null;
    initial: SetupProfile;
    /** Running Windows VMs, for reading provisioned packages. */
    vms: Vm[];
    onclose: (saved: StoredSetupProfile | null) => void;
  }

  let { host, profileId, initial, vms, onclose }: Props = $props();

  const start = untrack(() => initial);
  let name = $state(start.name);
  let description = $state(start.description ?? "");
  const draft = new SoftwareDraft(start);

  let tab = $state("packages");
  let detail = $state<Detail | null>(null);
  let busy = $state(false);
  let error = $state<string | null>(null);
  let issues = $state<ValidationIssue[]>([]);

  onMount(async () => {
    error = await draft.load(host.key);
  });

  async function save() {
    busy = true;
    error = null;
    issues = [];
    try {
      const profile: SetupProfile = { name: name.trim(), description: description.trim() || undefined, ...draft.toSoftware() };
      onclose(await withElevation(host.key, () => saveSetupProfile(host.key, profileId, profile)));
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
  title={profileId ? `Edit ${start.name}` : "New setup profile"}
  sections={draft.sections()}
  bind:tab
  bind:detail
  controls={{ items: draft.install.length }}
  hud={draft.hud(tab)}
  {error}
  {issues}
  {busy}
  canSave={name.trim() !== ""}
  onsave={save}
  oncancel={() => onclose(null)}
>
  {#snippet header()}
    <label>Name <input bind:value={name} maxlength="60" required /></label>
    <label>Description <input bind:value={description} maxlength="500" placeholder="Optional" /></label>
  {/snippet}
  <SoftwarePanel {host} {vms} {draft} {tab} selected={detail?.key ?? null} onselect={(next) => (detail = next)} />
</ProfileWorkspace>
