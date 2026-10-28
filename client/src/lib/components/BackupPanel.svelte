<script lang="ts">
  import { onDestroy, onMount } from "svelte";
  import {
    deleteBackupPolicy,
    deleteBackupTarget,
    errorMessage,
    getBackupStatus,
    listBackupHistory,
    listBackupPolicies,
    listBackupTargets,
    setBackupPolicy,
    startBackupRun,
    testBackupTarget,
    updateBackupTarget,
    type BackupPolicy,
    type BackupRun,
    type BackupStatus,
    type BackupTarget,
    hostSupports,
    RestoreTestApiVersion,
    type HostEntry,
    type Vm,
  } from "$lib/api/client";
  import {
    ageText,
    encryptionNote,
    itemName,
    runSummary,
    scheduleText,
    storedRunWarnings,
    targetLocation,
    targetReady,
    uploadText,
    KIND_LABELS,
  } from "$lib/backup";
  import { ElevationCancelled, waitForJob, withElevation } from "$lib/lifecycle.svelte";
  import BackupTargetDialog from "./BackupTargetDialog.svelte";
  import ConfirmDialog from "./ConfirmDialog.svelte";
  import RecoveryKeyDialog from "./RecoveryKeyDialog.svelte";
  import RestoreDialog from "./RestoreDialog.svelte";
  import RestoreTestSection from "./RestoreTestSection.svelte";

  interface Props {
    host: HostEntry;
    vms: Vm[];
    /** Reports the loaded targets, so the VM menu can offer replace-disk. */
    ontargets?: (targets: BackupTarget[]) => void;
  }

  let { host, vms, ontargets }: Props = $props();

  const POLL_MS = 5000;

  let targets = $state<BackupTarget[]>([]);
  let policies = $state<BackupPolicy[]>([]);
  let status = $state<BackupStatus | null>(null);
  let history = $state<BackupRun[]>([]);
  let loadError = $state<string | null>(null);
  let actionError = $state<string | null>(null);
  let notice = $state<string | null>(null);
  let busyKey = $state<string | null>(null);
  let adding = $state(false);
  let keying = $state<BackupTarget | null>(null);
  let removing = $state<BackupTarget | null>(null);
  let restoring = $state(false);
  let timer: ReturnType<typeof setInterval> | undefined;

  const ready = $derived(targets.filter(targetReady));

  async function load() {
    try {
      const [loadedTargets, loadedPolicies, loadedStatus, loadedHistory] = await Promise.all([
        listBackupTargets(host.key),
        listBackupPolicies(host.key),
        getBackupStatus(host.key),
        listBackupHistory(host.key, 20),
      ]);
      targets = loadedTargets;
      policies = loadedPolicies;
      status = loadedStatus;
      history = loadedHistory;
      loadError = null;
      ontargets?.(loadedTargets);
    } catch (e) {
      loadError = errorMessage(e);
    }
  }

  onMount(() => {
    void load();
    timer = setInterval(() => void load(), POLL_MS);
  });
  onDestroy(() => clearInterval(timer));

  /** Runs one action with a busy marker; elevation prompts are handled, cancelling is silent. */
  async function act(key: string, action: () => Promise<void>) {
    busyKey = key;
    actionError = null;
    notice = null;
    try {
      await action();
      await load();
    } catch (e) {
      if (!(e instanceof ElevationCancelled)) actionError = errorMessage(e);
    } finally {
      busyKey = null;
    }
  }

  const runNow = (targetId: string, vmId?: string) =>
    act(`run:${targetId}:${vmId ?? "host"}`, async () => {
      const job = await startBackupRun(host.key, targetId, vmId);
      notice = "Backup started.";
      const done = await waitForJob(host.key, job, () => {});
      notice = done.state === "succeeded" ? "Backup finished." : null;
      if (done.state === "failed") actionError = done.error ? `${done.error.title}. ${done.error.detail}` : "The backup failed.";
    });

  const testTarget = (target: BackupTarget) =>
    act(`test:${target.id}`, async () => {
      const result = await testBackupTarget(host.key, target.id);
      notice = result.passed
        ? `${target.name}: the connection test passed.`
        : `${target.name}: ${result.steps.filter((s) => !s.passed).map((s) => s.detail).join(" ")}`;
    });

  const toggleTarget = (target: BackupTarget) =>
    act(`toggle:${target.id}`, async () => {
      await withElevation(host.key, () => updateBackupTarget(host.key, target.id, { enabled: !target.enabled, clearBandwidth: false }));
    });

  async function removeTarget(confirmed: boolean) {
    const target = removing;
    removing = null;
    if (!confirmed || !target) return;
    await act(`remove:${target.id}`, async () => {
      await withElevation(host.key, () => deleteBackupTarget(host.key, target.id));
    });
  }

  const policyFor = (vm: Vm, target: BackupTarget) =>
    policies.find((p) => p.vmId === vm.id && p.targetId === target.id) ?? null;

  const setPolicy = (vm: Vm, target: BackupTarget, enabled: boolean, onShutdown?: boolean) =>
    act(`policy:${target.id}:${vm.id}`, async () => {
      const current = policyFor(vm, target);
      if (!enabled) {
        await deleteBackupPolicy(host.key, target.id, vm.id);
        return;
      }
      await setBackupPolicy(host.key, {
        ...(current ?? { vmId: vm.id, targetId: target.id }),
        enabled: true,
        triggers: {
          schedule: true,
          onShutdown: onShutdown ?? current?.triggers?.onShutdown ?? false,
          debounceMinutes: current?.triggers?.debounceMinutes ?? 10,
        },
      });
    });

  async function keyClosed(confirmed: BackupTarget | null) {
    keying = null;
    if (confirmed) notice = `${confirmed.name} is ready. Backups start at the next scheduled time, or run one now.`;
    await load();
  }

  async function targetAdded(created: BackupTarget | null) {
    adding = false;
    if (!created) return;
    await load();
    if (!targetReady(created)) keying = created;
  }
