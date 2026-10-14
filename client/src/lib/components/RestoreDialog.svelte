<script lang="ts">
  import {
    BackupBrowseApiVersion,
    errorMessage,
    hostSupports,
    restoreHostState,
    restoreVmFromBackup,
    scanBackups,
    type BackupCatalog,
    type BackupTarget,
    type HostEntry,
    type RestoreVmRequest,
    type VmJob,
  } from "$lib/api/client";
  import { snapshotLabel, targetReady } from "$lib/backup";
  import { ElevationCancelled, waitForJob, withElevation } from "$lib/lifecycle.svelte";
  import BackupBrowseDialog from "./BackupBrowseDialog.svelte";
  import Dialog from "./Dialog.svelte";
  import JobProgress from "./JobProgress.svelte";

  interface Props {
    host: HostEntry;
    targets: BackupTarget[];
    onclose: () => void;
  }

  let { host, targets, onclose }: Props = $props();

  const usable = $derived(targets.filter(targetReady));

  let pickedTarget = $state("");
  const targetId = $derived(usable.some((t) => t.id === pickedTarget) ? pickedTarget : (usable[0]?.id ?? ""));
  let catalog = $state<BackupCatalog | null>(null);
  let vmId = $state("");
  let pickedSnapshot = $state("");
  let name = $state("");
  let start = $state(false);
  let adminUser = $state("");
  let adminPassword = $state("");
  let hostSnapshotId = $state("");
  let busy = $state(false);
  let error = $state<string | null>(null);
  let job = $state<VmJob | null>(null);
  let browsing = $state(false);

  const chosenVm = $derived(catalog?.vms.find((vm) => vm.vmId === vmId) ?? null);
  const finished = $derived(job !== null && job.state !== "running");
  const result = $derived(job?.state === "succeeded" ? (job.restoreResult ?? null) : null);

  // A different VM starts at its newest snapshot.
  const snapshotId = $derived(
    chosenVm?.snapshots.some((s) => s.id === pickedSnapshot) ? pickedSnapshot : (chosenVm?.snapshots[0]?.id ?? ""),
  );

  async function scan() {
    if (!targetId || busy) return;
    busy = true;
    error = null;
    try {
      catalog = await withElevation(host.key, () => scanBackups(host.key, targetId));
      vmId = catalog.vms[0]?.vmId ?? "";
      hostSnapshotId = catalog.hostSnapshots[0]?.id ?? "";
    } catch (e) {
      if (!(e instanceof ElevationCancelled)) error = errorMessage(e);
    } finally {
      busy = false;
    }
  }

  async function run(begin: () => Promise<VmJob>) {
    busy = true;
    error = null;
    try {
      const started = await withElevation(host.key, begin);
      await waitForJob(host.key, started, (update) => (job = update));
    } catch (e) {
      if (!(e instanceof ElevationCancelled)) error = errorMessage(e);
    } finally {
      busy = false;
      adminPassword = "";
    }
  }

  function restoreVm(event: SubmitEvent) {
    event.preventDefault();
    if (!chosenVm || !snapshotId || busy) return;
    const request: RestoreVmRequest = { targetId, snapshotId, start };
    if (name.trim() !== "") request.name = name.trim();
    if (start && adminUser.trim() !== "" && adminPassword !== "") {
      request.administrator = { adminUserName: adminUser.trim(), adminPassword, enableRemoteDesktop: true, installDesktop: false, trustNewHostKey: false };
    }
    void run(() => restoreVmFromBackup(host.key, request));
  }

  function restoreHost() {
    if (!hostSnapshotId || busy) return;
    void run(() => restoreHostState(host.key, targetId, hostSnapshotId));
  }
</script>

