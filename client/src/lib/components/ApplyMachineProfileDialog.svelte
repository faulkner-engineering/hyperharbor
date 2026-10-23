<script lang="ts">
  import {
    applyVmMachineProfile,
    errorMessage,
    isClientError,
    listMachineProfiles,
    previewVmMachineProfile,
    type HostEntry,
    type MachinePart,
    type MachineProfilePreview,
    type MachineProfileSummary,
    type ValidationIssue,
    type Vm,
    type VmJob,
  } from "$lib/api/client";
  import { ElevationCancelled, waitForJob, withElevation } from "$lib/lifecycle.svelte";
  import { onMount } from "svelte";
  import { SvelteSet } from "svelte/reactivity";
  import Dialog from "./Dialog.svelte";
  import JobProgress from "./JobProgress.svelte";

  interface Props {
    host: HostEntry;
    vm: Vm;
    onclose: () => void;
  }

  let { host, vm, onclose }: Props = $props();

  const PART_LABELS: Record<MachinePart, string> = {
    compute: "Compute",
    firmware: "Firmware",
    gpu: "GPU",
    network: "Network",
    backup: "Backups",
    software: "Software and display",
  };

  let profiles = $state<MachineProfileSummary[]>([]);
  let profileId = $state("");
  let preview = $state<MachineProfilePreview | null>(null);
  let loading = $state(false);
  let shutDown = $state(false);
  let busy = $state(false);
  let error = $state<string | null>(null);
  let issues = $state<ValidationIssue[]>([]);
  let job = $state<VmJob | null>(null);
  const chosen = new SvelteSet<MachinePart>();

  const parts = $derived([...new Set((preview?.changes ?? []).map((change) => change.part))]);
  const needsOff = $derived((preview?.changes ?? []).some((change) => change.needsOff && chosen.has(change.part)));
  const mustShutDown = $derived(needsOff && preview?.vmState === "running");
  const finished = $derived(job !== null && job.state !== "running");
  const canApply = $derived(!busy && job === null && chosen.size > 0 && (!mustShutDown || shutDown));

  onMount(async () => {
    try {
      profiles = (await listMachineProfiles(host.key)).filter((profile) => !profile.error);
      if (profiles.length > 0) await choose(profiles[0].id);
    } catch (e) {
      error = errorMessage(e);
    }
  });

  async function choose(id: string) {
    profileId = id;
    preview = null;
    error = null;
    chosen.clear();
    loading = true;
    try {
      preview = await previewVmMachineProfile(host.key, vm.id, id);
      for (const change of preview.changes) chosen.add(change.part);
    } catch (e) {
      error = errorMessage(e);
    } finally {
      loading = false;
    }
  }

  function toggle(part: MachinePart, on: boolean) {
    if (on) chosen.add(part);
    else chosen.delete(part);
  }

  async function apply() {
    busy = true;
    error = null;
    issues = [];
    try {
      const started = await withElevation(host.key, () =>
        applyVmMachineProfile(host.key, vm.id, { profileId, parts: [...chosen], shutDownToApply: shutDown }),
      );
      await waitForJob(host.key, started, (update) => (job = update));
    } catch (e) {
      if (e instanceof ElevationCancelled) return;
      error = errorMessage(e);
      issues = isClientError(e) ? e.issues : [];
    } finally {
      busy = false;
    }
  }
</script>

