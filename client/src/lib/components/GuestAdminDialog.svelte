<script lang="ts">
  import { onDestroy } from "svelte";
  import {
    closeGuestAdmin,
    errorMessage,
    hideGuestAdminCard,
    showGuestAdminCard,
    type GuestAdminWindow,
    type HostEntry,
    type Vm,
  } from "$lib/api/client";
  import {
    formatLeft,
    guestAdminError,
    msLeft,
    passphraseParts,
    requestGuestAdmin,
    SLOW_AFTER_MS,
  } from "$lib/guestAdmin";
  import { ElevationCancelled } from "$lib/lifecycle.svelte";
  import { toasts } from "$lib/toasts.svelte";
  import Dialog from "./Dialog.svelte";

  interface Props {
    host: HostEntry;
    vm: Vm;
    onclose: () => void;
    /** Called when the window may have opened or ended, so the page can read its state from the host again. */
    onchanged?: () => void;
  }

  let { host, vm, onclose, onchanged }: Props = $props();

  type Phase = "opening" | "open" | "ended" | "failed";

  let phase = $state<Phase>("opening");
  let slow = $state(false);
  let access = $state<GuestAdminWindow | null>(null);
  let error = $state<string | null>(null);
  let now = $state(Date.now());
  /** Set once the dialog is gone; a late answer must not show the card then. */
  let dismissed = false;

  const left = $derived(access ? msLeft(access.expiresAt, now) : 0);

  function timeOf(iso: string): string {
    return new Date(iso).toLocaleTimeString([], { hour: "numeric", minute: "2-digit" });
  }

  /** Leaving never waits on the host (docs/ui-principles.md): the dialog goes away at once and forgets the passphrase. */
  function leave() {
    dismissed = true;
    access = null;
    onclose();
  }

  async function request() {
    phase = "opening";
    error = null;
    slow = false;
    const timer = setTimeout(() => (slow = true), SLOW_AFTER_MS);
    try {
      const opened = await requestGuestAdmin(host.key, vm.id);
      if (dismissed) return;
      access = opened;
      now = Date.now();
      phase = "open";
      // A card on top of every window, so the passphrase stays readable over a full-screen Remote Desktop session.
      showGuestAdminCard({ vmName: vm.name, ...opened }).catch(() => {});
    } catch (e) {
      if (dismissed) return;
      if (e instanceof ElevationCancelled) {
        leave();
        return;
      }
      error = guestAdminError(e, vm.name);
      phase = "failed";
    } finally {
      clearTimeout(timer);
      onchanged?.();
    }
  }

  /** Ends the window. The dialog and the card go away at once; the host is told in the background. */
  function done() {
    const endsAt = access ? timeOf(access.expiresAt) : "";
    leave();
    hideGuestAdminCard().catch(() => {});
    closeGuestAdmin(host.key, vm.id)
      .catch((e) =>
        toasts.error(
          `Administrator access to ${vm.name} could not be ended: ${errorMessage(e)} It ends by itself at ${endsAt}, ` +
            "and HyperHarbor disables the account as soon as it can reach the VM.",
        ),
      )
      .finally(() => onchanged?.());
  }

  /** The countdown's clock: one tick a second while the window is open, ending it when its time is up. */
  function tick() {
    now = Date.now();
    if (access && msLeft(access.expiresAt, now) === 0) {
      access = null;
      hideGuestAdminCard().catch(() => {});
      phase = "ended";
      onchanged?.();
    }
  }

  $effect(() => {
    if (phase !== "open") return;
    const timer = setInterval(tick, 1000);
    return () => clearInterval(timer);
  });

  onDestroy(() => {
    dismissed = true;
    access = null;
  });

  request();
</script>

<Dialog title="Administrator access to {vm.name}">
  {#if phase === "opening"}
    <p class="muted" role="status">Opening administrator access in {vm.name}…</p>
    {#if slow}
      <p class="muted">
        This is taking longer than usual; {vm.name} may be slow to answer. You can close this. If access opens anyway, it
        ends by itself, and Administrator access… in the VM's menu shows it again.
      </p>
      <div class="actions">
        <button type="button" onclick={leave}>Close</button>
      </div>
    {/if}
  {:else if phase === "open" && access}
    <p>In the Windows prompt choose <strong>More choices</strong>, pick this account, and type the passphrase.</p>
    <dl>
      <dt>Account</dt>
      <dd class="mono">{access.accountName}</dd>
      <dt>Passphrase</dt>
      <dd>
        <!-- No copy button on purpose: with clipboard redirection on, anything on this device's clipboard can be read
             inside the VM's session, so the passphrase is typed rather than pasted. -->
        <span class="passphrase mono" aria-label="Passphrase {access.passphrase.split('-').join(' dash ')}">
          {#each passphraseParts(access.passphrase) as part, index (index)}
            {#if index > 0}<span class="dash" aria-hidden="true">-</span>{/if}<span class="part" aria-hidden="true">{part}</span>
          {/each}
        </span>
      </dd>
    </dl>
    <p class="muted" role="timer" aria-live="off">
      Ends in {formatLeft(left)}. The passphrase also stays in a card at the top right of your screen, above Remote
      Desktop. Closing this dialog leaves administrator access open until {timeOf(access.expiresAt)}; Done ends it now.
    </p>
    <div class="actions">
      <button type="button" onclick={leave}>Close</button>
      <button type="button" class="primary" onclick={done}>Done</button>
    </div>
  {:else if phase === "ended"}
    <p role="status">Administrator access to {vm.name} has ended; HyperHarbor disables the account in the VM again.</p>
    <div class="actions">
      <button type="button" onclick={leave}>Close</button>
      <button type="button" class="primary" onclick={request}>Request again</button>
    </div>
  {:else}
    <p class="error" role="alert">{error}</p>
    <div class="actions">
      <button type="button" onclick={leave}>Close</button>
      <button type="button" class="primary" onclick={request}>Try again</button>
    </div>
  {/if}
</Dialog>

<style>
  dl {
    display: grid;
    grid-template-columns: auto 1fr;
    gap: 0.5rem 1rem;
    align-items: baseline;
    margin: 1rem 0;
  }

  dt {
    color: var(--muted);
    font-size: 0.85rem;
  }

  dd {
    margin: 0;
  }

  .mono {
    font-family: ui-monospace, "Cascadia Mono", Consolas, monospace;
  }

  .passphrase {
    display: flex;
    flex-wrap: wrap;
    align-items: baseline;
    gap: 0.15rem;
    font-size: 1.6rem;
    line-height: 1.4;
    user-select: none;
  }

  .part {
    padding: 0 0.35rem;
    border-radius: 4px;
    border: 1px solid var(--border);
  }

  .dash {
    color: var(--muted);
  }
</style>
