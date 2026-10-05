<script lang="ts">
  import { onMount } from "svelte";
  import {
    checkHostUpdate,
    errorMessage,
    getHostUpdate,
    setHostUpdateSettings,
    type HostEntry,
    type HostUpdateMode,
    type HostUpdateStatus,
  } from "$lib/api/client";
  import { ElevationCancelled, withElevation } from "$lib/lifecycle.svelte";

  interface Props {
    host: HostEntry;
  }

  let { host }: Props = $props();

  const POLL_MS = 5000;

  let status = $state<HostUpdateStatus | null>(null);
  let loadError = $state<string | null>(null);
  let checking = $state(false);
  let editing = $state(false);
  let saving = $state(false);
  let saveError = $state<string | null>(null);
  let channel = $state("stable");
  let mode = $state<HostUpdateMode>("auto");
  let maintenanceTime = $state("");

  // Checking, downloading, and installing move on by themselves, so watch them until they settle.
  const working = $derived(
    status !== null && (status.activity === "checking" || status.activity === "preparing" || status.activity === "installing"),
  );

  const summary = $derived.by(() => {
    if (!status) return "";
    if (!status.supported) return status.message ?? "This host does not update itself.";
    switch (status.activity) {
      case "checking":
        return "Checking for updates…";
      case "preparing":
        return `Downloading and testing version ${status.availableVersion}…`;
      case "ready":
        return status.mode === "auto"
          ? `Version ${status.availableVersion} is ready. It installs when nothing is in progress on the host, or at the maintenance time.`
          : `Version ${status.availableVersion} is ready. Install it from the HyperHarbor Host window on the host.`;
      case "installing":
        return `Installing version ${status.availableVersion}. The host restarts and is back in a minute or two.`;
      default:
        return status.message ?? (status.lastCheck ? "Up to date." : "Not checked yet.");
    }
  });

  async function load() {
    try {
      status = await getHostUpdate(host.key);
      loadError = null;
    } catch (error) {
      // While an update installs the host restarts, so a failed poll is expected for a while.
      loadError = status?.activity === "installing" ? null : errorMessage(error);
    }
  }

  onMount(() => {
    void load();
  });

  $effect(() => {
    if (!working) return;
    const timer = setInterval(() => void load(), POLL_MS);
    return () => clearInterval(timer);
  });

  async function checkNow() {
    checking = true;
    try {
      status = await checkHostUpdate(host.key);
      loadError = null;
    } catch (error) {
      loadError = errorMessage(error);
    } finally {
      checking = false;
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
          <button type="button" onclick={checkNow} disabled={checking || working}>
            {checking ? "Checking…" : "Check now"}
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
</style>
