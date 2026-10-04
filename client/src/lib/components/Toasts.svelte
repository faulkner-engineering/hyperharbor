<script lang="ts">
  import { TOAST_DURATION_MS, toasts } from "$lib/toasts.svelte";
</script>

<div class="toasts" aria-live="polite">
  {#each toasts.items as toast (toast.id)}
    <div class="toast {toast.kind}" role={toast.kind === "error" ? "alert" : "status"}>
      <p>{toast.message}</p>
      <button type="button" class="close" aria-label="Close notification" onclick={() => toasts.dismiss(toast.id)}>×</button>
      <div class="progress" style:animation-duration="{TOAST_DURATION_MS}ms"></div>
    </div>
  {/each}
</div>

<style>
  .toasts {
    position: fixed;
    right: 1rem;
    bottom: 1rem;
    z-index: 50;
    display: flex;
    flex-direction: column;
    gap: 0.5rem;
    width: min(24rem, calc(100vw - 2rem));
    pointer-events: none;
  }

  .toast {
    position: relative;
    overflow: hidden;
    padding: 0.75rem 2.5rem 1rem 1rem;
    border: 1px solid var(--border);
    border-left: 4px solid var(--accent);
    border-radius: 0;
    background: var(--surface);
    box-shadow: 0 4px 12px rgb(0 0 0 / 0.18);
    pointer-events: auto;
  }

  .toast.error {
    border-left-color: var(--danger);
  }

  p {
    margin: 0;
    overflow-wrap: anywhere;
  }

  .close {
    position: absolute;
    top: 0.35rem;
    right: 0.35rem;
    width: 1.75rem;
    height: 1.75rem;
    border: none;
    border-radius: 0;
    background: none;
    color: var(--muted);
    font-size: 1.25rem;
    line-height: 1;
    cursor: pointer;
  }

  .close:hover {
    background: var(--hover);
    color: inherit;
  }

  .progress {
    position: absolute;
    left: 0;
    bottom: 0;
    height: 3px;
    width: 100%;
    background: var(--accent);
    transform-origin: left;
    animation-name: fill;
    animation-timing-function: linear;
    animation-fill-mode: forwards;
  }

  .toast.error .progress {
    background: var(--danger);
  }

  @keyframes fill {
    from {
      transform: scaleX(0);
    }
    to {
      transform: scaleX(1);
    }
  }
</style>
