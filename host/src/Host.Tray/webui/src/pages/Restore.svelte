<script lang="ts">
  import Card from "../components/Card.svelte";
  import { tray } from "../store.svelte";
  import type { RestoreWizardForm, TrayViewState } from "../bridge";
  import { restoreStatus, snapshotLabel } from "../text";

  interface Props {
    view: TrayViewState;
  }

  let { view }: Props = $props();

  type Kind = RestoreWizardForm["kind"];

  let kind = $state<Kind>("local");
  let name = $state("");
  let recoveryKey = $state("");
  let path = $state("");
  let endpoint = $state("");
  let bucket = $state("");
  let region = $state("");
  let prefix = $state("");
  let useTls = $state(true);
  let accessKey = $state("");
  let secretKey = $state("");
  let azureAccount = $state("");
  let azureContainer = $state("");
  let azureKey = $state("");

  let pickedVm = $state("");
  let pickedSnapshot = $state("");
  let pickedHost = $state("");
  let vmName = $state("");
  let startVm = $state(false);

  const wizard = $derived(view.restoreWizard);
  const status = $derived(restoreStatus(wizard));
  const working = $derived(wizard?.phase === "attaching" || wizard?.phase === "restoring");
  const busy = $derived(working || !view.connected);

  const vm = $derived(wizard?.vms.find((v) => v.vmId === pickedVm) ?? wizard?.vms[0] ?? null);
  const snapshotId = $derived(vm?.snapshots.some((s) => s.id === pickedSnapshot) ? pickedSnapshot : (vm?.snapshots[0]?.id ?? ""));
  const hostSnapshotId = $derived(wizard?.hostSnapshots.some((s) => s.id === pickedHost) ? pickedHost : (wizard?.hostSnapshots[0]?.id ?? ""));

  const formReady = $derived(
    recoveryKey.trim() !== "" &&
      (kind === "local"
        ? path.trim() !== ""
        : kind === "s3"
          ? endpoint.trim() !== "" && bucket.trim() !== "" && accessKey !== "" && secretKey !== ""
          : azureAccount.trim() !== "" && azureContainer.trim() !== "" && azureKey !== ""),
  );

  function attach(event: SubmitEvent) {
    event.preventDefault();
    if (!formReady || busy) return;
    const form: RestoreWizardForm = { kind, name: name.trim() || "Restored backups", recoveryKey: recoveryKey.trim() };
    if (kind === "local") form.path = path.trim();
    if (kind === "s3") {
      Object.assign(form, { endpoint: endpoint.trim(), bucket: bucket.trim(), region: region.trim() || undefined, prefix: prefix.trim() || undefined, useTls, accessKey, secretKey });
    }
    if (kind === "azure") {
      Object.assign(form, { azureAccount: azureAccount.trim(), azureContainer: azureContainer.trim(), prefix: prefix.trim() || undefined, azureKey });
    }
    tray.send({ type: "restoreWizardAttach", form });
    // The keys were sent; do not keep them in the page.
    recoveryKey = "";
    accessKey = "";
    secretKey = "";
    azureKey = "";
  }

  function restoreVm(event: SubmitEvent) {
    event.preventDefault();
    if (!snapshotId || busy) return;
    tray.send({ type: "restoreWizardRestore", kind: "vm", snapshotId, name: vmName.trim() || undefined, start: startVm });
  }

  function restoreHost() {
    if (!hostSnapshotId || busy) return;
    tray.send({ type: "restoreWizardRestore", kind: "host", snapshotId: hostSnapshotId });
  }
</script>

