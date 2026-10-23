<script lang="ts">
  import {
    createVmFromMachineProfile,
    errorMessage,
    hasProblemCode,
    inspectIso,
    isClientError,
    listIsos,
    ProblemCodes,
    type HostEntry,
    type IsoImage,
    type IsoInspection,
    type MachineProfileSummary,
    type ValidationIssue,
    type VmJob,
  } from "$lib/api/client";
  import { ElevationCancelled, waitForJob, withElevation } from "$lib/lifecycle.svelte";
  import { machineSummary } from "$lib/machineDraft.svelte";
  import { onMount } from "svelte";
  import Dialog from "./Dialog.svelte";
  import JobProgress from "./JobProgress.svelte";

  interface Props {
    host: HostEntry;
    profile: MachineProfileSummary;
    /** True when a VM was created. */
    onclose: (created: boolean) => void;
  }

  let { host, profile, onclose }: Props = $props();

  let name = $state("");
  let isoName = $state("");
  let isos = $state<IsoImage[]>([]);
  let inspection = $state<IsoInspection | null>(null);
  let edition = $state<string | null>(null);
  let busy = $state(false);
  let error = $state<string | null>(null);
  let issues = $state<ValidationIssue[]>([]);
  let warnings = $state<ValidationIssue[]>([]);
  let job = $state<VmJob | null>(null);

  const finished = $derived(job !== null && (job.state === "succeeded" || job.state === "failed"));
  const canCreate = $derived(!busy && name.trim() !== "" && isoName !== "" && job === null);
  const issueFor = (field: string) => issues.find((issue) => issue.field === field)?.message;

  onMount(async () => {
    try {
      isos = await listIsos(host.key);
      await choose(isos[0]?.name ?? "");
    } catch (e) {
      error = errorMessage(e);
    }
  });

  async function choose(image: string) {
    isoName = image;
    inspection = null;
    edition = null;
    if (image === "") return;
    try {
      inspection = await inspectIso(host.key, image);
    } catch {
      // Without an inspection the profile's edition is used.
    }
  }

  async function create(acknowledgeWarnings: boolean) {
    busy = true;
    error = null;
    issues = [];
    try {
      const started = await withElevation(host.key, () =>
        createVmFromMachineProfile(host.key, profile.id, { name: name.trim(), isoName, windowsEdition: edition, acknowledgeWarnings }),
      );
      warnings = [];
      await waitForJob(host.key, started, (update) => (job = update));
    } catch (e) {
      if (e instanceof ElevationCancelled) return;
      if (hasProblemCode(e, ProblemCodes.resourceWarnings) && isClientError(e)) {
        warnings = e.issues;
      } else {
        error = errorMessage(e);
        issues = isClientError(e) ? e.issues : [];
      }
    } finally {
      busy = false;
    }
  }

  function submit(event: SubmitEvent) {
    event.preventDefault();
    create(false);
  }
</script>

<Dialog title="New VM from {profile.name}" wide>
  <form onsubmit={submit} oninput={() => (warnings = [])}>
    <p class="muted">{machineSummary(profile)}. The profile decides the rest; the job builds the VM and starts its install.</p>
    <label>
      Name
      <input bind:value={name} maxlength="100" required placeholder="For example Gaming rig" />
    </label>
    {#if issueFor("name")}<p class="error">{issueFor("name")}</p>{/if}
    <label>
      Install from
      <select value={isoName} onchange={(event) => choose(event.currentTarget.value)}>
        {#if isos.length === 0}<option value="">No images in the ISO library</option>{/if}
        {#each isos as iso (iso.name)}<option value={iso.name}>{iso.name}</option>{/each}
      </select>
    </label>
    {#if issueFor("isoName")}<p class="error">{issueFor("isoName")}</p>{/if}
    {#if inspection?.editions && inspection.editions.length > 1}
      <label>
        Edition
        <select bind:value={edition}>
          <option value={null}>The profile's edition</option>
          {#each inspection.editions as item (item)}<option value={item}>{item}</option>{/each}
        </select>
      </label>
    {/if}

    {#if warnings.length > 0}
      <div class="warning" role="alert">
        {#each warnings as warning (warning.field + warning.message)}<p>{warning.message}</p>{/each}
      </div>
    {/if}
    {#if job}<JobProgress {job} />{/if}
    {#if finished && job?.state === "succeeded"}
      <p class="muted">{name.trim()} is created. Its row in the VM list shows the install, and Connect appears when it is ready.</p>
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
        <button type="button" class="primary" onclick={() => onclose(job?.state === "succeeded")}>Close</button>
      {:else}
        <button type="button" onclick={() => onclose(false)} disabled={busy}>Cancel</button>
        {#if warnings.length > 0}
          <button type="button" class="primary" disabled={busy} onclick={() => create(true)}>Create anyway</button>
        {:else}
          <button type="submit" class="primary" disabled={!canCreate}>{busy ? "Creating…" : "Create"}</button>
        {/if}
      {/if}
    </div>
  </form>
</Dialog>

<style>
  label {
    display: flex;
    flex-direction: column;
    gap: 0.2rem;
  }

  .warning p {
    margin: 0.2rem 0;
  }
</style>
