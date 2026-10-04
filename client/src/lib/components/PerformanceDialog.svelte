<script lang="ts">
  import { onMount } from "svelte";
  import {
    applyVmPerformance,
    errorMessage,
    getHostGpu,
    getHostResources,
    getVmCompute,
    getVmPerformance,
    hasProblemCode,
    isClientError,
    ProblemCodes,
    removeVmPerformance,
    setUpPerformanceGuest,
    type HostEntry,
    type HostGpu,
    type HostResources,
    type PerformanceSettings,
    type ValidationIssue,
    type Vm,
    type VmComputeSettings,
    type VmJob,
    type VmPerformance,
  } from "$lib/api/client";
  import { ElevationCancelled, toGb, toMb, waitForJob, withElevation } from "$lib/lifecycle.svelte";
  import Dialog from "./Dialog.svelte";
  import JobProgress from "./JobProgress.svelte";

  interface Props {
    host: HostEntry;
    vm: Vm;
    onclose: (changed: boolean) => void;
  }

  let { host, vm, onclose }: Props = $props();

  const shares = [
    { key: "vramPercent", label: "Video memory" },
    { key: "encodePercent", label: "Video encode" },
    { key: "decodePercent", label: "Video decode" },
    { key: "computePercent", label: "Compute" },
  ] as const;

  type ShareKey = (typeof shares)[number]["key"];

  let performance = $state<VmPerformance | null>(null);
  let compute = $state<VmComputeSettings | null>(null);
  let gpu = $state<HostGpu | null>(null);
  let resources = $state<HostResources | null>(null);
  let loadError = $state<string | null>(null);

  let processorCount = $state(2);
  let memoryGb = $state(8);
  let instancePath = $state("");
  let percents = $state<Record<ShareKey, number>>({ vramPercent: 50, encodePercent: 50, decodePercent: 50, computePercent: 50 });
  let lowGapGb = $state(1);
  let highGapGb = $state(32);
  let moveStorageTo = $state("");
  let hardwareEncoding = $state(false);

  let busy = $state(false);
  let changed = $state(false);
  let error = $state<string | null>(null);
  let issues = $state<ValidationIssue[]>([]);
  let warnings = $state<ValidationIssue[]>([]);
  let job = $state<VmJob | null>(null);
  let notice = $state<string | null>(null);

  const partitionable = $derived(gpu?.gpus.filter((device) => device.partitionable && device.instancePath) ?? []);
  const off = $derived(compute?.state === "off");
  const running = $derived(compute?.state === "running");
  const jobRunning = $derived(job !== null && job.state === "running");
  const canApply = $derived(off && partitionable.length > 0 && !busy && !jobRunning);
  const driver = $derived(performance?.driver ?? null);

  onMount(load);

  async function load() {
    try {
      [performance, compute, gpu, resources] = await Promise.all([
        getVmPerformance(host.key, vm.id),
        getVmCompute(host.key, vm.id),
        getHostGpu(host.key),
        getHostResources(host.key),
      ]);
      reset();
    } catch (e) {
      loadError = errorMessage(e);
    }
  }

  /** Fills the form from the applied settings, or from the VM's compute settings the first time. */
  function reset() {
    const applied = performance?.settings;
    if (applied) {
      processorCount = applied.processorCount;
      memoryGb = applied.memoryMb / 1024;
      instancePath = applied.gpu?.instancePath ?? "";
      for (const share of shares) percents[share.key] = applied.gpu?.[share.key] ?? 50;
      lowGapGb = (applied.mmio?.lowGapMb ?? 1024) / 1024;
      highGapGb = (applied.mmio?.highGapMb ?? 32768) / 1024;
      hardwareEncoding = applied.rdp?.hardwareEncoding ?? false;
    } else if (compute) {
      processorCount = compute.processorCount;
      memoryGb = Math.max(compute.startupMemoryMb, 4096) / 1024;
    }
    if (!instancePath && partitionable.length > 0) instancePath = partitionable[0].instancePath ?? "";
  }

  function issueFor(field: string): string | undefined {
    return issues.find((issue) => issue.field === field || issue.field.startsWith(`${field}.`))?.message;
  }

  function settings(acknowledgeWarnings: boolean): PerformanceSettings {
    return {
      processorCount,
      memoryMb: toMb(memoryGb),
      gpu: { instancePath: instancePath || null, ...percents },
      mmio: { lowGapMb: Math.round(lowGapGb * 1024), highGapMb: Math.round(highGapGb * 1024) },
      moveStorageTo: moveStorageTo.trim() || null,
      rdp: { hardwareEncoding },
      acknowledgeWarnings,
    };
  }

  /** Runs a request that may start a job, then reads the VM's Performance mode again. */
  async function run(request: () => Promise<VmJob | void>) {
    busy = true;
    error = null;
    issues = [];
    notice = null;
    try {
      const started = await withElevation(host.key, request);
      warnings = [];
      changed = true;
      if (started) {
        const finished = await waitForJob(host.key, started, (next) => (job = next));
        if (finished.state !== "succeeded") return;
      }
      [performance, compute] = await Promise.all([getVmPerformance(host.key, vm.id), getVmCompute(host.key, vm.id)]);
    } catch (e) {
      if (e instanceof ElevationCancelled) return;
      if (hasProblemCode(e, ProblemCodes.resourceWarnings) && isClientError(e)) {
        warnings = e.issues;
      } else if (hasProblemCode(e, ProblemCodes.credentialRequired)) {
        error = `HyperHarbor has no administrator account for ${vm.name} yet. Use Set up… on the VM first, then come back here.`;
      } else {
        error = errorMessage(e);
        issues = isClientError(e) ? e.issues : [];
      }
    } finally {
      busy = false;
    }
  }

  async function apply(acknowledgeWarnings: boolean) {
    await run(() => applyVmPerformance(host.key, vm.id, settings(acknowledgeWarnings)));
    if (performance?.enabled && !error && warnings.length === 0) {
      notice = "Performance mode is on. Start the VM, then set up the guest to copy the GPU driver.";
    }
  }

  async function submit(event: SubmitEvent) {
    event.preventDefault();
    await apply(false);
  }

  async function remove() {
    await run(() => removeVmPerformance(host.key, vm.id));
    if (performance && !performance.enabled && !error) notice = "Performance mode is off. The GPU partition was removed.";
  }

  async function setUpGuest(driversOnly: boolean) {
    await run(() => setUpPerformanceGuest(host.key, vm.id, driversOnly));
  }
