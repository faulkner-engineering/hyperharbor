<script lang="ts">
  import {
    createVmFromMachineProfile,
    createVmFromMachineProfileTemplate,
    errorMessage,
    getMachineProfile,
    hasProblemCode,
    hostSupports,
    inspectIso,
    isClientError,
    listIsos,
    listMachineProfiles,
    listMachineProfileTemplates,
    listSwitches,
    ProblemCodes,
    ProfilesConsolidatedApiVersion,
    type CreateVmFromProfileRequest,
    type HostEntry,
    type IsoImage,
    type IsoInspection,
    type MachineProfile,
    type MachineProfileSummary,
    type MachineProfileTemplate,
    type ValidationIssue,
    type VirtualSwitch,
    type VmJob,
  } from "$lib/api/client";
  import { ElevationCancelled, toGb, toMb, waitForJob, withElevation } from "$lib/lifecycle.svelte";
  import { machineSummary } from "$lib/machineDraft.svelte";
  import { onMount, untrack } from "svelte";
  import Dialog from "./Dialog.svelte";
  import JobProgress from "./JobProgress.svelte";

  interface Props {
    host: HostEntry;
    /** The profile to start from. Without one (New VM on a host at 1.32.0), the dialog asks for a profile or template. */
    profile?: MachineProfileSummary | null;
    /** True when a VM was created. */
    onclose: (created: boolean) => void;
    /** Shown when the ISO library is empty: closes this dialog and opens the library. */
    onopenlibrary?: () => void;
  }

  let { host, profile = null, onclose, onopenlibrary }: Props = $props();

  /** The Create VM dialog's defaults, which the host uses for what a profile leaves out. */
  const DEFAULTS = { processors: 2, startupMb: 4096, maximumMb: 8192, performanceMb: 8192, diskSizeGb: 64 };

  // On a host at 1.32.0 every VM starts from a profile or a template, and its hardware can be changed for this VM.
  const consolidated = $derived(hostSupports(host, ProfilesConsolidatedApiVersion));
  let profiles = $state<MachineProfileSummary[]>([]);
  let templates = $state<MachineProfileTemplate[]>([]);
  let switches = $state<VirtualSwitch[]>([]);
  /** "profile:<id>" or "template:<id>". Starts at the profile the dialog was opened for, then follows the choice. */
  let source = $state(untrack(() => (profile ? `profile:${profile.id}` : "")));
  let chosen = $state<MachineProfile | null>(null);

  let name = $state("");
  let isoName = $state("");
  let isos = $state<IsoImage[]>([]);
  let inspection = $state<IsoInspection | null>(null);
  let edition = $state<string | null>(null);

  // Hardware for this VM, filled from the chosen profile; sent only when changed.
  let processors = $state(DEFAULTS.processors);
  let startupGb = $state(toGb(DEFAULTS.startupMb));
  let maximumGb = $state(toGb(DEFAULTS.maximumMb));
  let dynamic = $state(true);
  let diskSizeGb = $state(DEFAULTS.diskSizeGb);
  let switchName = $state("");
  let profileSwitch = $state("");
  let adjusted = $state(false);

  let busy = $state(false);
  let error = $state<string | null>(null);
  let issues = $state<ValidationIssue[]>([]);
  let warnings = $state<ValidationIssue[]>([]);
  let job = $state<VmJob | null>(null);

  const usableProfiles = $derived(profiles.filter((item) => !item.error));
  const performance = $derived(chosen?.gpu?.enabled === true);
  const finished = $derived(job !== null && (job.state === "succeeded" || job.state === "failed"));
  const canCreate = $derived(!busy && name.trim() !== "" && isoName !== "" && source !== "" && job === null);
  const title = $derived(consolidated || !profile ? `New virtual machine on ${host.displayName}` : `New VM from ${profile.name}`);
  const sourceName = $derived(
    source.startsWith("template:")
      ? templates.find((item) => `template:${item.id}` === source)?.name
      : (profiles.find((item) => `profile:${item.id}` === source)?.name ?? profile?.name),
  );
  const issueFor = (field: string) => issues.find((issue) => issue.field === field)?.message;

  onMount(async () => {
    try {
      const loading: Promise<unknown>[] = [listIsos(host.key).then((list) => (isos = list))];
      if (consolidated) {
        loading.push(
          listMachineProfiles(host.key).then((list) => (profiles = list)),
          listMachineProfileTemplates(host.key).then((list) => (templates = list)),
          listSwitches(host.key).then((list) => (switches = list)),
        );
      }
      await Promise.all(loading);
      if (consolidated && source === "") {
        const first = usableProfiles[0];
        source = first ? `profile:${first.id}` : templates[0] ? `template:${templates[0].id}` : "";
      }
      if (consolidated) await chooseSource(source);
      await choose(isos[0]?.name ?? "");
    } catch (e) {
      error = errorMessage(e);
    }
  });

  /** Reads the chosen profile and fills the hardware from it. */
  async function chooseSource(value: string) {
    source = value;
    chosen = null;
    if (value.startsWith("template:")) {
      chosen = templates.find((item) => `template:${item.id}` === value)?.profile ?? null;
    } else if (value.startsWith("profile:")) {
      try {
        chosen = (await getMachineProfile(host.key, value.slice("profile:".length))).profile;
      } catch (e) {
        error = errorMessage(e);
      }
    }
    fillHardware(chosen);
  }

  function fillHardware(from: MachineProfile | null) {
    const gpu = from?.gpu?.enabled === true;
    const memory = from?.compute?.memory;
    processors = from?.compute?.processors ?? DEFAULTS.processors;
    dynamic = !gpu && (memory?.dynamic ?? true);
    const startupMb = memory?.startupMb ?? (gpu ? DEFAULTS.performanceMb : DEFAULTS.startupMb);
    startupGb = toGb(startupMb);
    maximumGb = toGb(dynamic ? (memory?.maximumMb ?? Math.max(startupMb, DEFAULTS.maximumMb)) : startupMb);
    diskSizeGb = from?.compute?.diskSizeGb ?? DEFAULTS.diskSizeGb;
    profileSwitch = from?.network?.switch ?? switches.find((item) => item.isDefault)?.name ?? switches[0]?.name ?? "";
    switchName = profileSwitch;
    adjusted = false;
  }

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

  function request(acknowledgeWarnings: boolean): CreateVmFromProfileRequest {
    const startupMb = toMb(startupGb);
    return {
      name: name.trim(),
      isoName,
      windowsEdition: edition,
      acknowledgeWarnings,
      ...(consolidated && adjusted
        ? {
            compute: {
              processors,
              diskSizeGb,
              memory: { startupMb, maximumMb: dynamic ? Math.max(toMb(maximumGb), startupMb) : null, dynamic },
            },
          }
        : {}),
      ...(consolidated && switchName !== "" && switchName !== profileSwitch ? { switch: switchName } : {}),
    };
  }

  async function create(acknowledgeWarnings: boolean) {
    busy = true;
    error = null;
    issues = [];
    try {
      const body = request(acknowledgeWarnings);
      const started = await withElevation(host.key, () =>
        source.startsWith("template:")
          ? createVmFromMachineProfileTemplate(host.key, source.slice("template:".length), body)
          : createVmFromMachineProfile(host.key, source.slice("profile:".length), body),
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

<Dialog {title} wide>
  <form onsubmit={submit} oninput={() => (warnings = [])}>
    {#if consolidated}
      <label>
        Start from
        <select value={source} onchange={(event) => chooseSource(event.currentTarget.value)} disabled={job !== null}>
          {#if usableProfiles.length > 0}
            <optgroup label="Your profiles">
              {#each usableProfiles as item (item.id)}<option value="profile:{item.id}">{item.name}</option>{/each}
            </optgroup>
          {/if}
          {#if templates.length > 0}
            <optgroup label="Templates">
              {#each templates as item (item.id)}<option value="template:{item.id}">{item.name}</option>{/each}
            </optgroup>
          {/if}
        </select>
      </label>
      {#if source.startsWith("template:")}
        <p class="muted">{templates.find((item) => `template:${item.id}` === source)?.description} Make a profile from it on the Profiles tab to keep more settings.</p>
      {:else if usableProfiles.find((item) => `profile:${item.id}` === source)}
        <p class="muted">{machineSummary(usableProfiles.find((item) => `profile:${item.id}` === source)!)}. The profile decides the rest.</p>
      {/if}
    {:else if profile}
      <p class="muted">{machineSummary(profile)}. The profile decides the rest; the job builds the VM and starts its install.</p>
    {/if}
    <label>
      Name
      <input bind:value={name} maxlength="100" required placeholder="For example Gaming rig" disabled={job !== null} />
    </label>
    {#if issueFor("name")}<p class="error">{issueFor("name")}</p>{/if}
    <label>
      Install from
      <select value={isoName} onchange={(event) => choose(event.currentTarget.value)} disabled={job !== null}>
        {#if isos.length === 0}<option value="">No images in the ISO library</option>{/if}
        {#each isos as iso (iso.name)}<option value={iso.name}>{iso.name}</option>{/each}
      </select>
    </label>
    {#if isos.length === 0 && onopenlibrary}
      <button type="button" class="link" onclick={onopenlibrary}>Open ISO library</button>
    {/if}
    {#if issueFor("isoName")}<p class="error">{issueFor("isoName")}</p>{/if}
    {#if inspection?.editions && inspection.editions.length > 1}
      <label>
        Edition
        <select bind:value={edition} disabled={job !== null}>
          <option value={null}>The profile's edition</option>
          {#each inspection.editions as item (item)}<option value={item}>{item}</option>{/each}
        </select>
      </label>
    {/if}

    {#if consolidated && chosen}
      <fieldset class="hardware" disabled={job !== null} oninput={() => (adjusted = true)}>
        <legend>Hardware for this VM</legend>
        <p class="muted">From {sourceName}. Changes here are for this VM only; the profile stays as it is.</p>
        <div class="grid">
          <label>Virtual processors <input type="number" min="1" bind:value={processors} /></label>
          <label>Disk size (GB) <input type="number" min="1" max="65536" bind:value={diskSizeGb} /></label>
          <label>{dynamic ? "Startup memory (GB)" : "Memory (GB)"} <input type="number" min="0.5" step="any" bind:value={startupGb} /></label>
          <label>Maximum memory (GB) <input type="number" min="0.5" step="any" bind:value={maximumGb} disabled={!dynamic} /></label>
        </div>
        {#each ["compute.processors", "compute.diskSizeGb", "compute.memory.startupMb", "compute.memory.maximumMb"] as field (field)}
          {#if issueFor(field)}<p class="error">{issueFor(field)}</p>{/if}
        {/each}
        <label class="check">
          <input type="checkbox" bind:checked={dynamic} disabled={performance} />
          Dynamic memory{performance ? " (off: Performance mode needs fixed memory)" : ""}
        </label>
        {#if switches.length > 0}
          <label>
            Network
            <select bind:value={switchName}>
              {#each switches as item (item.id)}<option value={item.name}>{item.name}{item.isDefault ? " (reachable only from the host)" : ""}</option>{/each}
            </select>
          </label>
        {/if}
        {#if issueFor("switch")}<p class="error">{issueFor("switch")}</p>{/if}
      </fieldset>
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
  label:not(.check) {
    display: flex;
    flex-direction: column;
    gap: 0.2rem;
  }

  .warning p {
    margin: 0.2rem 0;
  }

  .hardware {
    display: flex;
    flex-direction: column;
    gap: 0.4rem;
    margin: 0.75rem 0 0.25rem;
    padding: 0.6rem 0.8rem;
    border: 1px solid var(--border);
    border-radius: 6px;
  }

  .hardware legend {
    padding: 0 0.3rem;
    font-weight: 600;
  }

  .grid {
    display: grid;
    grid-template-columns: 1fr 1fr;
    gap: 0.4rem 1rem;
  }

  .link {
    align-self: flex-start;
    padding: 0;
    border: none;
    background: none;
    color: var(--accent);
    cursor: pointer;
  }
</style>
