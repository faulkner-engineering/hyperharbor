<script lang="ts">
  import type { Snippet } from "svelte";

  interface Props {
    /** Text of the button that opens the menu. */
    label: string;
    ariaLabel?: string;
    title?: string;
    /** "icon" is a compact button for a single symbol such as ⋮; "button" matches toolbar buttons. */
    variant?: "icon" | "button";
    /** Buttons, separated with <hr /> where useful. Choosing an enabled button closes the menu. */
    children: Snippet;
  }

  let { label, ariaLabel, title, variant = "icon", children }: Props = $props();

  // The element's own open state is used directly: <details> opens and closes itself from its summary.
  let root = $state<HTMLDetailsElement>();

  function close() {
    if (root) root.open = false;
  }

  function closeOnChoice(event: MouseEvent) {
    const button = (event.target as HTMLElement).closest("button");
    if (button && !button.disabled) close();
  }

  function closeOnOutsideClick(event: MouseEvent) {
    if (root?.open && !root.contains(event.target as Node)) close();
  }

  function closeOnEscape(event: KeyboardEvent) {
    if (root?.open && event.key === "Escape") close();
  }
</script>

<svelte:window onclick={closeOnOutsideClick} onkeydown={closeOnEscape} />

<details class="menu {variant}" bind:this={root}>
  <summary aria-label={ariaLabel} {title}>{label}</summary>
  <!-- svelte-ignore a11y_click_events_have_key_events, a11y_no_static_element_interactions -->
  <div class="menu-items" onclick={closeOnChoice}>
    {@render children()}
  </div>
</details>

<style>
  .menu {
    display: inline-block;
    position: relative;
  }

  .menu.icon {
    margin-left: 0.35rem;
  }

  .menu summary {
    list-style: none;
    border: 1px solid var(--border);
    border-radius: 6px;
    background: var(--surface);
    cursor: pointer;
    user-select: none;
  }

  .menu.icon summary {
    padding: 0.3rem 0.5rem;
    font-weight: 700;
    line-height: 1;
  }

  .menu.button summary {
    padding: 0.4rem 0.9rem;
  }

  .menu summary::-webkit-details-marker {
    display: none;
  }

  .menu-items {
    position: absolute;
    right: 0;
    z-index: 5;
    display: flex;
    flex-direction: column;
    min-width: 10rem;
    margin-top: 0.25rem;
    padding: 0.25rem;
    border: 1px solid var(--border);
    border-radius: 6px;
    background: var(--surface);
    box-shadow: 0 4px 12px rgb(0 0 0 / 0.15);
  }

  .menu-items :global(button) {
    border: none;
    border-radius: 4px;
    background: none;
    color: inherit;
    font: inherit;
    text-align: left;
    white-space: nowrap;
    padding: 0.4rem 0.6rem;
    cursor: pointer;
  }

  .menu-items :global(button:hover) {
    background: var(--hover);
  }

  .menu-items :global(button.danger) {
    color: var(--danger);
  }

  .menu-items :global(button:disabled) {
    opacity: 0.5;
    cursor: default;
  }

  .menu-items :global(button:disabled:hover) {
    background: none;
  }

  .menu-items :global(hr) {
    width: 100%;
    margin: 0.25rem 0;
    border: none;
    border-top: 1px solid var(--border);
  }
</style>
