<script lang="ts">
  import {
    errorMessage,
    hasProblemCode,
    listPackageCatalog,
    ProblemCodes,
    searchPackages,
    type HostEntry,
    type PackageCatalogItem,
    type PackageSearchResult,
    type ProfileItem,
  } from "$lib/api/client";
  import { onMount } from "svelte";
  import SearchInput from "./SearchInput.svelte";
  import Tabs from "./Tabs.svelte";

  interface Props {
    host: HostEntry;
    /** Ids already in the profile, shown as added. */
    added: string[];
    onadd: (item: ProfileItem) => void;
  }

  let { host, added, onadd }: Props = $props();

  let tab = $state("popular");
  let catalog = $state<PackageCatalogItem[]>([]);
  let results = $state<PackageSearchResult[] | null>(null);
  let searching = $state(false);
  let error = $state<string | null>(null);
  let lastQuery = "";

  const addedIds = $derived(new Set(added.map((id) => id.toLowerCase())));
  const popular = $derived(catalog.filter((item) => item.popular));

  onMount(async () => {
    try {
      catalog = await listPackageCatalog(host.key);
    } catch (e) {
      error = errorMessage(e);
    }
  });

  async function search(query: string) {
    lastQuery = query;
    error = null;
    if (query.length === 0) {
      results = null;
      return;
    }

    searching = true;
    try {
      const found = await searchPackages(host.key, query);
      // An older search that finishes late must not replace a newer one.
      if (query === lastQuery) results = found;
    } catch (e) {
      if (query !== lastQuery) return;
      results = null;
      error = hasProblemCode(e, ProblemCodes.wingetUnavailable)
        ? "Package search is not set up on this host. On the host, double-click the HyperHarbor tray icon and choose Set up package search. You can still add packages from the Popular tab."
        : errorMessage(e);
    } finally {
      if (query === lastQuery) searching = false;
    }
  }

  const isAdded = (id: string) => addedIds.has(id.toLowerCase());
</script>

<div class="picker">
  <Tabs
    label="Find packages"
    tabs={[
      { id: "popular", label: "Popular" },
      { id: "search", label: "Search winget" },
    ]}
    bind:selected={tab}
  />

  {#if tab === "search"}
    <SearchInput label="Search winget packages" placeholder="Search, for example git or vscode" onsearch={search} />
    {#if error}<p class="error" role="alert">{error}</p>{/if}
    {#if searching}<p class="muted">Searching…</p>{/if}
    {#if results !== null && !searching}
      {#if results.length === 0}
        <p class="muted">Nothing found.</p>
      {:else}
        <ul>
          {#each results as result (result.id)}
            <li>
              <span class="name">{result.name}</span>
              <code>{result.id}</code>
              <span class="muted small">{result.version}</span>
              <button type="button" disabled={isAdded(result.id)} onclick={() => onadd({ id: result.id, name: result.name })}>
                {isAdded(result.id) ? "Added" : "Add"}
              </button>
            </li>
          {/each}
        </ul>
      {/if}
    {/if}
  {:else}
    {#if error}<p class="error" role="alert">{error}</p>{/if}
    <ul>
      {#each popular as item (item.alias)}
        <li>
          <span class="name">{item.name}</span>
          <code>{item.id}</code>
          <span class="muted small">{item.category}</span>
          <button type="button" disabled={isAdded(item.id)} onclick={() => onadd({ id: item.id, name: item.name })}>
            {isAdded(item.id) ? "Added" : "Add"}
          </button>
        </li>
      {/each}
    </ul>
  {/if}
</div>

<style>
  .picker {
    display: flex;
    flex-direction: column;
    gap: 0.4rem;
    min-height: 0;
  }

  ul {
    list-style: none;
    margin: 0;
    padding: 0;
    overflow-y: auto;
    max-height: 22rem;
  }

  li {
    display: grid;
    grid-template-columns: 1fr auto auto auto;
    align-items: center;
    gap: 0.5rem;
    padding: 0.3rem 0;
    border-bottom: 1px solid var(--border);
  }

  .name {
    font-weight: 600;
  }

  code {
    font-size: 0.8rem;
    color: var(--muted);
  }

  .small {
    font-size: 0.8rem;
    margin: 0;
  }

  button {
    padding: 0.2rem 0.7rem !important;
  }
</style>
