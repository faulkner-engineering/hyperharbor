<script lang="ts">
  import type { Snippet } from "svelte";
  import { fade, scale } from "svelte/transition";

  interface Props {
    title: string;
    onclose: () => void;
    children: Snippet;
    /** Buttons at the bottom. */
    actions: Snippet;
  }

  let { title, onclose, children, actions }: Props = $props();
  const id = $props.id();
</script>

<svelte:window onkeydown={(event) => event.key === "Escape" && onclose()} />

<div class="backdrop" transition:fade={{ duration: 150 }} onclick={(event) => event.target === event.currentTarget && onclose()} role="presentation">
  <div class="modal" role="dialog" aria-modal="true" aria-labelledby="{id}-title" transition:scale={{ start: 0.96, duration: 180 }}>
    <h2 id="{id}-title">{title}</h2>
    <div class="content">{@render children()}</div>
    <div class="actions">{@render actions()}</div>
  </div>
</div>

<style>
  .backdrop {
    position: fixed;
    inset: 0;
    z-index: 10;
    display: grid;
    place-items: center;
    padding: 1.5rem;
    background: rgb(13 17 23 / 45%);
  }

  .modal {
    width: min(460px, 100%);
    padding: 1.4rem;
    border: 1px solid var(--border);
    border-radius: 14px;
    background: var(--surface);
    box-shadow: 0 12px 48px rgb(0 0 0 / 30%);
  }

  h2 {
    font-size: 1.1rem;
    font-weight: 600;
  }

  .content {
    display: grid;
    gap: 0.75rem;
    margin: 0.8rem 0 1.2rem;
  }

  .actions {
    display: flex;
    justify-content: flex-end;
    gap: 0.5rem;
  }
</style>
