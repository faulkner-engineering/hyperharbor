<script lang="ts">
  import { flip } from "svelte/animate";
  import { fly } from "svelte/transition";
  import { tray } from "../store.svelte";
  import Icon from "./Icon.svelte";
</script>

<div class="toasts" aria-live="polite">
  {#each tray.toasts as toast (toast.id)}
    <div class="toast {toast.kind}" role={toast.kind === "error" ? "alert" : "status"} animate:flip={{ duration: 200 }} transition:fly={{ y: 16, duration: 220 }}>
      <Icon name={toast.kind === "error" ? "warning" : toast.kind === "success" ? "check" : "info"} />
      <div>
        <strong>{toast.title}</strong>
        <p>{toast.text}</p>
      </div>
      <button type="button" class="close" aria-label="Dismiss" onclick={() => tray.dismiss(toast.id)}><Icon name="close" size={14} /></button>
    </div>
  {/each}
</div>

<style>
  .toasts {
    position: fixed;
    right: 1.25rem;
    bottom: 1.25rem;
    z-index: 20;
    display: grid;
    gap: 0.6rem;
    width: min(380px, calc(100vw - 2.5rem));
  }

  .toast {
    display: grid;
    grid-template-columns: auto 1fr auto;
    gap: 0.7rem;
    align-items: start;
    padding: 0.8rem 0.9rem;
    border: 1px solid var(--border);
    border-left: 4px solid var(--busy-fg);
    border-radius: var(--radius);
    background: var(--surface);
    box-shadow: 0 8px 28px rgb(0 0 0 / 18%);
  }

  .toast.success {
    border-left-color: var(--ok-fg);
    color: var(--text);
  }

  .toast.success :global(svg) {
    color: var(--ok-fg);
  }

  .toast.error {
    border-left-color: var(--danger);
  }

  .toast.error :global(svg) {
    color: var(--danger);
  }

  p {
    margin-top: 0.1rem;
    color: var(--muted);
    font-size: 0.88rem;
    user-select: text;
  }

  .close {
    padding: 0.15rem;
    border: 0;
    background: none;
    color: var(--muted);
  }
</style>