<Dialog title="Restore from a backup" wide>
  {#if usable.length === 0}
    <p class="muted">No backup target is ready. Add a target and save its recovery key first.</p>
    <div class="actions"><button type="button" onclick={onclose}>Close</button></div>
  {:else}
    <label for="rs-target">Backup target</label>
    <select id="rs-target" bind:value={pickedTarget} disabled={busy || catalog !== null}>
      {#each usable as target (target.id)}<option value={target.id}>{target.name}</option>{/each}
    </select>

    {#if catalog === null}
      <p class="muted">Looking opens the target and lists what it holds.</p>
      {#if error}<p class="error" role="alert">{error}</p>{/if}
      <div class="actions">
        <button type="button" onclick={onclose} disabled={busy}>Cancel</button>
        <button type="button" class="primary" onclick={scan} disabled={busy}>{busy ? "Looking…" : "Look for backups"}</button>
      </div>
    {:else if job === null}
      <h4>Virtual machines</h4>
      {#if catalog.vms.length === 0}
        <p class="muted">This target holds no virtual machine backups.</p>
      {:else}
        <form onsubmit={restoreVm}>
          <label for="rs-vm">Virtual machine</label>
          <select id="rs-vm" bind:value={vmId} disabled={busy}>
            {#each catalog.vms as vm (vm.vmId)}<option value={vm.vmId}>{vm.name ?? vm.vmId}</option>{/each}
          </select>
          <label for="rs-snap">Backup</label>
          <select id="rs-snap" bind:value={pickedSnapshot} disabled={busy}>
            {#each chosenVm?.snapshots ?? [] as snapshot (snapshot.id)}
              <option value={snapshot.id}>{snapshotLabel(snapshot)}</option>
            {/each}
          </select>
          <label for="rs-name">Name for the restored VM (optional)</label>
          <input id="rs-name" type="text" bind:value={name} disabled={busy} placeholder={chosenVm?.name ?? ""} />
          <p class="muted">
            It is restored as a new VM; one already named this way is left alone and the new one gets "(restored)" added.
            Network adapters use the switch of the same name, or the Default Switch.
          </p>
          <label class="check"><input type="checkbox" bind:checked={start} disabled={busy} /> Start it and wait for it to respond</label>
          {#if start}
            <p class="muted">
              To set up your HyperHarbor account in it again, give a local administrator of the VM. It is used once and not
              kept.
            </p>
            <label for="rs-admin">Administrator name (optional)</label>
            <input id="rs-admin" type="text" bind:value={adminUser} disabled={busy} autocomplete="off" />
            <label for="rs-pass">Administrator password</label>
            <input id="rs-pass" type="password" bind:value={adminPassword} disabled={busy} autocomplete="off" />
          {/if}
          <div class="actions">
            {#if hostSupports(host, BackupBrowseApiVersion)}
              <button type="button" onclick={() => (browsing = true)} disabled={busy || !snapshotId}>Browse files…</button>
            {/if}
            <button type="submit" class="primary" disabled={busy || !snapshotId}>{busy ? "Starting…" : "Restore this VM"}</button>
          </div>
        </form>
      {/if}

      <h4>Host settings</h4>
      {#if catalog.hostSnapshots.length === 0}
        <p class="muted">This target holds no host settings backup.</p>
      {:else}
        <label for="rs-host">Backup</label>
        <select id="rs-host" bind:value={hostSnapshotId} disabled={busy}>
          {#each catalog.hostSnapshots as snapshot (snapshot.id)}
            <option value={snapshot.id}>{snapshotLabel(snapshot)}</option>
          {/each}
        </select>
        <p class="muted">
          Restores paired devices, the host certificate, settings, and profiles. The host must restart afterwards. VM and
          console account passwords belong to the old PC and are set up again.
        </p>
        <div class="actions">
          <button type="button" onclick={restoreHost} disabled={busy || !hostSnapshotId}>Restore host settings</button>
        </div>
      {/if}
      {#if error}<p class="error" role="alert">{error}</p>{/if}
      <div class="actions"><button type="button" onclick={onclose} disabled={busy}>Close</button></div>
    {:else}
      <JobProgress {job} />
      {#if result}
        <div role="status">
          <p>
            {#if job.kind === "restoreHostState"}
              Host settings restored.{result.restartRequired ? " Restart the host to use them." : ""}
            {:else if result.outcome === "needsConsole"}
              {result.vmName} was restored and started, but it has not responded. Open its console to see what it waits for.
            {:else}
              {result.vmName} was restored{result.outcome === "succeeded" ? "" : ", with things to check"}.
            {/if}
          </p>
          {#if result.needsDriverResync}
            <p class="warning">It has a GPU partition. Run guest setup in Performance mode to copy the host's driver again.</p>
          {/if}
          {#if result.bitLockerRecoveryMayBeNeeded}
            <p class="warning">It has a new virtual TPM key, so BitLocker may ask for its recovery key at first start.</p>
          {/if}
          {#each result.warnings as warning, index (index)}<p class="warning">{warning}</p>{/each}
        </div>
      {/if}
      {#if error}<p class="error" role="alert">{error}</p>{/if}
      <div class="actions">
        {#if finished}<button type="button" class="primary" onclick={onclose}>Close</button>{/if}
      </div>
    {/if}
  {/if}
</Dialog>

{#if browsing && chosenVm}
  <BackupBrowseDialog {host} {targetId} {snapshotId} vmName={chosenVm.name ?? "the VM"} onclose={() => (browsing = false)} />
{/if}

<style>
  h4 {
    margin: 1rem 0 0.25rem;
    font-size: 0.95rem;
  }
</style>
