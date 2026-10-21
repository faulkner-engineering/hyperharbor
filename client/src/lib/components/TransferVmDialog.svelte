<script lang="ts">
  import { onMount } from "svelte";
  import {
    cancelTransfer,
    carryVmSettings,
    errorMessage,
    forgetTransferOffer,
    getTransferPreview,
    hasProblemCode,
    hostKnownToSupport,
    isClientError,
    offerTransfer,
    ProblemCodes,
    startTransfer,
    TransferApiVersion,
    type HostEntry,
    type TransferMode,
    type TransferOfferView,
    type TransferPreview,
    type ValidationIssue,
    type Vm,
    type VmJob,
  } from "$lib/api/client";
  import { ElevationCancelled, waitForJob, withElevation } from "$lib/lifecycle.svelte";
  import Dialog from "./Dialog.svelte";
  import JobProgress from "./JobProgress.svelte";

  interface Props {
    /** The host the VM is on now. */
    host: HostEntry;
    vm: Vm;
    /** Every known host; the paired ones other than `host` are the choices. */
    hosts: HostEntry[];
    onclose: (changed: boolean) => void;
  }

  let { host, vm, hosts, onclose }: Props = $props();

  let preview = $state<TransferPreview | null>(null);
  let loadError = $state<string | null>(null);
  let targetKey = $state<string | null>(null);
  let mode = $state<TransferMode>("move");
  let confirmName = $state("");
  let warnings = $state<ValidationIssue[] | null>(null);
  let offer = $state<TransferOfferView | null>(null);
  let busy = $state(false);
  let cancelling = $state(false);
  let error = $state<string | null>(null);
  let settingsNote = $state<string | null>(null);
  let job = $state<VmJob | null>(null);

  /** Paired hosts other than this one, with whether they can take part. */
  const targets = $derived(
    hosts
      .filter((other) => other.paired && other.key !== host.key && (other.hostId === null || other.hostId !== host.hostId))
      .map((other) => ({ host: other, ready: hostKnownToSupport(other, TransferApiVersion) })),
  );
  const target = $derived(targets.find((choice) => choice.host.key === targetKey && choice.ready)?.host ?? null);
  const finished = $derived(job !== null && job.state !== "running");
  const result = $derived(job?.transferResult ?? null);
  const blocked = $derived((preview?.blockers.length ?? 0) > 0);
  const canSubmit = $derived(
    preview !== null &&
      !blocked &&
      target !== null &&
      (mode === "copy" || confirmName === vm.name) &&
      !busy &&
      job === null,
  );

  onMount(() => {
    targetKey = targets.find((choice) => choice.ready)?.host.key ?? null;
    getTransferPreview(host.key, vm.id).then(
      (value) => (preview = value),
      (e) => (loadError = errorMessage(e)),
    );
  });

  /** A different host or mode needs a new offer; the one in hand is dropped (the other host ends it on its own). */
  function changed() {
    if (offer) void forgetTransferOffer(offer.transferId);
    offer = null;
    warnings = null;
    error = null;
  }

  function gigabytes(bytes: number): string {
    return `${Math.max(0.1, Math.round((bytes / 1024 ** 3) * 10) / 10)} GB`;
  }

  async function submit(event: SubmitEvent) {
    event.preventDefault();
    if (!canSubmit || preview === null || target === null) return;
    const checked = preview;
    const destination = target;
    busy = true;
    error = null;
    try {
      if (offer === null) {
        try {
          offer = await withElevation(destination.key, () =>
            offerTransfer(host.key, destination.key, checked, mode, warnings !== null),
          );
        } catch (e) {
          if (hasProblemCode(e, ProblemCodes.resourceWarnings) && isClientError(e)) {
            warnings = e.issues;
            return;
          }
          throw e;
        }
      }

      const accepted = offer;
      const started = await withElevation(host.key, () =>
        startTransfer(vm.id, accepted.transferId, mode === "move" ? confirmName : null),
      );
      offer = null;
      const ended = await waitForJob(host.key, started, (update) => (job = update));
      if (ended.state === "succeeded" && ended.transferResult) {
        try {
          await carryVmSettings(host.key, destination.key, vm.id, ended.transferResult.targetVmId, mode === "move");
        } catch (e) {
          settingsNote = `This device's monitor and camera settings for ${vm.name} were not carried over: ${errorMessage(e)}`;
        }
      }
    } catch (e) {
      if (!(e instanceof ElevationCancelled)) error = errorMessage(e);
    } finally {
      busy = false;
    }
  }

  async function cancel() {
    cancelling = true;
    try {
      await cancelTransfer(host.key, vm.id);
    } catch (e) {
      error = errorMessage(e);
    } finally {
      cancelling = false;
    }
  }

  function close() {
    if (offer) void forgetTransferOffer(offer.transferId);
    onclose(job?.state === "succeeded");
  }
