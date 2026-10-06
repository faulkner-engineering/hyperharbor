<script lang="ts">
  import { onMount } from "svelte";
  import {
    checkHostUpdate,
    errorMessage,
    getHostUpdate,
    hostSupports,
    installHostUpdate,
    InstallUpdateApiVersion,
    setHostUpdateSettings,
    type HostEntry,
    type HostUpdateMode,
    type HostUpdateProgress,
    type HostUpdateStatus,
  } from "$lib/api/client";
  import { ElevationCancelled, withElevation } from "$lib/lifecycle.svelte";

  interface Props {
    host: HostEntry;
  }

  let { host }: Props = $props();

  const POLL_MS = 5000;
  /** While Check now waits for the host (it gives up after CHECK_TIMEOUT_MS) or a version downloads, it polls faster. */
  const CHECK_POLL_MS = 1500;
  const CHECK_TIMEOUT_MS = 90_000;

  let status = $state<HostUpdateStatus | null>(null);
  let loadError = $state<string | null>(null);
  let checking = $state(false);
  let editing = $state(false);
  let saving = $state(false);
  let saveError = $state<string | null>(null);
  let channel = $state("stable");
  let mode = $state<HostUpdateMode>("auto");
  let maintenanceTime = $state("");
  let startingInstall = $state(false);
  /** The version this device asked the host to install, until the host runs it or gives up. */
  let installTarget = $state<string | null>(null);
  /**
   * A check this device asked for, until the host reports a newer lastCheck. The host answers Check now before the
   * check runs (older hosts even report it as idle), so the reply alone says nothing about the result.
   */
  let pendingCheck = $state<{ since: number; before: string | null } | null>(null);
  /** What the last check from this device found, shown until the next one. */
  let checkOutcome = $state<{ text: string; failed: boolean; at: Date } | null>(null);

  // Older hosts install only from their tray.
  const canInstallHere = $derived(hostSupports(host, InstallUpdateApiVersion));

  // Checking, downloading, and installing move on by themselves, so watch them until they settle.
  const working = $derived(
    installTarget !== null ||
      pendingCheck !== null ||
      (status !== null && (status.activity === "checking" || status.activity === "preparing" || status.activity === "installing")),
  );

  const summary = $derived.by(() => {
    if (!status) return "";
    if (!status.supported) return status.message ?? "This host does not update itself.";
    if (pendingCheck) return "Checking for updates…";
    switch (status.activity) {
      case "checking":
        return "Checking for updates…";
      case "preparing":
        return `Downloading and testing version ${status.availableVersion}…`;
      case "ready":
        return status.mode === "auto"
          ? `Version ${status.availableVersion} is ready. It installs when nothing is in progress on the host, or at the maintenance time.`
          : canInstallHere
            ? `Version ${status.availableVersion} is ready to install.`
            : `Version ${status.availableVersion} is ready. Install it from the HyperHarbor Host window on the host.`;
      case "installing":
        return `Installing version ${status.availableVersion}. The host restarts and is back in a minute or two.`;
      default:
        return status.message ?? (status.lastCheck ? "Up to date." : "Not checked yet.");
    }
  });

  const MB = 1024 * 1024;

  /** What preparing a version is doing (API 1.15.0 hosts report it), and how far it is when that is known. */
  function preparationText(progress: HostUpdateProgress): { text: string; fraction: number | null } {
    if (progress.step === "verifying") return { text: "Checking the downloaded package…", fraction: null };
    if (progress.step === "testing") return { text: "Testing the new version on a copy of the host's data…", fraction: null };
    const fraction = progress.bytesTotal > 0 ? Math.min(1, progress.bytesDone / progress.bytesTotal) : 0;
    return {
      text: `Downloading: ${(progress.bytesDone / MB).toFixed(0)} of ${(progress.bytesTotal / MB).toFixed(0)} MB (${Math.floor(fraction * 100)}%)`,
      fraction,
    };
  }

  async function load() {
    try {
      status = await getHostUpdate(host.key);
      loadError = null;
      // The host runs the new version, or came back without it (rolled back, or the install was refused).
      if (installTarget && (status.currentVersion === installTarget || status.activity === "idle")) installTarget = null;
      settleCheck();
    } catch (error) {
      // While an update installs the host restarts, so a failed poll is expected for a while.
      loadError = status?.activity === "installing" || installTarget ? null : errorMessage(error);
    }
  }

  onMount(() => {
    void load();
  });

  $effect(() => {
    if (!working) return;
    const timer = setInterval(() => void load(), pendingCheck || status?.activity === "preparing" ? CHECK_POLL_MS : POLL_MS);
    return () => clearInterval(timer);
  });

  async function checkNow() {
    checking = true;
    checkOutcome = null;
    const before = status?.lastCheck ?? null;
    try {
      status = await checkHostUpdate(host.key);
      loadError = null;
      pendingCheck = { since: Date.now(), before };
      settleCheck();
    } catch (error) {
      loadError = errorMessage(error);
    } finally {
      checking = false;
    }
  }

  /** Ends a pending check once the host reports a new check time, or after CHECK_TIMEOUT_MS. */
  function settleCheck() {
    if (!pendingCheck || !status) return;
    if (status.lastCheck !== pendingCheck.before && status.activity !== "checking") {
      const failed = status.message?.startsWith("The update check failed") ?? false;
      checkOutcome = {
        failed,
        at: new Date(),
        text: failed
          ? (status.message ?? "The update check failed.")
          : status.availableVersion
            ? `Found version ${status.availableVersion}.`
            : `Up to date. ${status.currentVersion} is the newest version on the ${status.channel} channel.`,
      };
      pendingCheck = null;
    } else if (Date.now() - pendingCheck.since > CHECK_TIMEOUT_MS) {
      checkOutcome = {
        failed: true,
        at: new Date(),
        text: "The host has not finished checking after 90 seconds. It may not reach GitHub; the host log has details.",
      };
      pendingCheck = null;
    }
  }

  async function installNow() {
    if (!status?.availableVersion) return;
    const target = status.availableVersion;
    startingInstall = true;
    loadError = null;
    try {
      status = await withElevation(host.key, () => installHostUpdate(host.key));
      installTarget = target;
    } catch (error) {
      if (!(error instanceof ElevationCancelled)) loadError = errorMessage(error);
    } finally {
      startingInstall = false;
    }
  }

  function startEditing() {
    if (!status) return;
    channel = status.channel;
    mode = status.mode;
    maintenanceTime = status.maintenanceTime ?? "";
    saveError = null;
    editing = true;
  }

  async function save(event: SubmitEvent) {
    event.preventDefault();
    saving = true;
    saveError = null;
    try {
      status = await withElevation(host.key, () =>
        setHostUpdateSettings(host.key, { channel, mode, maintenanceTime: maintenanceTime || null }),
      );
      editing = false;
    } catch (error) {
      if (!(error instanceof ElevationCancelled)) saveError = errorMessage(error);
    } finally {
      saving = false;
    }
  }

  function describeMode(current: HostUpdateStatus) {
    switch (current.mode) {
      case "notify":
        return "Updates download automatically; the owner installs them at the host.";
      case "off":
        return "Automatic checks are off.";
      default:
        return current.maintenanceTime
          ? `Updates install automatically when nothing is in progress, or after ${current.maintenanceTime}.`
          : "Updates install automatically when nothing is in progress.";
    }
  }