<div class="stack">
  <Card icon="storage" title="Restore from a backup" status={status.status} tone={status.tone}>
    Use this on a new or reinstalled PC: point HyperHarbor at the place your backups are kept, enter the recovery key you saved
    when you set them up, and restore the host's settings (paired devices, certificate, profiles) and your VMs. Restored VMs
    are new VMs; passwords of the old PC (VM accounts, console accounts) are set up again with Provision VM.
    {#if wizard?.error}<span class="line error" role="alert">{wizard.error}</span>{/if}
    {#if working}<span class="line">{wizard?.step}{wizard && wizard.percent > 0 ? ` (${wizard.percent}%)` : ""}</span>{/if}
  </Card>

  {#if wizard && (wizard.phase === "idle" || wizard.phase === "attaching")}
    <form class="block" onsubmit={attach}>
      <label for="rw-kind">Where are the backups?</label>
      <select id="rw-kind" bind:value={kind} disabled={busy}>
        <option value="local">A folder</option>
        <option value="s3">S3-compatible storage</option>
        <option value="azure">Azure Blob Storage</option>
      </select>

      <label for="rw-name">Name for this location (optional)</label>
      <input id="rw-name" type="text" bind:value={name} disabled={busy} placeholder="Restored backups" />

      {#if kind === "local"}
        <label for="rw-path">Folder</label>
        <input id="rw-path" type="text" bind:value={path} disabled={busy} placeholder="D:\Backups" autocomplete="off" />
      {:else if kind === "s3"}
        <label for="rw-endpoint">Endpoint</label>
        <input id="rw-endpoint" type="text" bind:value={endpoint} disabled={busy} placeholder="s3.example.com" autocomplete="off" />
        <label for="rw-bucket">Bucket</label>
        <input id="rw-bucket" type="text" bind:value={bucket} disabled={busy} autocomplete="off" />
        <label for="rw-region">Region (optional)</label>
        <input id="rw-region" type="text" bind:value={region} disabled={busy} autocomplete="off" />
        <label for="rw-prefix">Folder inside the bucket (optional)</label>
        <input id="rw-prefix" type="text" bind:value={prefix} disabled={busy} autocomplete="off" />
        <label class="check"><input type="checkbox" bind:checked={useTls} disabled={busy} /> Use TLS</label>
        <label for="rw-access">Access key</label>
        <input id="rw-access" type="password" bind:value={accessKey} disabled={busy} autocomplete="off" />
        <label for="rw-secret">Secret key</label>
        <input id="rw-secret" type="password" bind:value={secretKey} disabled={busy} autocomplete="off" />
      {:else}
        <label for="rw-account">Storage account</label>
        <input id="rw-account" type="text" bind:value={azureAccount} disabled={busy} autocomplete="off" />
        <label for="rw-container">Container</label>
        <input id="rw-container" type="text" bind:value={azureContainer} disabled={busy} autocomplete="off" />
        <label for="rw-aprefix">Folder inside the container (optional)</label>
        <input id="rw-aprefix" type="text" bind:value={prefix} disabled={busy} autocomplete="off" />
        <label for="rw-akey">Account key</label>
        <input id="rw-akey" type="password" bind:value={azureKey} disabled={busy} autocomplete="off" />
      {/if}

      <label for="rw-key">Recovery key</label>
      <input id="rw-key" type="password" bind:value={recoveryKey} disabled={busy} autocomplete="off" spellcheck="false" placeholder="XXXX-XXXX-XXXX-…" />
      <p class="muted">The key is checked against the backups, kept in this PC's protected store, and never shown again.</p>
      <div class="actions">
        <button type="submit" class="btn primary" disabled={busy || !formReady}>{wizard.phase === "attaching" ? "Looking…" : "Look for backups"}</button>
      </div>
    </form>
  {:else if wizard}
    {#if wizard.messages.length > 0}
      <section class="block" role="status">
        {#each wizard.messages as message, index (index)}<p>{message}</p>{/each}
        {#if wizard.restartRequired}
          <p class="warn">Restart the HyperHarbor host service (or this PC) so the restored settings take effect.</p>
        {/if}
      </section>
    {/if}

    <section class="block">
      <h2>Virtual machines in {wizard.targetName}</h2>
      {#if wizard.vms.length === 0}
        <p class="muted">This location holds no virtual machine backups.</p>
      {:else}
        <form onsubmit={restoreVm}>
          <label for="rw-vm">Virtual machine</label>
          <select id="rw-vm" bind:value={pickedVm} disabled={busy}>
            {#each wizard.vms as v (v.vmId)}<option value={v.vmId}>{v.name ?? v.vmId}</option>{/each}
          </select>
          <label for="rw-snap">Backup</label>
          <select id="rw-snap" bind:value={pickedSnapshot} disabled={busy}>
            {#each vm?.snapshots ?? [] as s (s.id)}<option value={s.id}>{snapshotLabel(s)}</option>{/each}
          </select>
          <label for="rw-vmname">Name for the restored VM (optional)</label>
          <input id="rw-vmname" type="text" bind:value={vmName} disabled={busy} placeholder={vm?.name ?? ""} />
          <label class="check"><input type="checkbox" bind:checked={startVm} disabled={busy} /> Start it and wait for it to respond</label>
          <div class="actions">
            <button type="submit" class="btn" disabled={busy || !snapshotId}>Restore this VM</button>
          </div>
        </form>
      {/if}
    </section>

    <section class="block">
      <h2>Host settings</h2>
      {#if wizard.hostSnapshots.length === 0}
        <p class="muted">This location holds no host settings backup.</p>
      {:else}
        <label for="rw-host">Backup</label>
        <select id="rw-host" bind:value={pickedHost} disabled={busy}>
          {#each wizard.hostSnapshots as s (s.id)}<option value={s.id}>{snapshotLabel(s)}</option>{/each}
        </select>
        <p class="muted">Restores paired devices, the host certificate, settings, and profiles. The host must restart afterwards.</p>
        <div class="actions">
          <button type="button" class="btn" disabled={busy || !hostSnapshotId} onclick={restoreHost}>Restore host settings</button>
        </div>
      {/if}
    </section>

    <div class="actions">
      <button type="button" class="btn" disabled={busy} onclick={() => tray.send({ type: "restoreWizardReset" })}>Use another location</button>
    </div>
  {:else}
    <p class="muted">{view.connected ? "Reading…" : "Unknown while the service is not running."}</p>
  {/if}
</div>

<style>
  .stack {
    display: grid;
    gap: 0.8rem;
  }

  .block {
    display: grid;
    gap: 0.4rem;
  }

  h2 {
    margin: 0.4rem 0 0;
    font-size: 1rem;
  }

  .line {
    display: block;
    margin-top: 0.3rem;
  }

  .error {
    color: var(--danger, #c0392b);
  }

  .warn {
    color: var(--warn, #b9770e);
  }

  .check {
    display: flex;
    align-items: center;
    gap: 0.4rem;
  }
</style>
