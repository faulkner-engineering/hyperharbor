<script lang="ts">
  import type { Snippet } from "svelte";

  interface Props {
    title: string;
    /** Wider dialogs for forms with two columns. */
    wide?: boolean;
    /** Nearly the whole window, for editors and side-by-side lists. */
    xl?: boolean;
    children: Snippet;
  }

  let { title, wide = false, xl = false, children }: Props = $props();

  const titleId = $props.id();
</script>

<div class="backdrop" role="presentation">
  <div class="dialog" class:wide class:xl role="dialog" aria-modal="true" aria-labelledby={titleId}>
    <h3 id={titleId}>{title}</h3>
    {@render children()}
  </div>
</div>

<style>
  .backdrop {
    position: fixed;
    inset: 0;
    display: grid;
    place-items: center;
    background: rgb(0 0 0 / 0.35);
    z-index: 10;
  }

  .dialog {
    width: min(480px, calc(100vw - 2rem));
    max-height: calc(100vh - 2rem);
    overflow-y: auto;
    padding: 1.25rem;
    border-radius: 8px;
    background: var(--surface);
    border: 1px solid var(--border);
    box-sizing: border-box;
  }

  .dialog.wide {
    width: min(620px, calc(100vw - 2rem));
  }

  .dialog.xl {
    width: min(1100px, calc(100vw - 2rem));
    height: min(820px, calc(100vh - 2rem));
    display: flex;
    flex-direction: column;
  }

  h3 {
    margin: 0 0 0.75rem;
    font-size: 1.05rem;
  }

  /* Shared form styling for the content of every dialog. */
  .dialog :global(form) {
    display: flex;
    flex-direction: column;
    gap: 0.4rem;
  }

  .dialog :global(.muted) {
    margin: 0 0 0.5rem;
    color: var(--muted);
    font-size: 0.9rem;
  }

  .dialog :global(label) {
    font-size: 0.85rem;
    color: var(--muted);
  }

  .dialog :global(input:not([type="checkbox"]):not([type="radio"])),
  .dialog :global(select),
  .dialog :global(textarea) {
    padding: 0.45rem 0.6rem;
    border: 1px solid var(--border);
    border-radius: 6px;
    background: var(--bg);
    color: inherit;
    font: inherit;
  }

  .dialog :global(.check) {
    display: flex;
    align-items: center;
    gap: 0.5rem;
    color: inherit;
    margin: 0.25rem 0;
  }

  .dialog :global(.error) {
    margin: 0.25rem 0;
    color: var(--danger);
  }

  .dialog :global(.warning) {
    margin: 0.25rem 0;
    padding: 0.5rem 0.75rem;
    border-radius: 6px;
    background: var(--warn-bg);
    color: var(--warn-fg);
  }

  .dialog :global(.actions) {
    display: flex;
    justify-content: flex-end;
    gap: 0.5rem;
    margin-top: 0.75rem;
  }

  .dialog :global(button) {
    padding: 0.45rem 1rem;
    border: 1px solid var(--border);
    border-radius: 6px;
    background: var(--surface);
    color: inherit;
    cursor: pointer;
    font: inherit;
  }

  .dialog :global(button.primary) {
    background: var(--accent);
    border-color: var(--accent);
    color: var(--accent-fg);
  }

  .dialog :global(button.danger) {
    background: var(--danger);
    border-color: var(--danger);
    color: #ffffff;
  }

  .dialog :global(button:disabled) {
    opacity: 0.5;
    cursor: default;
  }
</style>