</script>

<section class="panel" aria-label="Host updates">
  <h3>Host updates</h3>

  {#if loadError}
    <p class="error" role="alert">{loadError}</p>
  {/if}

  {#if status}
    <p><strong>Version {status.currentVersion}.</strong> {summary}</p>
    {#if status.activity === "preparing" && status.progress}
      {@const progress = preparationText(status.progress)}
      <div class="progress">
        {#if progress.fraction === null}
          <progress aria-label="Preparing version {status.availableVersion}"></progress>
        {:else}
          <progress aria-label="Preparing version {status.availableVersion}" max="100" value={Math.round(progress.fraction * 100)}></progress>
        {/if}
        <p class="muted">{progress.text}</p>
      </div>
    {/if}

    {#if status.supported}
      <p class="muted">Following the <strong>{status.channel}</strong> channel. {describeMode(status)}</p>

      {#if status.lastResult}
        <p class="muted">Last update: {status.lastResult}</p>
      {/if}
      {#if status.rolledBack.length > 0}
        <p class="muted">
          Skipped because they did not start on this host: {status.rolledBack.join(", ")}.
        </p>
      {/if}
      {#if checkOutcome}
        <p class:error={checkOutcome.failed} role="status">
          {checkOutcome.text} <span class="muted">({checkOutcome.at.toLocaleTimeString()})</span>
        </p>
      {/if}
      {#if status.lastCheck}
        <p class="muted">Checked {new Date(status.lastCheck).toLocaleString()}.</p>
      {/if}

      {#if editing}
        <form onsubmit={save}>
          <label>
            Channel
            <select bind:value={channel}>
              {#each status.channels as option (option)}
                <option value={option}>{option}</option>
              {/each}
            </select>
          </label>
          <label>
            Mode
            <select bind:value={mode}>
              <option value="auto">Install automatically</option>
              <option value="notify">Download, install at the host</option>
              <option value="off">Do not check</option>
            </select>
          </label>
          <label>
            Maintenance time (host clock, empty for none)
            <input type="time" bind:value={maintenanceTime} />
          </label>
          {#if saveError}
            <p class="error" role="alert">{saveError}</p>
          {/if}
          <div class="actions">
            <button type="submit" class="primary" disabled={saving}>{saving ? "Saving…" : "Save"}</button>
            <button type="button" onclick={() => (editing = false)} disabled={saving}>Cancel</button>
          </div>
        </form>
      {:else}
        <div class="actions">
          {#if status.activity === "ready" && canInstallHere && installTarget === null}
            <button
              type="button"
              class="primary"
              onclick={installNow}
              disabled={startingInstall}
              title="Install now, whatever the update mode. Work in progress on the host finishes first, and the host restarts."
              >{startingInstall ? "Starting the install…" : `Install ${status.availableVersion} now`}</button
            >
          {/if}
          <button type="button" onclick={checkNow} disabled={checking || working} aria-busy={checking || pendingCheck !== null}>
            {checking || pendingCheck ? "Checking…" : "Check now"}
          </button>
          <button type="button" onclick={startEditing} disabled={working}>Change settings…</button>
        </div>
      {/if}
    {/if}
  {:else if !loadError}
    <p class="muted">Loading…</p>
  {/if}
</section>

<style>
  .panel {
    margin-top: 1.25rem;
    padding: 1.25rem;
    border: 1px solid var(--border);
    border-radius: 8px;
    background: var(--surface);
    max-width: 640px;
  }

  h3 {
    margin: 0 0 0.75rem;
    font-size: 1.05rem;
  }

  .progress {
    display: grid;
    gap: 0.3rem;
    margin: 0.4rem 0 0.6rem;
  }

  .progress progress {
    width: 100%;
    height: 0.5rem;
    accent-color: var(--accent);
  }

  p {
    margin: 0 0 0.75rem;
  }

  .muted {
    color: var(--muted);
  }

  .error {
    color: var(--danger);
  }

  form {
    display: flex;
    flex-direction: column;
    gap: 0.75rem;
  }

  label {
    display: flex;
    flex-direction: column;
    gap: 0.25rem;
    font-weight: 600;
  }

  select,
  input {
    max-width: 18rem;
  }

  .actions {
    display: flex;
    gap: 0.5rem;
  }

  .actions button.primary {
    background: var(--accent);
    border-color: var(--accent);
    color: var(--accent-fg);
  }
</style>
