<script lang="ts">
  import { onMount } from "svelte";
  import {
    checkClientUpdate,
    errorMessage,
    getClientUpdate,
    getClientUpdateSettings,
    installClientUpdate,
    isClientError,
    onClientUpdateChanged,
    setClientUpdateSettings,
    type ClientUpdateChannel,
    type ClientUpdateSettings,
    type ClientUpdateStatus,
  } from "$lib/api/client";
  import ConfirmDialog from "./ConfirmDialog.svelte";

  let status = $state<ClientUpdateStatus | null>(null);
  let settings = $state<ClientUpdateSettings | null>(null);
  let checking = $state(false);
  let error = $state<string | null>(null);
  let showNotes = $state(false);
  let editing = $state(false);
  let draftChannel = $state<ClientUpdateChannel>("stable");
  let draftAutomatic = $state(true);
  /** The number of open sessions, while the user decides whether to install anyway. */
  let confirmingSessions = $state<number | null>(null);
  let copied = $state(false);

  const phase = $derived(status?.phase ?? { state: "idle" as const });
  const busy = $derived(checking || phase.state === "checking" || phase.state === "downloading");

  onMount(() => {
    getClientUpdate()
      .then((current) => (status = current))
      .catch((e) => (error = errorMessage(e)));
    getClientUpdateSettings()
      .then((current) => (settings = current))
      .catch((e) => (error = errorMessage(e)));
    const unlisten = onClientUpdateChanged((current) => (status = current));
    return () => {
      unlisten.then((stop) => stop());
    };
  });

  async function checkNow() {
    checking = true;
    error = null;
    try {
      status = await checkClientUpdate();
    } catch (e) {
      error = errorMessage(e);
    } finally {
      checking = false;
    }
  }

  async function install(confirmed: boolean) {
    error = null;
    try {
      await installClientUpdate(confirmed);
    } catch (e) {
      if (isClientError(e) && e.code === "sessionsActive") {
        confirmingSessions = Number(/^\d+/.exec(e.message)?.[0] ?? 1);
      } else {
        error = errorMessage(e);
      }
    }
  }

  function startEditing() {
    if (!settings) return;
    draftChannel = settings.channel;
    draftAutomatic = settings.checkAutomatically;
    editing = true;
  }

  async function save(event: SubmitEvent) {
    event.preventDefault();
    error = null;
    try {
      const next = { channel: draftChannel, checkAutomatically: draftAutomatic };
      status = await setClientUpdateSettings(next);
      settings = next;
      editing = false;
    } catch (e) {
      error = errorMessage(e);
    }
  }

  async function copyLink() {
    if (!status) return;
    try {
      await navigator.clipboard.writeText(status.releasesUrl);
      copied = true;
    } catch {
      copied = false;
    }
  }

  function percent(downloaded: number, total: number | null) {
    return total ? Math.min(100, Math.round((downloaded / total) * 100)) : null;
  }
</script>

<section aria-label="HyperHarbor updates">
  {#if status}
    <p class="version">Version {status.currentVersion}</p>

    {#if phase.state === "available"}
      <p role="status"><strong>Version {phase.version} is available.</strong></p>
      {#if phase.notes}
        <button type="button" class="link" onclick={() => (showNotes = !showNotes)} aria-expanded={showNotes}>
          {showNotes ? "Hide what's new" : "What's new"}
        </button>
        {#if showNotes}<pre class="notes">{phase.notes}</pre>{/if}
      {/if}
      {#if status.installKind === "installed"}
        <button type="button" class="primary" onclick={() => install(false)}>Install and restart</button>
      {:else}
        <p class="muted">This is a portable copy and cannot update itself. Download the new version:</p>
        <p class="muted url">{status.releasesUrl}</p>
        <button type="button" onclick={copyLink}>{copied ? "Link copied" : "Copy link"}</button>
      {/if}
    {:else if phase.state === "downloading"}
      {@const done = percent(phase.downloaded, phase.total)}
      <p role="status">Downloading the update…</p>
      {#if done === null}
        <progress aria-label="Downloading the update"></progress>
      {:else}
        <progress aria-label="Downloading the update" max="100" value={done}></progress>
      {/if}
    {:else if phase.state === "upToDate"}
      <p class="muted" role="status">You have the latest version.</p>
    {:else if phase.state === "failed"}
      <p class="error" role="alert">{phase.message}</p>
    {/if}

    {#if error}<p class="error" role="alert">{error}</p>{/if}

    {#if editing}
      <form onsubmit={save}>
        <label>
          Channel
          <select bind:value={draftChannel}>
            <option value="stable">Stable</option>
            <option value="beta">Beta (prereleases)</option>
          </select>
        </label>
        <label class="inline">
          <input type="checkbox" bind:checked={draftAutomatic} />
          Check for updates automatically
        </label>
        <div class="actions">
          <button type="submit" class="primary">Save</button>
          <button type="button" onclick={() => (editing = false)}>Cancel</button>
        </div>
      </form>
    {:else}
      <div class="actions">
        <button type="button" onclick={checkNow} disabled={busy} aria-busy={busy}>
          {busy && phase.state !== "downloading" ? "Checking…" : "Check for updates"}
        </button>
        <button type="button" class="link" onclick={startEditing} disabled={settings === null || busy}>Settings</button>
      </div>
    {/if}
  {/if}
</section>

{#if confirmingSessions !== null}
  <ConfirmDialog
    title="Install the update?"
    message="{confirmingSessions} Remote Desktop or console window{confirmingSessions === 1
      ? ' is'
      : 's are'} open. Installing the update closes HyperHarbor and ends {confirmingSessions === 1
      ? 'it'
      : 'them'}. Save your work in the virtual machines first."
    confirmLabel="Install and restart"
    onclose={(confirmed) => {
      confirmingSessions = null;
      if (confirmed) install(true);
    }}
  />
{/if}

<style>
  section {
    display: flex;
    flex-direction: column;
    gap: 0.5rem;
    margin-top: auto;
    padding-top: 1rem;
    border-top: 1px solid var(--border);
    font-size: 0.85rem;
  }

  p {
    margin: 0;
  }

  .version,
  .muted {
    color: var(--muted);
  }

  .error {
    color: var(--danger);
  }

  .url {
    word-break: break-all;
  }

  .notes {
    margin: 0;
    max-height: 10rem;
    overflow: auto;
    white-space: pre-wrap;
    font: inherit;
    padding: 0.5rem;
    border: 1px solid var(--border);
    border-radius: 6px;
  }

  progress {
    width: 100%;
    accent-color: var(--accent);
  }

  form {
    display: flex;
    flex-direction: column;
    gap: 0.5rem;
  }

  label {
    display: flex;
    flex-direction: column;
    gap: 0.25rem;
    font-weight: 600;
  }

  label.inline {
    flex-direction: row;
    align-items: center;
    font-weight: normal;
  }

  .actions {
    display: flex;
    flex-wrap: wrap;
    gap: 0.5rem;
  }

  button.primary {
    background: var(--accent);
    border-color: var(--accent);
    color: var(--accent-fg);
  }
</style>
