<script lang="ts">
  interface Chip {
    key: string;
    label: string;
  }

  interface Props {
    /** What the list holds, for screen readers and the heading. */
    label: string;
    items: Chip[];
    onremove: (key: string) => void;
    /** Shows the item's details; chips are plain text without it. */
    onselect?: (key: string) => void;
    empty: string;
  }

  let { label, items, onremove, onselect, empty }: Props = $props();
</script>

<div class="profile-chips">
  <div class="heading">{label}<span class="n">{items.length}</span></div>
  {#if items.length === 0}
    <p class="muted">{empty}</p>
  {:else}
    <ul aria-label={label}>
      {#each items as item (item.key)}
        <li>
          {#if onselect}
            <button type="button" class="label" onclick={() => onselect(item.key)}>{item.label}</button>
          {:else}
            <span class="label">{item.label}</span>
          {/if}
          <button type="button" class="remove" aria-label="Remove {item.label}" onclick={() => onremove(item.key)}>×</button>
        </li>
      {/each}
    </ul>
  {/if}
</div>

<style>
  .profile-chips {
    margin-bottom: 0.6rem;
  }

  .heading {
    font-size: 0.85rem;
    font-weight: 600;
    margin-bottom: 0.35rem;
  }

  .n {
    margin-left: 0.4rem;
    font-family: ui-monospace, "Cascadia Mono", Consolas, monospace;
    font-size: 0.75rem;
    color: var(--accent);
  }

  ul {
    display: flex;
    flex-wrap: wrap;
    gap: 0.3rem;
    list-style: none;
    margin: 0;
    padding: 0;
  }

  li {
    display: flex;
    align-items: center;
    border: 1px solid color-mix(in srgb, var(--accent) 45%, var(--border));
    border-radius: 999px;
    background: color-mix(in srgb, var(--accent) 10%, transparent);
    font-size: 0.82rem;
  }

  .label {
    padding: 0.15rem 0.2rem 0.15rem 0.65rem !important;
    border: none !important;
    background: transparent !important;
  }

  span.label {
    padding: 0.15rem 0.2rem 0.15rem 0.65rem;
  }

  .remove {
    padding: 0.1rem 0.55rem 0.15rem 0.35rem !important;
    border: none !important;
    background: transparent !important;
    color: var(--muted);
    line-height: 1;
  }

  .remove:hover {
    color: var(--danger);
  }
</style>
