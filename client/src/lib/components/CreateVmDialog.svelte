<script lang="ts">
  import { onMount } from "svelte";
  import {
    createVm,
    errorMessage,
    getHostResources,
    hasProblemCode,
    inspectIso,
    isClientError,
    listIsos,
    listSwitches,
    listUnattendProfiles,
    openConsole,
    ProblemCodes,
    type HostEntry,
    type HostResources,
    type IsoImage,
    type IsoInspection,
    type UnattendProfile,
    type ValidationIssue,
    type VirtualSwitch,
    type VmJob,
  } from "$lib/api/client";
  import { ElevationCancelled, toGb, toMb, waitForJob, withElevation } from "$lib/lifecycle.svelte";
  import Dialog from "./Dialog.svelte";
  import JobProgress from "./JobProgress.svelte";
  import { toasts } from "$lib/toasts.svelte";

  interface Props {
    host: HostEntry;
    onclose: (created: boolean) => void;
    /** Shown when the library is empty: closes this dialog and opens the ISO library. */
    onopenlibrary?: () => void;
    /** Closes this dialog and opens the install profiles. */
    onopenprofiles?: () => void;
  }

  let { host, onclose, onopenlibrary, onopenprofiles }: Props = $props();

  let resources = $state<HostResources | null>(null);
  let isos = $state<IsoImage[]>([]);
  let switches = $state<VirtualSwitch[]>([]);
  let loadError = $state<string | null>(null);

  let name = $state("");
  let isoName = $state("");
  let diskSizeGb = $state(64);
  let processorCount = $state(2);
  let startupMemoryGb = $state(4);
  let maximumMemoryGb = $state(8);
  let dynamicMemory = $state(true);
  let switchId = $state<string | null>(null);
  let enableTpm = $state(true);

  // Unattended install. Profiles that do not match what the image installs are not offered.
  let profiles = $state<UnattendProfile[]>([]);
  let inspection = $state<IsoInspection | null>(null);
  let inspecting = $state(false);
  let installMode = $state<"console" | "unattended">("console");
  let profileId = $state("");
  let windowsEdition = $state("");
  let computerName = $state("");

  const matchingProfiles = $derived(
    inspection?.os ? profiles.filter((profile) => profile.os === inspection?.os) : [],
  );
  const selectedProfile = $derived(matchingProfiles.find((profile) => profile.id === profileId) ?? null);
  const unattended = $derived(installMode === "unattended" && selectedProfile !== null);
  const suggestedComputerName = $derived(
    name
      .toUpperCase()
      .replace(/[^A-Z0-9]+/g, "-")
      .slice(0, 15)
      .replace(/^-+|-+$/g, "") || "HYPERHARBOR-VM",
  );

  let busy = $state(false);
  let error = $state<string | null>(null);
  let issues = $state<ValidationIssue[]>([]);
  let warnings = $state<ValidationIssue[]>([]);
  let job = $state<VmJob | null>(null);

  const finished = $derived(job !== null && job.state !== "running");
  const canSubmit = $derived(
    name.trim() !== "" &&
      isoName !== "" &&
      !busy &&
      job === null &&
      resources !== null &&
      (installMode === "console" || unattended),
  );

  onMount(async () => {
    try {
      [resources, isos, switches, profiles] = await Promise.all([
        getHostResources(host.key),
        listIsos(host.key),
        listSwitches(host.key),
        // An older host has no profiles; only console installs are offered then.
        listUnattendProfiles(host.key).catch(() => []),
      ]);
      processorCount = Math.min(2, resources.logicalProcessorCount);
      chooseIso(isos[0]?.name ?? "");
      switchId = switches.find((item) => item.isDefault)?.id ?? switches[0]?.id ?? null;
    } catch (e) {
      loadError = errorMessage(e);
    }
  });

  let inspectionRequest = 0;

  /** Asks the host what an image installs. An answer for an image that is no longer chosen is ignored. */
  async function inspect(image: string) {
    const request = ++inspectionRequest;
    inspection = null;
    if (image === "") return;
    inspecting = true;
    try {
      const result = await inspectIso(host.key, image);
      if (request !== inspectionRequest) return;
      inspection = result;
      chooseProfile(profiles.find((profile) => profile.os === result.os)?.id ?? "");
    } catch {
      // An older host cannot inspect images; only console installs are offered then.
    } finally {
      if (request === inspectionRequest) inspecting = false;
    }
  }

  function chooseIso(image: string) {
    isoName = image;
    void inspect(image);
  }

  /** Selects a profile and its default edition, when the image has it. */
  function chooseProfile(id: string) {
    profileId = id;
    const profile = profiles.find((item) => item.id === id);
    const editions = inspection?.editions ?? [];
    const wanted = profile?.windows?.defaultEdition;
    windowsEdition = editions.find((edition) => edition === wanted) ?? editions[0] ?? "";
  }

  async function openNewVmConsole() {
    if (!job?.vmId) return;
    try {
      await openConsole(host.key, job.vmId);
      toasts.show(`Opening the console of ${name.trim()}…`);
    } catch (e) {
      toasts.error(errorMessage(e));
    }
  }

  function issueFor(field: string): string | undefined {
    return issues.find((issue) => issue.field === field)?.message;
  }

  function formatSize(bytes: number): string {
    return bytes >= 1024 ** 3 ? `${(bytes / 1024 ** 3).toFixed(1)} GB` : `${Math.round(bytes / 1024 ** 2)} MB`;
  }

  async function submit(event: SubmitEvent) {
    event.preventDefault();
    await create(false);
  }

  async function create(acknowledgeWarnings: boolean) {
    busy = true;
    error = null;
    issues = [];
    const startupMemoryMb = toMb(startupMemoryGb);
    const request = {
      name: name.trim(),
      isoName,
      diskSizeGb,
      processorCount,
      startupMemoryMb,
      maximumMemoryMb: dynamicMemory ? Math.max(toMb(maximumMemoryGb), startupMemoryMb) : startupMemoryMb,
      dynamicMemory,
      switchId,
      enableTpm,
      acknowledgeWarnings,
      install: unattended
        ? {
            profileId,
            windowsEdition: selectedProfile?.os === "windows" ? windowsEdition : null,
            computerName: selectedProfile?.os === "windows" && computerName.trim() !== "" ? computerName.trim() : null,
          }
        : null,
    };
    try {
      const started = await withElevation(host.key, () => createVm(host.key, request));
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
</script>

<Dialog title="New virtual machine on {host.displayName}" wide>
  <!-- Any edit after a warning means the next Create checks the new values again. -->
  <form onsubmit={submit} oninput={() => (warnings = [])}>
    {#if loadError}
      <p class="error" role="alert">{loadError}</p>
    {:else if resources === null}
      <p class="muted">Reading the host's resources…</p>
    {:else}
      <p class="muted">
        Generation 2 with Secure Boot, a new dynamic disk, and the ISO first in the boot order.
        {installMode === "console"
          ? "The VM is created off; start it and install the operating system from its console."
          : "The VM starts and installs the operating system by itself; follow it from the VM list or its console."}
      </p>
      <p class="muted">Stored on the host in {resources.virtualHardDiskFolder}.</p>

      <label for="vm-name">Name</label>
      <input id="vm-name" bind:value={name} maxlength="100" autocomplete="off" spellcheck="false" disabled={job !== null} />
      {#if issueFor("name")}<p class="error">{issueFor("name")}</p>{/if}

      <label for="vm-iso">Installation image</label>
      {#if isos.length === 0}
        <div class="warning">
          <p>The host's ISO library is empty. Add an installation image first.</p>
          {#if onopenlibrary}
            <button type="button" onclick={onopenlibrary}>Open ISO library</button>
          {/if}
        </div>
      {:else}
        <select id="vm-iso" value={isoName} onchange={(event) => chooseIso(event.currentTarget.value)} disabled={job !== null}>
          {#each isos as iso (iso.name)}
            <option value={iso.name}>{iso.name} ({formatSize(iso.sizeBytes)})</option>
          {/each}
        </select>
      {/if}
      {#if issueFor("isoName")}<p class="error">{issueFor("isoName")}</p>{/if}

      <fieldset class="install" disabled={job !== null}>
        <legend>Operating system install</legend>
        <label class="check">
          <input type="radio" name="install-mode" value="console" bind:group={installMode} />
          From the console (any image)
        </label>
        <label class="check">
          <input type="radio" name="install-mode" value="unattended" bind:group={installMode} />
          Automatically with a profile
        </label>

        {#if installMode === "unattended"}
          {#if inspecting}
            <p class="muted">Reading what {isoName} installs…</p>
          {:else if !inspection?.os}
            <p class="warning">
              HyperHarbor cannot tell what {isoName || "this image"} installs. Automatic installs work with Windows
              Setup media and Ubuntu installers; install this one from the console.
            </p>
          {:else if matchingProfiles.length === 0}
            <p class="warning">No profile installs {inspection.os === "windows" ? "Windows" : "Linux"}. Create one first.</p>
          {:else}
            <p class="muted">{inspection.distribution ?? ""}</p>
            <label for="vm-profile">Profile</label>
            <select id="vm-profile" value={profileId} onchange={(event) => chooseProfile(event.currentTarget.value)}>
              {#each matchingProfiles as profile (profile.id)}
                <option value={profile.id}>{profile.name}{profile.builtIn ? " (built in)" : ""}</option>
              {/each}
            </select>
            {#if issueFor("install.profileId")}<p class="error">{issueFor("install.profileId")}</p>{/if}

            {#if selectedProfile?.os === "windows"}
              <label for="vm-edition">Edition</label>
              <select id="vm-edition" bind:value={windowsEdition}>
                {#each inspection.editions as edition (edition)}
                  <option value={edition}>{edition}</option>
                {/each}
              </select>
              {#if issueFor("install.windowsEdition")}<p class="error">{issueFor("install.windowsEdition")}</p>{/if}

              <label for="vm-computer">Computer name</label>
              <input
                id="vm-computer"
                bind:value={computerName}
                maxlength="15"
                placeholder={suggestedComputerName}
                autocomplete="off"
                spellcheck="false"
              />
              {#if issueFor("install.computerName")}<p class="error">{issueFor("install.computerName")}</p>{/if}
              <p class="muted">
                Setup creates {selectedProfile.adminAccountName} with a one-time password the host changes once setup is
                done, and your Remote Desktop account.
              </p>
            {:else if selectedProfile}
              <p class="muted">
                The Ubuntu installer asks "Continue with autoinstall?" before it changes the disk. Open the console
                and type yes.
              </p>
            {/if}
          {/if}
          {#if onopenprofiles}
            <button type="button" class="link" onclick={onopenprofiles}>Manage profiles…</button>
          {/if}
        {/if}
      </fieldset>

      <div class="grid">
        <div>
          <label for="vm-cpu">Virtual processors (host has {resources.logicalProcessorCount})</label>
          <input id="vm-cpu" type="number" min="1" max={resources.logicalProcessorCount} bind:value={processorCount} disabled={job !== null} />
          {#if issueFor("processorCount")}<p class="error">{issueFor("processorCount")}</p>{/if}
        </div>
        <div>
          <label for="vm-disk">Disk size (GB)</label>
          <input id="vm-disk" type="number" min="1" max="65536" bind:value={diskSizeGb} disabled={job !== null} />
          {#if issueFor("diskSizeGb")}<p class="error">{issueFor("diskSizeGb")}</p>{/if}
        </div>
        <div>
          <label for="vm-startup">{dynamicMemory ? "Startup memory (GB)" : "Memory (GB)"}</label>
          <input id="vm-startup" type="number" min="0.1" step="any" bind:value={startupMemoryGb} disabled={job !== null} />
          {#if issueFor("startupMemoryMb")}<p class="error">{issueFor("startupMemoryMb")}</p>{/if}
        </div>
        <div>
          <label for="vm-maximum">Maximum memory (GB)</label>
          <input id="vm-maximum" type="number" min="0.1" step="any" bind:value={maximumMemoryGb} disabled={!dynamicMemory || job !== null} />
          {#if issueFor("maximumMemoryMb")}<p class="error">{issueFor("maximumMemoryMb")}</p>{/if}
        </div>
      </div>
      <p class="muted">
        The host has {toGb(resources.totalMemoryMb)} GB of memory, {toGb(resources.availableMemoryMb)} GB free now.
      </p>

      <label class="check">
        <input type="checkbox" bind:checked={dynamicMemory} disabled={job !== null} />
        Dynamic memory
      </label>
      <label class="check">
        <input type="checkbox" bind:checked={enableTpm} disabled={job !== null} />
        Virtual TPM (Windows 11 needs one)
      </label>

      <label for="vm-switch">Network</label>
      {#if switches.length === 0}
        <p class="muted">The host has no virtual switches, so the VM will have no network adapter.</p>
      {:else}
        <select id="vm-switch" bind:value={switchId} disabled={job !== null}>
          {#each switches as item (item.id)}
            <option value={item.id}>{item.name}{item.isDefault ? " (reachable only from the host)" : ""}</option>
          {/each}
        </select>
      {/if}
      {#if issueFor("switchId")}<p class="error">{issueFor("switchId")}</p>{/if}
    {/if}

    {#if warnings.length > 0}
      <div class="warning" role="alert">
        {#each warnings as warning (warning.field + warning.message)}<p>{warning.message}</p>{/each}
      </div>
    {/if}
    {#if job}<JobProgress {job} />{/if}
    {#if finished && job?.state === "succeeded" && unattended}
      <p class="muted">
        {name.trim()} is installing. Its row in the VM list shows how far it is, and Connect appears when it is ready.
      </p>
    {/if}
    {#if error}<p class="error" role="alert">{error}</p>{/if}

    <div class="actions">
      {#if finished}
        {#if job?.state === "succeeded" && unattended && job.vmId}
          <button type="button" onclick={openNewVmConsole}>Console</button>
        {/if}
        <button type="button" class="primary" onclick={() => onclose(job?.state === "succeeded")}>Close</button>
      {:else}
        <button type="button" onclick={() => onclose(false)} disabled={busy}>Cancel</button>
        {#if warnings.length > 0}
          <button type="button" class="primary" disabled={busy} onclick={() => create(true)}>Create anyway</button>
        {:else}
          <button type="submit" class="primary" disabled={!canSubmit}>{busy ? "Creating…" : "Create"}</button>
        {/if}
      {/if}
    </div>
  </form>
</Dialog>

<style>
  .grid {
    display: grid;
    grid-template-columns: 1fr 1fr;
    gap: 0.4rem 1rem;
    margin-top: 0.25rem;
  }

  .grid > div {
    display: flex;
    flex-direction: column;
    gap: 0.3rem;
  }

  .warning p {
    margin: 0.2rem 0;
  }

  .install {
    display: flex;
    flex-direction: column;
    gap: 0.3rem;
    margin: 0.75rem 0 0.25rem;
    padding: 0.6rem 0.8rem;
    border: 1px solid var(--border);
    border-radius: 6px;
  }

  .install legend {
    padding: 0 0.3rem;
    font-weight: 600;
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
