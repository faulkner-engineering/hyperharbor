<script lang="ts">
  import {
    errorMessage,
    hasProblemCode,
    hostSupports,
    PackageIndexApiVersion,
    listPackageCatalog,
    ProblemCodes,
    searchPackages,
    type HostEntry,
    type PackageCatalogItem,
    type PackageSearchResult,
    type ProfileItem,
  } from "$lib/api/client";
  import { looksLikeWingetId, type BrowseEntry, type Detail } from "$lib/catalogBrowser";
  import { addItem } from "$lib/setupProfiles";
  import { onMount } from "svelte";
  import CatalogBrowser from "./CatalogBrowser.svelte";
  import ProfileChips from "./ProfileChips.svelte";

  interface Props {
    host: HostEntry;
    /** The profile's install list. */
    install: ProfileItem[];
    selected: string | null;
    onselect: (detail: Detail) => void;
  }

  let { host, install = $bindable(), selected, onselect }: Props = $props();

  let catalog = $state<PackageCatalogItem[]>([]);
  let error = $state<string | null>(null);
  let searched = $state("");
  let results = $state<PackageSearchResult[] | null>(null);
  let searching = $state(false);
  let searchError = $state<string | null>(null);
  let query = $state("");
  /** The host searches its own winget index, so the search runs as the user types. */
  const instant = $derived(hostSupports(host, PackageIndexApiVersion));
  /** winget results the catalog list does not already show. */
  const extra = $derived(
    (results ?? []).filter((result) => !catalog.some((item) => item.id.toLowerCase() === result.id.toLowerCase())),
  );

  // Search as the user types, a moment after the last key.
  $effect(() => {
    const text = query.trim();
    if (!instant || text.length < 2) {
      results = null;
      searchError = null;
      return;
    }
    const timer = setTimeout(() => search(text), 250);
    return () => clearTimeout(timer);
  });

  const entries = $derived<BrowseEntry[]>(
    catalog.map((item) => ({
      key: item.id,
      name: item.name,
      category: item.category,
      description: item.description,
      code: item.id,
      popular: item.popular,
      keywords: item.alias,
    })),
  );

  onMount(async () => {
    try {
      catalog = await listPackageCatalog(host.key);
    } catch (e) {
      error = errorMessage(e);
    }
  });

  /** The profile entry for a winget id: profiles may list the id or the catalog alias. */
  function entryFor(id: string): ProfileItem | undefined {
    const alias = catalog.find((item) => item.id.toLowerCase() === id.toLowerCase())?.alias.toLowerCase();
    return install.find((item) => item.id.toLowerCase() === id.toLowerCase() || item.id.toLowerCase() === alias);
  }

  const has = (id: string) => entryFor(id) !== undefined;
  const catalogName = (id: string) => catalog.find((item) => item.id.toLowerCase() === id.toLowerCase() || item.alias === id.toLowerCase())?.name;

  function setIn(id: string, name: string | undefined, on: boolean) {
    if (on) {
      install = addItem(install, { id, name });
    } else {
      const existing = entryFor(id);
      install = install.filter((item) => item !== existing);
    }
  }

  function detailFor(id: string, name: string | undefined): Detail {
    const known = catalog.find((item) => item.id.toLowerCase() === id.toLowerCase() || item.alias === id.toLowerCase());
    const found = results?.find((result) => result.id === id);
    return {
      key: known?.id ?? id,
      title: known?.name ?? name ?? id,
      code: known?.id ?? id,
      category: known?.category,
      description: known?.description ?? (found ? `Version ${found.version} from ${found.source}.` : "Not in HyperHarbor's catalog; installed by its winget id."),
      isOn: () => has(known?.id ?? id),
      toggle: () => setIn(known?.id ?? id, known?.name ?? name, !has(known?.id ?? id)),
      onLabel: "Remove from profile",
      offLabel: "Add to profile",
    };
  }

  async function search(query: string) {
    searched = query;
    searching = true;
    searchError = null;
    try {
      const found = await searchPackages(host.key, query);
      if (query === searched) results = found;
    } catch (e) {
      if (query !== searched) return;
      results = null;
      searchError = hasProblemCode(e, ProblemCodes.wingetUnavailable)
        ? "Package search is not set up on this host. On the host, double-click the HyperHarbor tray icon and choose Set up package search. You can still add anything from the catalog, or a winget id as typed."
        : errorMessage(e);
    } finally {
      if (query === searched) searching = false;
    }
  }
