<script lang="ts">
  import { ALL, chipsFor, filterEntries, type BrowseEntry } from "$lib/catalogBrowser";
  import type { Snippet } from "svelte";

  interface Props {
    /** Names the filter box and the list, for screen readers. */
    label: string;
    placeholder?: string;
    entries: BrowseEntry[];
    /** Whether an entry is in the profile. */
    isOn: (key: string) => boolean;
    ontoggle: (entry: BrowseEntry, on: boolean) => void;
    onselect: (entry: BrowseEntry) => void;
    /** The entry whose details are shown. */
    selected?: string | null;
    /** Button text for an entry in the profile (removes it) and one not in it (adds it). */
    onLabel?: string;
    offLabel?: string;
    /** A short status word per entry, for example an Appx rating; also its CSS class. */
    badge?: (entry: BrowseEntry) => string | undefined;
    disabled?: (entry: BrowseEntry) => boolean;
    /** Category order for the chips; by size when left out. */
    order?: string[];
    query?: string;
    /** Extra controls under the chips, for example "Add all safe". */
    toolbar?: Snippet;
    /** After the list, given the filter text: for example a winget search for what the catalog lacks. */
    footer?: Snippet<[string]>;
  }

  let {
    label,
    placeholder = "Filter",
    entries,
    isOn,
    ontoggle,
    onselect,
    selected = null,
    onLabel = "Remove",
    offLabel = "Add",
    badge,
    disabled,
    order,
    query = $bindable(""),
    toolbar,
    footer,
  }: Props = $props();

  let group = $state(ALL);

  const chips = $derived(chipsFor(entries, order));
  const matching = $derived(new Map(chipsFor(filterEntries(entries, query, ALL), order).map((chip) => [chip.id, chip.count])));
  const shown = $derived(filterEntries(entries, query, group));
</script>

<div class="browser">
  <div class="filters">
    <input type="search" aria-label={label} {placeholder} bind:value={query} />
    <div class="chips" role="radiogroup" aria-label="{label}: categories">
      {#each chips as chip (chip.id)}
        <button type="button" role="radio" aria-checked={group === chip.id} class:active={group === chip.id} onclick={() => (group = chip.id)}>
          {chip.label}<span class="n">{matching.get(chip.id) ?? 0}</span>
        </button>
      {/each}
    </div>
    {@render toolbar?.()}
  </div>

  {#if shown.length === 0}
    <p class="muted empty">Nothing in the catalog matches{query.trim() ? ` "${query.trim()}"` : ""}.</p>
  {:else}
    <ul aria-label={label}>
      {#each shown as entry (entry.key)}
        {@const on = isOn(entry.key)}
        {@const status = badge?.(entry)}
        <li class:selected={selected === entry.key} class:on>
          <button type="button" class="pick" aria-current={selected === entry.key ? "true" : undefined} onclick={() => onselect(entry)}>
            <span class="title">
              <span class="name">{entry.name}</span>
              {#if status}<span class="badge {status}">{status}</span>{/if}
              {#if entry.code}<code>{entry.code}</code>{/if}
            </span>
            {#if entry.description}<span class="description">{entry.description}</span>{/if}
          </button>
          <button
            type="button"
            class="toggle"
            class:on
            aria-label="{on ? onLabel : offLabel} {entry.name}"
            disabled={!on && (disabled?.(entry) ?? false)}
            onclick={() => ontoggle(entry, !on)}>{on ? onLabel : offLabel}</button
          >
        </li>
      {/each}
    </ul>
  {/if}

  {@render footer?.(query.trim())}
</div>

<style>
  .browser {
    display: flex;
    flex-direction: column;
  }

  /* Pinned to the top of the panel's one scroll area, so the filter stays at hand in a long list. */
  .filters {
    position: sticky;
    top: 0;
    z-index: 1;
    display: flex;
    flex-direction: column;
    gap: 0.45rem;
    padding: 0.2rem 0 0.55rem;
    background: var(--surface);
    border-bottom: 1px solid var(--border);
  }

  .chips {
    display: flex;
    flex-wrap: wrap;
    gap: 0.3rem;
  }

  .chips button {
    padding: 0.15rem 0.6rem !important;
    border-radius: 999px !important;
    font-size: 0.8rem;
    color: var(--muted);
  }

  .chips button.active {
    color: var(--text);
    border-color: var(--accent) !important;
    background: color-mix(in srgb, var(--accent) 14%, transparent);
  }

  .n {
    margin-left: 0.35rem;
    font-family: ui-monospace, "Cascadia Mono", Consolas, monospace;
    font-size: 0.72rem;
    color: var(--accent);
  }

  ul {
    list-style: none;
    margin: 0;
    padding: 0;
  }

  li {
    display: flex;
    align-items: center;
    gap: 0.5rem;
    border-bottom: 1px solid var(--border);
  }

  li.selected {
    background: color-mix(in srgb, var(--accent) 8%, transparent);
    box-shadow: inset 2px 0 0 var(--accent);
  }

  .pick {
    flex: 1;
    min-width: 0;
    display: flex;
    flex-direction: column;
    align-items: flex-start;
    gap: 0.1rem;
    padding: 0.45rem 0.4rem !important;
    border: none !important;
    background: transparent !important;
    text-align: left;
  }

  .title {
    display: flex;
    align-items: baseline;
    gap: 0.45rem;
    min-width: 0;
    max-width: 100%;
  }

  .name {
    font-weight: 600;
  }

  li.on .name {
    color: var(--accent);
  }

  code {
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
    font-size: 0.75rem;
    color: var(--muted);
  }

  .description {
    max-width: 100%;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
    font-size: 0.8rem;
    color: var(--muted);
  }

  .toggle {
    flex: none;
    min-width: 5.2rem;
    padding: 0.2rem 0.7rem !important;
  }

  .toggle.on {
    color: var(--muted);
  }

  .badge {
    font-size: 0.68rem;
    text-transform: uppercase;
    letter-spacing: 0.06em;
    padding: 0 0.35rem;
    border-radius: 4px;
    background: var(--idle-bg);
  }

  .badge.safe {
    background: var(--ok-bg);
    color: var(--ok-fg);
  }

  .badge.caution {
    background: var(--warn-bg);
    color: var(--warn-fg);
  }

  .empty {
    margin: 0.75rem 0 !important;
  }
</style>
