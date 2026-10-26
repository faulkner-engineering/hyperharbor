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
  import { formatLeft, guestAdminError, msLeft, passphraseParts, requestGuestAdmin } from "$lib/guestAdmin";
  import { ElevationCancelled } from "$lib/lifecycle.svelte";
  import Dialog from "./Dialog.svelte";

  interface Props {
    host: HostEntry;
    vm: Vm;
    onclose: () => void;
  }

  let { host, vm, onclose }: Props = $props();

  type Phase = "opening" | "open" | "ended" | "closing" | "failed";

  let phase = $state<Phase>("opening");
  let access = $state<GuestAdminWindow | null>(null);
  let error = $state<string | null>(null);
  let now = $state(Date.now());

  const left = $derived(access ? msLeft(access.expiresAt, now) : 0);
  const endsAt = $derived(
    access ? new Date(access.expiresAt).toLocaleTimeString([], { hour: "numeric", minute: "2-digit" }) : "",
  );

  /** Forgets the passphrase; the dialog keeps it only while the window is open. */
  function forget() {
    access = null;
  }

  /** Forgets the passphrase here and on the always-on-top card. */
  function forgetEverywhere() {
    forget();
    hideGuestAdminCard().catch(() => {});
  }

  async function request() {
    phase = "opening";
    error = null;
    try {
      access = await requestGuestAdmin(host.key, vm.id);
      now = Date.now();
      phase = "open";
      // A card on top of every window, so the passphrase stays readable over a full-screen Remote Desktop session.
      showGuestAdminCard({ vmName: vm.name, ...access }).catch(() => {});
    } catch (e) {
      if (e instanceof ElevationCancelled) {
        onclose();
        return;
      }
      error = guestAdminError(e, vm.name);
      phase = "failed";
    }
  }

  async function done() {
    phase = "closing";
    try {
      await closeGuestAdmin(host.key, vm.id);
      forgetEverywhere();
      onclose();
    } catch (e) {
      forgetEverywhere();
      error = `${errorMessage(e)} HyperHarbor disables the account as soon as it can reach ${vm.name} again.`;
      phase = "failed";
    }
  }

  function closeLeavingOpen() {
    forget();
    onclose();
  }

  /** The countdown's clock: one tick a second while the window is open, ending it when its time is up. */
  function tick() {
    now = Date.now();
    if (access && msLeft(access.expiresAt, now) === 0) {
      forgetEverywhere();
      phase = "ended";
    }
  }

  $effect(() => {
    if (phase !== "open") return;
    const timer = setInterval(tick, 1000);
    return () => clearInterval(timer);
  });

  onDestroy(forget);

  request();
</script>

<Dialog title="Administrator access to {vm.name}">
  {#if phase === "opening"}
    <p class="muted" role="status">Opening administrator access in {vm.name}…</p>
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
      Desktop. Closing this dialog leaves administrator access open until {endsAt}; Done ends it now.
    </p>
    <div class="actions">
      <button type="button" onclick={closeLeavingOpen}>Close</button>
      <button type="button" class="primary" onclick={done}>Done</button>
    </div>
  {:else if phase === "closing"}
    <p class="muted" role="status">Ending administrator access…</p>
  {:else if phase === "ended"}
    <p role="status">Administrator access to {vm.name} has ended; HyperHarbor disables the account in the VM again.</p>
    <div class="actions">
      <button type="button" onclick={onclose}>Close</button>
      <button type="button" class="primary" onclick={request}>Request again</button>
    </div>
  {:else}
    <p class="error" role="alert">{error}</p>
    <div class="actions">
      <button type="button" onclick={onclose}>Close</button>
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