</script>

<Dialog title="Move or copy {vm.name}">
  <form onsubmit={submit}>
    {#if loadError}
      <p class="error" role="alert">{loadError}</p>
    {:else if preview === null}
      <p class="muted">Checking {vm.name}…</p>
    {:else}
      <p class="muted">
        Sends {vm.name} ({gigabytes(preview.totalBytes)} of disks{preview.tpmEnabled ? ", with its TPM" : ""}) from
        {host.displayName} to another host. The hosts copy it between themselves; keep both awake until it finishes.
      </p>

      {#each preview.blockers as blocker (blocker.message)}
        <p class="warning" role="alert">{blocker.message}</p>
      {/each}

      {#if targets.length === 0}
        <p class="warning">Pair this device with the other host first.</p>
      {:else}
        <label for="transfer-target">To</label>
        <select id="transfer-target" bind:value={targetKey} onchange={changed} disabled={busy || job !== null}>
          {#each targets as choice (choice.host.key)}
            <option value={choice.host.key} disabled={!choice.ready}>
              {choice.host.displayName}{choice.ready ? "" : choice.host.apiVersion === null ? " (connect to it first)" : " (update it first)"}
            </option>
          {/each}
        </select>
      {/if}

      <fieldset disabled={busy || job !== null}>
        <legend>What happens to {vm.name} here</legend>
        <label class="check">
          <input type="radio" name="transfer-mode" value="move" bind:group={mode} onchange={changed} />
          Move: it keeps its identity and network addresses, and is deleted here once the other host has it
        </label>
        <label class="check">
          <input type="radio" name="transfer-mode" value="copy" bind:group={mode} onchange={changed} />
          Copy: it stays here; the copy gets a new identity and new network addresses
        </label>
      </fieldset>

      {#each preview.warnings as warning (warning)}
        <p class="muted">{warning}</p>
      {/each}

      {#if mode === "move"}
        <label for="transfer-confirm">Type <strong>{vm.name}</strong> to confirm the move</label>
        <input id="transfer-confirm" bind:value={confirmName} autocomplete="off" spellcheck="false" disabled={busy || job !== null} />
      {/if}

      {#if warnings}
        <div class="warning" role="alert">
          <p>{target?.displayName ?? "The other host"} says:</p>
          <ul>
            {#each warnings as warning (warning.message)}<li>{warning.message}</li>{/each}
          </ul>
        </div>
      {/if}
    {/if}

    {#if job}<JobProgress {job} />{/if}
    {#if result}
      <p class="muted">
        {mode === "move" ? "Moved" : "Copied"} to {target?.displayName} as {result.targetName}.
        {#if mode === "move" && !result.sourceRemoved}It is still on {host.displayName} too; delete it there and do not start both.{/if}
      </p>
      {#each result.warnings as warning (warning)}<p class="warning">{warning}</p>{/each}
    {/if}
    {#if settingsNote}<p class="muted">{settingsNote}</p>{/if}
    {#if error}<p class="error" role="alert">{error}</p>{/if}

    <div class="actions">
      {#if finished}
        <button type="button" class="primary" onclick={close}>Close</button>
      {:else if job}
        <button type="button" onclick={cancel} disabled={cancelling}>{cancelling ? "Cancelling…" : "Cancel transfer"}</button>
      {:else}
        <button type="button" onclick={close} disabled={busy}>Cancel</button>
        <button type="submit" class="primary" disabled={!canSubmit}>
          {busy ? "Checking…" : warnings ? "Continue anyway" : mode === "move" ? "Move" : "Copy"}
        </button>
      {/if}
    </div>
  </form>
</Dialog>

<style>
  fieldset {
    border: none;
    margin: 0.5rem 0;
    padding: 0;
  }

  legend {
    padding: 0;
    margin-bottom: 0.25rem;
  }

  select {
    width: 100%;
  }

  .warning ul {
    margin: 0.25rem 0 0 1.2rem;
    padding: 0;
  }
</style>
