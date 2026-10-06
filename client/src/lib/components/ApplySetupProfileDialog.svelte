<script lang="ts">
  import { onMount } from "svelte";
  import {
    applyVmSetupProfile,
    errorMessage,
    listSetupProfiles,
    type HostEntry,
    type SetupProfileSummary,
    type Vm,
    type VmJob,
  } from "$lib/api/client";
  import { ElevationCancelled, waitForJob, withElevation } from "$lib/lifecycle.svelte";
  import Dialog from "./Dialog.svelte";
  import InfoTip from "./InfoTip.svelte";
  import JobProgress from "./JobProgress.svelte";

  interface Props {
    host: HostEntry;
    vm: Vm;
    onclose: () => void;
  }

  let { host, vm, onclose }: Props = $props();

  let profiles = $state<SetupProfileSummary[] | null>(null);
  let loadError = $state<string | null>(null);
  let profileId = $state("");
  let restartIfNeeded = $state(true);
  let busy = $state(false);
  let error = $state<string | null>(null);
  let job = $state<VmJob | null>(null);

  // Profiles whose file does not read on the host cannot be applied.
  const usable = $derived(profiles?.filter((profile) => !profile.error) ?? []);
  const chosen = $derived(usable.find((profile) => profile.id === profileId) ?? null);
  const finished = $derived(job !== null && job.state !== "running");
  const result = $derived(job?.state === "succeeded" ? (job.setupResult ?? null) : null);
  const canApply = $derived(chosen !== null && !busy && job === null);

  onMount(async () => {
    try {
      profiles = await listSetupProfiles(host.key);
      profileId = profiles.find((profile) => !profile.error)?.id ?? "";
    } catch (e) {
      loadError = errorMessage(e);
    }
  });

  async function submit(event: SubmitEvent) {
    event.preventDefault();
    if (!canApply) return;
    busy = true;
    error = null;
    try {
      const started = await withElevation(host.key, () => applyVmSetupProfile(host.key, vm.id, profileId, restartIfNeeded));
      await waitForJob(host.key, started, (update) => (job = update));
    } catch (e) {
      if (!(e instanceof ElevationCancelled)) error = errorMessage(e);
    } finally {
      busy = false;
    }
  }
</script>

<Dialog title="Apply a setup profile to {vm.name}">
  <form onsubmit={submit}>
    {#if loadError}
      <p class="error" role="alert">{loadError}</p>
    {:else if profiles === null}
      <p class="muted">Reading your setup profiles…</p>
    {:else if usable.length === 0}
      <p class="muted">You have no setup profiles yet. Create one on the Setup profiles tab, or capture one from a VM.</p>
    {:else}
      <div class="label-row">
        <label for="apply-profile">Setup profile</label>
        <InfoTip label="About installing software that needs administrator approval">
          The host runs this as {vm.name}'s administrator, so software that needs administrator approval installs
          without a prompt. Your HyperHarbor account in the VM stays a standard user. Only packages winget or the
          Microsoft Store can install silently are supported.
        </InfoTip>
      </div>
      <select id="apply-profile" bind:value={profileId} disabled={busy || job !== null}>
        {#each usable as profile (profile.id)}
          <option value={profile.id}>{profile.name}</option>
        {/each}
      </select>
      {#if chosen}
        <p class="muted">
          Installs {chosen.installCount}, removes {chosen.removeCount}, and applies {chosen.tweakCount}
          {chosen.tweakCount === 1 ? "tweak" : "tweaks"}{chosen.browser ? `, with ${chosen.browser} set up` : ""}. Apps and
          items already in place are left as they are.
        </p>
      {/if}

      <label class="check">
        <input type="checkbox" bind:checked={restartIfNeeded} disabled={busy || job !== null} />
        Restart {vm.name} if a change needs it
      </label>
      <p class="muted">
        If you are signed in to {vm.name}, save your work first. Some settings show up only after you sign out and back
        in.
      </p>
    {/if}

    {#if job}<JobProgress {job} />{/if}
    {#if result}
      <div role="status">
        <p>
          {result.applied}
          {result.applied === 1 ? "item" : "items"} applied{result.restarted ? `, and ${vm.name} was restarted` : ""}.
        </p>
        {#if result.restartPending}
          <p class="warning">Restart {vm.name} to finish.</p>
        {/if}
        {#if result.problems.length > 0}
          <p class="warning">These could not be applied:</p>
          <ul class="problems">
            {#each result.problems as problem, index (index)}<li>{problem}</li>{/each}
          </ul>
        {/if}
      </div>
    {/if}
    {#if error}<p class="error" role="alert">{error}</p>{/if}

    <div class="actions">
      {#if finished}
        <button type="button" class="primary" onclick={onclose}>Close</button>
      {:else}
        <button type="button" onclick={onclose} disabled={busy}>Cancel</button>
        <button type="submit" class="primary" disabled={!canApply}>{busy ? "Applying…" : "Apply"}</button>
      {/if}
    </div>
  </form>
</Dialog>

<style>
  .label-row {
    display: flex;
    align-items: center;
    gap: 0.4rem;
  }

  .problems {
    margin: 0 0 0.5rem 1.2rem;
    padding: 0;
    font-size: 0.85rem;
  }
</style>
