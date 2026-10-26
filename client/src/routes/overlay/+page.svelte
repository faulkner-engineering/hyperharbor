<script lang="ts">
  import { onMount } from "svelte";
  import { listen } from "@tauri-apps/api/event";
  import { getGuestAdminCard, GuestAdminCardChangedEvent, hideGuestAdminCard, type GuestAdminCard } from "$lib/api/client";
  import { formatLeft, msLeft, passphraseParts } from "$lib/guestAdmin";

  // The always-on-top passphrase card (src-tauri/src/guest_admin_overlay.rs). It never takes the focus, so the user
  // reads it while typing into the VM's prompt. Like the dialog, it has no copy control: with clipboard redirection on,
  // the clipboard is readable inside the VM's session. It closes itself when the window ends.
  let card = $state<GuestAdminCard | null>(null);
  let now = $state(Date.now());

  const left = $derived(card ? msLeft(card.expiresAt, now) : 0);

  async function load() {
    card = await getGuestAdminCard();
    now = Date.now();
    if (!card) await hideGuestAdminCard();
  }

  function tick() {
    now = Date.now();
    if (card && msLeft(card.expiresAt, now) === 0) {
      card = null;
      hideGuestAdminCard();
    }
  }

  onMount(() => {
    load();
    const timer = setInterval(tick, 1000);
    const changed = listen(GuestAdminCardChangedEvent, load);
    return () => {
      clearInterval(timer);
      changed.then((unlisten) => unlisten());
    };
  });

  function hide() {
    card = null;
    hideGuestAdminCard();
  }
</script>

<svelte:head><title>HyperHarbor administrator passphrase</title></svelte:head>

{#if card}
  <div class="card" data-tauri-drag-region>
    <div class="top" data-tauri-drag-region>
      <span class="label" data-tauri-drag-region>Administrator passphrase · {card.vmName}</span>
      <span class="time" role="timer" aria-live="off" data-tauri-drag-region>{formatLeft(left)}</span>
      <button type="button" aria-label="Hide" title="Hide this card (access stays open)" onclick={hide}>✕</button>
    </div>
    <div class="passphrase" aria-label="Passphrase {card.passphrase.split('-').join(' dash ')}" data-tauri-drag-region>
      {#each passphraseParts(card.passphrase) as part, index (index)}
        {#if index > 0}<span class="dash" aria-hidden="true" data-tauri-drag-region>-</span>{/if}<span
          class="part"
          aria-hidden="true"
          data-tauri-drag-region>{part}</span
        >
      {/each}
    </div>
    <div class="hint" data-tauri-drag-region>More choices → {card.accountName}</div>
  </div>
{/if}

<style>
  :global(html),
  :global(body) {
    margin: 0;
    height: 100%;
    overflow: hidden;
    background: #11151a;
  }

  .card {
    box-sizing: border-box;
    height: 100vh;
    display: flex;
    flex-direction: column;
    justify-content: space-between;
    padding: 0.5rem 0.75rem 0.6rem;
    border: 1px solid #e39b2e;
    color: #f3f1ec;
    font-family: "Segoe UI", system-ui, sans-serif;
    font-size: 12px;
    user-select: none;
    cursor: default;
  }

  .top {
    display: flex;
    align-items: center;
    gap: 0.5rem;
  }

  .label {
    flex: 1;
    overflow: hidden;
    white-space: nowrap;
    text-overflow: ellipsis;
    color: #8a93a1;
  }

  .time {
    font-variant-numeric: tabular-nums;
    color: #e39b2e;
  }

  button {
    padding: 0 0.3rem;
    border: none;
    background: none;
    color: #8a93a1;
    font: inherit;
    cursor: pointer;
  }

  button:hover {
    color: #f3f1ec;
  }

  .passphrase {
    display: flex;
    align-items: baseline;
    gap: 0.15rem;
    font-family: ui-monospace, "Cascadia Mono", Consolas, monospace;
    font-size: 22px;
    white-space: nowrap;
  }

  .dash {
    color: #8a93a1;
  }

  .hint {
    color: #8a93a1;
  }
</style>
