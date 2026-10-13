<script lang="ts">
  import { onDestroy, onMount } from "svelte";
  import {
    confirmRecoveryKey,
    createRecoveryKey,
    errorMessage,
    hasProblemCode,
    ProblemCodes,
    type BackupTarget,
    type HostEntry,
    type RecoveryKeyOffer,
  } from "$lib/api/client";
  import { confirmationComplete, groupKey } from "$lib/backup";
  import { ElevationCancelled, withElevation } from "$lib/lifecycle.svelte";
  import Dialog from "./Dialog.svelte";

  interface Props {
    host: HostEntry;
    target: BackupTarget;
    /** Called with the confirmed target, or null when the user leaves without confirming. */
    onclose: (confirmed: BackupTarget | null) => void;
  }

  let { host, target, onclose }: Props = $props();

  let offer = $state<RecoveryKeyOffer | null>(null);
  let typed = $state("");
  let saved = $state(false);
  let busy = $state(true);
  let error = $state<string | null>(null);
  let copied = $state(false);

  const ready = $derived(offer !== null && saved && confirmationComplete(typed) && !busy);

  onMount(start);

  // The key lives only in this dialog; drop it when the dialog goes away.
  onDestroy(() => {
    offer = null;
    typed = "";
  });

  async function start() {
    busy = true;
    error = null;
    try {
      offer = await withElevation(host.key, () => createRecoveryKey(host.key, target.id));
    } catch (e) {
      if (e instanceof ElevationCancelled) onclose(null);
      else error = errorMessage(e);
    } finally {
      busy = false;
    }
  }

  async function copy() {
    if (!offer) return;
    try {
      await navigator.clipboard.writeText(offer.recoveryKey);
      copied = true;
    } catch {
      error = "The key could not be copied. Select it and copy it by hand.";
    }
  }

  async function confirm(event: SubmitEvent) {
    event.preventDefault();
    if (!offer || !ready) return;
    busy = true;
    error = null;
    try {
      const confirmed = await withElevation(host.key, () => confirmRecoveryKey(host.key, offer!.setupId, groupKey(typed)));
      onclose(confirmed);
    } catch (e) {
      if (e instanceof ElevationCancelled) {
        // Stay open: nothing was confirmed.
      } else if (hasProblemCode(e, ProblemCodes.recoverySetupExpired)) {
        offer = null;
        typed = "";
        saved = false;
        error = "The recovery key expired. Get a new one.";
      } else {
        error = errorMessage(e);
      }
    } finally {
      busy = false;
    }
  }
</script>

<Dialog title="Recovery key for {target.name}" wide>
  <form onsubmit={confirm}>
    {#if offer}
      <p>
        This key opens every backup in <strong>{target.name}</strong>, on this PC or any other. HyperHarbor shows it
        <strong>only once</strong> and cannot recover it. Without it, the backups cannot be restored if this PC is lost.
      </p>
      <p class="key" data-testid="recovery-key">{offer.recoveryKey}</p>
      <div class="actions left">
        <button type="button" onclick={copy}>{copied ? "Copied" : "Copy the key"}</button>
      </div>
      <p class="muted">Keep it in a password manager, or print it and store it somewhere safe. Do not keep it only on this PC.</p>

      <label class="check">
        <input type="checkbox" bind:checked={saved} disabled={busy} />
        I saved the recovery key somewhere safe.
      </label>
      <label for="key-tail">Type the last four groups of the key from your saved copy</label>
      <input
        id="key-tail"
        type="text"
        bind:value={typed}
        disabled={busy || !saved}
        autocomplete="off"
        spellcheck="false"
        placeholder="XXXX-XXXX-XXXX-XXXX"
      />
    {:else if busy}
      <p class="muted">Creating the recovery key…</p>
    {/if}

    {#if error}<p class="error" role="alert">{error}</p>{/if}

    <div class="actions">
      <button type="button" onclick={() => onclose(null)} disabled={busy && offer !== null}>Not now</button>
      {#if offer}
        <button type="submit" class="primary" disabled={!ready}>{busy ? "Checking…" : "Confirm"}</button>
      {:else if !busy}
        <button type="button" class="primary" onclick={start}>Get a recovery key</button>
      {/if}
    </div>
    {#if offer}
      <p class="muted">Until you confirm, this target does not back anything up.</p>
    {/if}
  </form>
</Dialog>

<style>
  .key {
    margin: 0.5rem 0;
    padding: 0.75rem;
    border: 1px solid var(--border);
    border-radius: 6px;
    background: var(--surface-alt, var(--surface));
    font-family: var(--mono, ui-monospace, Consolas, monospace);
    font-size: 1.05rem;
    letter-spacing: 0.04em;
    overflow-wrap: anywhere;
    user-select: all;
  }

  .actions.left {
    justify-content: flex-start;
    margin-top: 0;
  }
</style>