<Dialog title="Apply a machine profile to {vm.name}" wide>
  {#if profiles.length === 0 && !error}
    <p class="muted">No machine profiles yet. Make one under Machine profiles.</p>
  {:else}
    <label>
      Machine profile
      <select value={profileId} onchange={(event) => choose(event.currentTarget.value)} disabled={job !== null}>
        {#each profiles as profile (profile.id)}<option value={profile.id}>{profile.name}</option>{/each}
      </select>
    </label>
  {/if}

  {#if loading}<p class="muted">Comparing the profile with {vm.name}…</p>{/if}

  {#if preview}
    {#if preview.changes.length === 0}
      <p class="muted">{vm.name} already matches what this profile can change on an existing VM.</p>
    {:else}
      <div class="parts">
        {#each parts as part (part)}
          <fieldset>
            <legend>
              <label class="check">
                <input type="checkbox" checked={chosen.has(part)} disabled={job !== null} onchange={(event) => toggle(part, event.currentTarget.checked)} />
                {PART_LABELS[part]}
              </label>
            </legend>
            <ul>
              {#each preview.changes.filter((change) => change.part === part) as change (change.setting)}
                <li>
                  <span class="setting">{change.setting}</span>
                  <span class="values">{change.current ?? "Not set"} → <strong>{change.wanted}</strong></span>
                  {#if change.needsOff}<span class="tag">VM off</span>{/if}
                  {#if change.needsGuest}<span class="tag">In the guest</span>{/if}
                </li>
              {/each}
            </ul>
          </fieldset>
        {/each}
      </div>
    {/if}
    {#if preview.notes.length > 0}
      <ul class="notes">
        {#each preview.notes as note, index (index)}<li>{note.message}</li>{/each}
      </ul>
    {/if}
    {#if preview.warnings.length > 0}
      <div class="warning" role="alert">
        {#each preview.warnings as warning (warning.field + warning.message)}<p>{warning.message}</p>{/each}
      </div>
    {/if}
    {#if mustShutDown}
      <label class="check">
        <input type="checkbox" bind:checked={shutDown} disabled={job !== null} />
        Shut {vm.name} down, apply, and start it again
      </label>
    {/if}
  {/if}

  {#if job}<JobProgress {job} />{/if}
  {#if finished && job?.state === "succeeded"}
    {@const result = job.setupResult}
    <p class="muted">Applied{result ? ` ${result.applied} items` : ""}{result?.restarted ? " and restarted the guest" : ""}.</p>
    {#if result && result.problems.length > 0}
      <div class="warning" role="alert">
        {#each result.problems as problem, index (index)}<p>{problem}</p>{/each}
      </div>
    {/if}
  {/if}
  {#if error}
    <div class="error" role="alert">
      {error}
      {#if issues.length > 0}
        <ul>{#each issues as issue (issue.field)}<li><code>{issue.field}</code> {issue.message}</li>{/each}</ul>
      {/if}
    </div>
  {/if}

  <div class="actions">
    {#if finished}
      <button type="button" class="primary" onclick={onclose}>Close</button>
    {:else}
      <button type="button" onclick={onclose} disabled={busy}>Cancel</button>
      <button type="button" class="primary" onclick={apply} disabled={!canApply}>{busy ? "Applying…" : "Apply"}</button>
    {/if}
  </div>
</Dialog>

<style>
  label:not(.check) {
    display: flex;
    flex-direction: column;
    gap: 0.2rem;
    margin-bottom: 0.6rem;
  }

  .parts {
    display: flex;
    flex-direction: column;
    gap: 0.5rem;
  }

  fieldset {
    border: 1px solid var(--border);
    border-radius: 6px;
    padding: 0.3rem 0.7rem 0.5rem;
    margin: 0;
  }

  legend .check {
    margin: 0;
    font-weight: 600;
  }

  ul {
    list-style: none;
    margin: 0;
    padding: 0;
  }

  li {
    display: flex;
    flex-wrap: wrap;
    align-items: baseline;
    gap: 0.5rem;
    padding: 0.2rem 0;
    font-size: 0.9rem;
  }

  .setting {
    min-width: 10rem;
  }

  .values {
    color: var(--muted);
  }

  .values strong {
    color: var(--text);
    font-weight: 600;
  }

  .tag {
    font-size: 0.72rem;
    padding: 0 0.4rem;
    border-radius: 4px;
    background: var(--idle-bg);
    color: var(--muted);
  }

  .notes {
    margin: 0.6rem 0;
    color: var(--muted);
    font-size: 0.85rem;
  }

  .notes li {
    display: list-item;
    list-style: disc inside;
  }
</style>