</script>

<ProfileChips
  label="In this profile"
  items={install.map((item) => ({ key: item.id, label: item.name ?? catalogName(item.id) ?? item.id }))}
  onremove={(key) => (install = install.filter((item) => item.id !== key))}
  onselect={(key) => onselect(detailFor(key, install.find((item) => item.id === key)?.name))}
  empty="Nothing yet. Pick from the catalog below, or type a winget id."
/>
{#if error}<p class="error" role="alert">{error}</p>{/if}

<CatalogBrowser
  label="Package catalog"
  placeholder={instant ? "Search the catalog and winget, or type a winget id" : "Filter packages, or type a winget id"}
  bind:query
  {entries}
  isOn={has}
  ontoggle={(entry, on) => setIn(entry.key, entry.name, on)}
  onselect={(entry) => onselect(detailFor(entry.key, entry.name))}
  {selected}
>
  {#snippet footer(query)}
    {#if query.length >= 2}
      <div class="beyond">
        <div class="caption">{instant ? "From winget" : "Beyond the catalog"}{#if instant && searching}<span class="muted"> · searching…</span>{/if}</div>
        <div class="actions-row">
          {#if looksLikeWingetId(query) && !has(query)}
            <button type="button" onclick={() => setIn(query, undefined, true)}>Add {query} by id</button>
          {/if}
          {#if !instant}
            <button type="button" onclick={() => search(query)} disabled={searching}>
              {searching && searched === query ? "Searching…" : `Search winget for "${query}"`}
            </button>
          {/if}
        </div>
        {#if searchError}<p class="error" role="alert">{searchError}</p>{/if}
        {#if results !== null && (instant || !searching)}
          {#if extra.length === 0}
            <p class="muted">{results.length === 0 ? `winget found nothing for "${searched}".` : "winget has nothing beyond the catalog's matches."}</p>
          {:else}
            <ul aria-label="winget results">
              {#each extra as result (result.id)}
                {@const on = has(result.id)}
                <li>
                  <button type="button" class="pick" onclick={() => onselect(detailFor(result.id, result.name))}>
                    <span class="name">{result.name}</span><code>{result.id}</code><span class="muted">{result.version}</span>
                  </button>
                  <button type="button" aria-label="{on ? 'Remove' : 'Add'} {result.name}" onclick={() => setIn(result.id, result.name, !on)}>
                    {on ? "Remove" : "Add"}
                  </button>
                </li>
              {/each}
            </ul>
          {/if}
        {/if}
      </div>
    {/if}
  {/snippet}
</CatalogBrowser>

<style>
  .beyond {
    margin-top: 0.75rem;
  }

  .caption {
    font-family: ui-monospace, "Cascadia Mono", Consolas, monospace;
    font-size: 0.72rem;
    letter-spacing: 0.1em;
    text-transform: uppercase;
    color: var(--muted);
    margin-bottom: 0.4rem;
  }

  .actions-row {
    display: flex;
    flex-wrap: wrap;
    gap: 0.4rem;
  }

  ul {
    list-style: none;
    margin: 0.5rem 0 0;
    padding: 0;
  }

  li {
    display: flex;
    align-items: center;
    gap: 0.5rem;
    border-bottom: 1px solid var(--border);
  }

  .pick {
    flex: 1;
    min-width: 0;
    display: flex;
    align-items: baseline;
    gap: 0.45rem;
    padding: 0.4rem !important;
    border: none !important;
    background: transparent !important;
    text-align: left;
  }

  .name {
    font-weight: 600;
  }

  code {
    font-size: 0.75rem;
    color: var(--muted);
  }
</style>