</script>

<Dialog title="Performance mode for {vm.name}" wide>
  <form onsubmit={submit} oninput={() => (warnings = [])}>
    {#if loadError}
      <p class="error" role="alert">{loadError}</p>
    {:else if performance === null || compute === null || gpu === null || resources === null}
      <p class="muted">Reading the VM and the host's GPUs…</p>
    {:else}
      <p class="muted">
        Gives the VM a share of the host's GPU and tunes Remote Desktop for graphics: fixed memory, no checkpoints, and a
        LAN connection profile.
      </p>

      {#if partitionable.length === 0}
        <p class="warning" role="alert">This host has no GPU that Hyper-V can partition, so Performance mode is not available.</p>
      {:else if !off}
        <p class="warning">Shut {vm.name} down to change Performance mode. It is {compute.state} now.</p>
      {/if}

      <fieldset disabled={!off || partitionable.length === 0 || busy || jobRunning}>
        <div class="grid">
          <label for="perf-cpu">Virtual processors</label>
          <input id="perf-cpu" type="number" min="1" max={resources.logicalProcessorCount} bind:value={processorCount} />

          <label for="perf-memory">Memory (GB, fixed)</label>
          <input id="perf-memory" type="number" min="0.5" step="any" bind:value={memoryGb} />

          <label for="perf-gpu">GPU</label>
          <select id="perf-gpu" bind:value={instancePath}>
            {#each partitionable as device (device.instancePath)}
              <option value={device.instancePath}>{device.name}{device.driverVersion ? ` (driver ${device.driverVersion})` : ""}</option>
            {/each}
          </select>

          {#each shares as share (share.key)}
            <label for="perf-{share.key}">{share.label}</label>
            <span class="range">
              <input id="perf-{share.key}" type="range" min="1" max="100" bind:value={percents[share.key]} />
              <output for="perf-{share.key}">{percents[share.key]}%</output>
            </span>
          {/each}

          <label for="perf-low">Low MMIO gap (GB)</label>
          <input id="perf-low" type="number" min="0.125" max="3.5" step="any" bind:value={lowGapGb} />

          <label for="perf-high">High MMIO gap (GB)</label>
          <input id="perf-high" type="number" min="1" max="512" step="any" bind:value={highGapGb} />

          <label for="perf-move">Move storage to</label>
          <input id="perf-move" type="text" placeholder="Leave empty to keep the VM where it is" bind:value={moveStorageTo} />
        </div>
        <p class="muted">
          The host has {resources.logicalProcessorCount} logical processors and {toGb(resources.availableMemoryMb)} GB of memory
          free. GPU shares are a percentage of what the GPU allows one partition.
        </p>
        {#each ["processorCount", "memoryMb", "gpu", "mmio", "moveStorageTo"] as name (name)}
          {#if issueFor(name)}<p class="error">{issueFor(name)}</p>{/if}
        {/each}

        <label class="check">
          <input type="checkbox" bind:checked={hardwareEncoding} />
          Hardware H.264 encoding for Remote Desktop <span class="badge">experimental</span>
        </label>
      </fieldset>

      {#if performance.enabled}
        <section class="driver" aria-label="Guest driver">
          <h3>Guest driver</h3>
          {#if driver === null}
            <p class="muted">
              The GPU driver has not been copied into the guest yet. Start {vm.name}, then set up the guest. This also turns on
              the Remote Desktop graphics settings.
            </p>
          {:else}
            <p>
              Host driver {driver.hostVersion ?? "unknown"}; guest copy {driver.guestVersion ?? "none"}.
            </p>
            {#if driver.drift}
              <p class="warning" role="alert">The host's GPU driver changed since it was copied. Re-sync it so the guest matches.</p>
            {/if}
            {#if driver.rebootRequired}
              <p class="warning">Restart {vm.name} to finish replacing driver files that were in use.</p>
            {/if}
          {/if}
          <div class="row">
            <button
              type="button"
              disabled={!running || busy || jobRunning}
              title={running ? "" : "The VM must be running"}
              onclick={() => setUpGuest(false)}>Set up the guest</button
            >
            {#if driver?.drift}
              <button type="button" disabled={!running || busy || jobRunning} onclick={() => setUpGuest(true)}>Re-sync drivers</button>
            {/if}
          </div>
        </section>
      {/if}
    {/if}

    {#if warnings.length > 0}
      <div class="warning" role="alert">
        {#each warnings as warning (warning.field + warning.message)}<p>{warning.message}</p>{/each}
      </div>
    {/if}
    {#if job}<JobProgress {job} />{/if}
    {#if notice}<p role="status">{notice}</p>{/if}
    {#if error}<p class="error" role="alert">{error}</p>{/if}

    <div class="actions">
      {#if performance?.enabled && off}
        <button type="button" class="danger" disabled={busy || jobRunning} onclick={remove}>Turn off Performance mode</button>
      {/if}
      <button type="button" onclick={() => onclose(changed)} disabled={busy}>Close</button>
      {#if warnings.length > 0}
        <button type="button" class="primary" disabled={busy} onclick={() => apply(true)}>Apply anyway</button>
      {:else}
        <button type="submit" class="primary" disabled={!canApply}>{busy && !jobRunning ? "Applying…" : "Apply"}</button>
      {/if}
    </div>
  </form>
</Dialog>

<style>
  fieldset {
    border: 0;
    margin: 0;
    padding: 0;
  }

  .grid {
    display: grid;
    grid-template-columns: max-content 1fr;
    align-items: center;
    gap: 0.4rem 1rem;
  }

  .range {
    display: flex;
    align-items: center;
    gap: 0.5rem;
  }

  .range input {
    flex: 1;
  }

  .range output {
    min-width: 3rem;
    text-align: right;
  }

  .badge {
    display: inline-block;
    margin-left: 0.35rem;
    padding: 0 0.4rem;
    border-radius: 999px;
    font-size: 0.75rem;
    background: var(--warn-bg);
    color: var(--warn-fg);
  }

  .driver h3 {
    margin: 1rem 0 0.35rem;
    font-size: 0.95rem;
  }

  .driver p {
    margin: 0.25rem 0;
  }

  .row {
    display: flex;
    gap: 0.5rem;
    margin-top: 0.5rem;
  }

  .warning p {
    margin: 0.2rem 0;
  }
</style>
