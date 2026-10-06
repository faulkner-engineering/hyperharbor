<script lang="ts">
  import type { Snippet } from "svelte";

  interface Props {
    /** Accessible name of the button, for example "About administrator installs". */
    label: string;
    /** The explanation shown in the popup. */
    children: Snippet;
  }

  let { label, children }: Props = $props();

  const popupId = $props.id();

  // Click or keyboard pins the popup open; hovering shows it only while the pointer is over the icon or the popup.
  let pinned = $state(false);
  let hovered = $state(false);
  let root = $state<HTMLSpanElement>();
  const open = $derived(pinned || hovered);

  function closeOnOutsideClick(event: MouseEvent) {
    if (pinned && root && !root.contains(event.target as Node)) pinned = false;
  }

  function closeOnEscape(event: KeyboardEvent) {
    if (open && event.key === "Escape") {
      pinned = false;
      hovered = false;
    }
  }
</script>

<svelte:window onclick={closeOnOutsideClick} onkeydown={closeOnEscape} />

<span class="info-tip" bind:this={root} onmouseenter={() => (hovered = true)} onmouseleave={() => (hovered = false)} role="presentation">
  <button type="button" class="info-button" aria-label={label} aria-expanded={open} aria-controls={popupId} onclick={() => (pinned = !pinned)}>
    i
  </button>
  <span class="info-popup" id={popupId} role="note" hidden={!open}>{@render children()}</span>
</span>

<style>
  .info-tip {
    position: relative;
    display: inline-block;
    vertical-align: middle;
  }

  .info-button {
    width: 1.15rem;
    height: 1.15rem;
    padding: 0;
    border: 1px solid var(--border);
    border-radius: 50%;
    background: var(--surface);
    color: inherit;
    font: italic 700 0.75rem/1 serif;
    cursor: pointer;
  }

  .info-button:hover,
  .info-button[aria-expanded="true"] {
    background: var(--hover);
  }

  .info-popup {
    position: absolute;
    top: 100%;
    left: 0;
    z-index: 10;
    width: max-content;
    max-width: min(22rem, 80vw);
    margin-top: 0.3rem;
    padding: 0.6rem 0.75rem;
    border: 1px solid var(--border);
    border-radius: 6px;
    background: var(--surface);
    box-shadow: 0 4px 12px rgb(0 0 0 / 0.15);
    font-size: 0.85rem;
    font-weight: 400;
    font-style: normal;
    line-height: 1.4;
    white-space: normal;
    text-align: left;
  }

  .info-popup[hidden] {
    display: none;
  }
</style>
