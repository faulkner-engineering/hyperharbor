<script lang="ts">
  import {
    attachBackupTarget,
    createBackupTarget,
    errorMessage,
    testBackupTarget,
    type BackupTarget,
    type BackupTargetTest,
    type HostEntry,
  } from "$lib/api/client";
  import { emptyTargetForm, formProblem, KIND_LABELS, toNewTarget, type TargetForm } from "$lib/backup";
  import { ElevationCancelled, withElevation } from "$lib/lifecycle.svelte";
  import Dialog from "./Dialog.svelte";

  interface Props {
    host: HostEntry;
    /** Called with the new target (its recovery key may still be pending), or null when cancelled. */
    onclose: (created: BackupTarget | null) => void;
  }

  let { host, onclose }: Props = $props();

  let form = $state<TargetForm>(emptyTargetForm());
  let existing = $state(false);
  let recoveryKey = $state("");
  let busy = $state(false);
  let error = $state<string | null>(null);
  let created = $state<BackupTarget | null>(null);
  let test = $state<BackupTargetTest | null>(null);

  const problem = $derived(formProblem(form) ?? (existing && recoveryKey.trim() === "" ? "Enter the recovery key." : null));

  async function submit(event: SubmitEvent) {
    event.preventDefault();
    if (problem || busy) return;
    busy = true;
    error = null;
    try {
      const request = toNewTarget(form);
      if (existing) {
        const attached = await withElevation(host.key, () => attachBackupTarget(host.key, request, recoveryKey));
        recoveryKey = "";
        onclose(attached);
        return;
      }

      created = await withElevation(host.key, () => createBackupTarget(host.key, request));
      test = await testBackupTarget(host.key, created.id);
    } catch (e) {
      if (!(e instanceof ElevationCancelled)) error = errorMessage(e);
    } finally {
      busy = false;
    }
  }
</script>

<Dialog title={created ? `Added ${created.name}` : "Add a backup target"} wide>
  {#if created && test}
    <p class={test.passed ? "" : "warning"} role="status">
      {test.passed ? "The connection test passed." : "The connection test found a problem."}
    </p>
    <ul class="steps">
      {#each test.steps as step (step.name)}
        <li class:fail={!step.passed}><strong>{step.name}:</strong> {step.detail}</li>
      {/each}
    </ul>
    <p class="muted">
      Next, HyperHarbor creates a recovery key for this target. Nothing is backed up until you save and confirm it.
    </p>
    <div class="actions">
      <button type="button" onclick={() => onclose(created)}>
        {test.passed ? "Continue" : "Continue anyway"}
      </button>
    </div>
  {:else}
    <form onsubmit={submit}>
      <label class="check">
        <input type="checkbox" bind:checked={existing} disabled={busy} />
        This location already holds HyperHarbor backups (a new PC, or a target I removed)
      </label>

      <label for="bt-name">Name</label>
      <input id="bt-name" type="text" bind:value={form.name} disabled={busy} placeholder="Backup drive" />

      <label for="bt-kind">Where</label>
      <select id="bt-kind" bind:value={form.kind} disabled={busy}>
        {#each Object.entries(KIND_LABELS) as [kind, label] (kind)}
          <option value={kind}>{label}</option>
        {/each}
      </select>

      {#if form.kind === "local"}
        <label for="bt-path">Folder on the host</label>
        <input id="bt-path" type="text" bind:value={form.path} disabled={busy} placeholder="D:\HyperHarbor Backups" />
        <p class="muted">
          A folder that syncs to cloud storage (Proton Drive, OneDrive) or a network share works. Only changed data is
          uploaded after the first backup.
        </p>
      {:else if form.kind === "s3"}
        <label for="bt-endpoint">Endpoint</label>
        <input id="bt-endpoint" type="text" bind:value={form.endpoint} disabled={busy} placeholder="s3.eu-central-1.amazonaws.com" />
        <label for="bt-bucket">Bucket</label>
        <input id="bt-bucket" type="text" bind:value={form.bucket} disabled={busy} />
        <label for="bt-region">Region (optional)</label>
        <input id="bt-region" type="text" bind:value={form.region} disabled={busy} />
        <label for="bt-prefix">Folder inside the bucket (optional)</label>
        <input id="bt-prefix" type="text" bind:value={form.prefix} disabled={busy} />
        <label for="bt-access">Access key</label>
        <input id="bt-access" type="text" bind:value={form.accessKey} disabled={busy} autocomplete="off" />
        <label for="bt-secret">Secret key</label>
        <input id="bt-secret" type="password" bind:value={form.secretKey} disabled={busy} autocomplete="off" />
        <label class="check"><input type="checkbox" bind:checked={form.useTls} disabled={busy} /> Use TLS (https)</label>
      {:else}
        <label for="bt-account">Storage account</label>
        <input id="bt-account" type="text" bind:value={form.azureAccount} disabled={busy} />
        <label for="bt-container">Container</label>
        <input id="bt-container" type="text" bind:value={form.azureContainer} disabled={busy} />
        <label for="bt-prefix">Folder inside the container (optional)</label>
        <input id="bt-prefix" type="text" bind:value={form.prefix} disabled={busy} />
        <label for="bt-azure-key">Storage account key</label>
        <input id="bt-azure-key" type="password" bind:value={form.azureKey} disabled={busy} autocomplete="off" />
      {/if}
      {#if form.kind !== "local"}
        <p class="muted">The keys are stored on the host, encrypted for that PC, and are never shown again.</p>
      {/if}

      {#if existing}
        <label for="bt-recovery">Recovery key</label>
        <input id="bt-recovery" type="text" bind:value={recoveryKey} disabled={busy} autocomplete="off" spellcheck="false" />
      {:else}
        <label for="bt-time">Back up every day at</label>
        <input id="bt-time" type="time" bind:value={form.time} disabled={busy} />
        <div class="retention">
          <label>Keep daily <input type="number" min="0" bind:value={form.daily} disabled={busy} /></label>
          <label>weekly <input type="number" min="0" bind:value={form.weekly} disabled={busy} /></label>
          <label>monthly <input type="number" min="0" bind:value={form.monthly} disabled={busy} /></label>
        </div>
        <label for="bt-limit">Upload limit in kilobits per second (optional)</label>
        <input id="bt-limit" type="number" min="1" step="any" bind:value={form.limitKbps} disabled={busy} />
      {/if}

      {#if error}<p class="error" role="alert">{error}</p>{/if}
      <div class="actions">
        <button type="button" onclick={() => onclose(null)} disabled={busy}>Cancel</button>
        <button type="submit" class="primary" disabled={problem !== null || busy} title={problem ?? ""}>
          {busy ? "Working…" : existing ? "Use these backups" : "Add and test"}
        </button>
      </div>
    </form>
  {/if}
</Dialog>

<style>
  .steps {
    margin: 0 0 0.75rem 1.2rem;
    padding: 0;
    font-size: 0.9rem;
  }

  .fail {
    color: var(--danger, #b3261e);
  }

  .retention {
    display: flex;
    flex-wrap: wrap;
    gap: 0.75rem;
    margin: 0.5rem 0;
  }

  .retention input {
    width: 4.5rem;
  }
</style>