</script>

<section class="panel" aria-label="Backups">
  <header>
    <h3>Backups</h3>
    <div class="buttons">
      <button type="button" onclick={() => (adding = true)}>Add a target</button>
      <button type="button" onclick={() => (restoring = true)} disabled={ready.length === 0}>Restore…</button>
    </div>
  </header>

  {#if loadError}<p class="error" role="alert">{loadError}</p>{/if}
  {#if actionError}<p class="error" role="alert">{actionError}</p>{/if}
  {#if notice}<p role="status">{notice}</p>{/if}

  {#if targets.length === 0 && !loadError}
    <p class="muted">
      Nothing is backed up yet. Add a target (a folder, S3-compatible storage, or Azure Blob), save its recovery key, and
      choose which VMs to back up.
    </p>
  {/if}

  {#each targets as target (target.id)}
    <div class="target" class:off={!target.enabled}>
      <div class="title">
        <strong>{target.name}</strong>
        <span class="muted">{KIND_LABELS[target.kind]} · {targetLocation(target)}</span>
      </div>
      <p class="muted">
        {scheduleText(target.schedule)} · keeps {target.retention.daily} daily, {target.retention.weekly} weekly,
        {target.retention.monthly} monthly{target.enabled ? "" : " · paused"}
      </p>
      {#if !targetReady(target)}
        <p class="warning">Its recovery key is not confirmed, so it does not back anything up.</p>
      {/if}
      <div class="buttons">
        {#if !targetReady(target)}
          <button type="button" class="primary" onclick={() => (keying = target)}>Set the recovery key</button>
        {:else}
          <button type="button" onclick={() => runNow(target.id)} disabled={busyKey !== null || !target.enabled}>
            Back up host settings now
          </button>
        {/if}
        <button type="button" onclick={() => testTarget(target)} disabled={busyKey !== null}>Test</button>
        <button type="button" onclick={() => toggleTarget(target)} disabled={busyKey !== null}>
          {target.enabled ? "Pause" : "Resume"}
        </button>
        <button type="button" class="danger" onclick={() => (removing = target)} disabled={busyKey !== null}>Remove…</button>
      </div>

      {#if targetReady(target) && vms.length > 0}
        <table class="vms">
          <thead>
            <tr><th>Virtual machine</th><th>Back up</th><th>After it shuts down</th><th></th></tr>
          </thead>
          <tbody>
            {#each vms as vm (vm.id)}
              {@const policy = policyFor(vm, target)}
              <tr>
                <td>{vm.name}</td>
                <td>
                  <input
                    type="checkbox"
                    aria-label="Back up {vm.name} to {target.name}"
                    checked={policy !== null}
                    disabled={busyKey !== null}
                    onchange={(event) => setPolicy(vm, target, event.currentTarget.checked)}
                  />
                </td>
                <td>
                  <input
                    type="checkbox"
                    aria-label="Back up {vm.name} to {target.name} after it shuts down"
                    checked={policy?.triggers?.onShutdown ?? false}
                    disabled={busyKey !== null || policy === null}
                    onchange={(event) => setPolicy(vm, target, true, event.currentTarget.checked)}
                  />
                </td>
                <td>
                  <button type="button" onclick={() => runNow(target.id, vm.id)} disabled={busyKey !== null || policy === null}>
                    Back up now
                  </button>
                </td>
              </tr>
            {/each}
          </tbody>
        </table>
      {/if}
    </div>
  {/each}

  {#if status && status.items.length > 0}
    <h4>Latest backups{status.running ? " (one is running)" : ""}</h4>
    <ul class="items">
      {#each status.items as item (item.item + item.targetId)}
        <li class:overdue={item.overdue}>
          <strong>{itemName(item)}</strong> to {item.targetName}: {runSummary(item)}
          {#if item.overdue}<span class="warning"> No good backup for a while.</span>{/if}
          {#if encryptionNote(item)}<span class="muted"> {encryptionNote(item)}</span>{/if}
          {#each storedRunWarnings(item) as warning, index (index)}<p class="warning">{warning}</p>{/each}
        </li>
      {/each}
    </ul>
    {#if status.waiting > 0}
      <p class="muted">{status.waiting} waiting for a good moment (no one connected, no game running).</p>
    {/if}
  {/if}

  {#if hostSupports(host, RestoreTestApiVersion)}<RestoreTestSection {host} />{/if}

  {#if history.length > 0}
    <h4>History</h4>
    <ul class="history">
      {#each history as run (run.runId)}
        <li>
          {ageText(run.finishedAt)} · {run.item === "host" ? "Host settings" : (run.vmName ?? "VM")} · {run.outcome} · {run.trigger}
          {#if uploadText(run)}<span class="muted"> · {uploadText(run)}</span>{/if}
          {#if run.reason && run.outcome !== "succeeded"}<span class="muted"> · {run.reason}</span>{/if}
        </li>
      {/each}
    </ul>
  {/if}
</section>

{#if adding}<BackupTargetDialog {host} onclose={targetAdded} />{/if}
{#if keying}<RecoveryKeyDialog {host} target={keying} onclose={keyClosed} />{/if}
{#if restoring}<RestoreDialog {host} {targets} onclose={() => (restoring = false)} />{/if}
{#if removing}
  <ConfirmDialog
    title="Remove {removing.name}?"
    message="HyperHarbor stops backing up to it and forgets its keys. Backups already stored there stay, and you can reach them again by adding the target with its recovery key."
    confirmLabel="Remove target"
    danger
    onclose={removeTarget}
  />
{/if}

<style>
  .panel {
    margin: 1rem 0;
  }

  header {
    display: flex;
    justify-content: space-between;
    align-items: center;
    gap: 1rem;
  }

  h3,
  h4 {
    margin: 0.5rem 0;
  }

  .buttons {
    display: flex;
    flex-wrap: wrap;
    gap: 0.5rem;
    margin: 0.5rem 0;
  }

  .target {
    padding: 0.75rem;
    margin: 0.5rem 0;
    border: 1px solid var(--border);
    border-radius: 6px;
  }

  .target.off {
    opacity: 0.7;
  }

  .title {
    display: flex;
    flex-wrap: wrap;
    gap: 0.5rem;
    align-items: baseline;
  }

  .vms {
    width: 100%;
    border-collapse: collapse;
    font-size: 0.9rem;
  }

  .vms th,
  .vms td {
    text-align: left;
    padding: 0.25rem 0.5rem;
  }

  .items,
  .history {
    margin: 0 0 0.5rem 1.2rem;
    padding: 0;
    font-size: 0.9rem;
  }
</style>
