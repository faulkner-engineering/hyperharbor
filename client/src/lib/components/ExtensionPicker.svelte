<script lang="ts">
  import {
    errorMessage,
    resolveExtension,
    type ExtensionCatalogItem,
    type HostEntry,
    type ProfileItem,
  } from "$lib/api/client";
  import { SvelteMap } from "svelte/reactivity";
  import { addItem, removeItem } from "$lib/setupProfiles";

  interface Props {
    host: HostEntry;
    catalog: ExtensionCatalogItem[];
    /** The profile's browser.extensions list. */
    items: ProfileItem[];
    /** Whether the chosen browser is Edge, which also takes Edge Add-ons ids. */
    edge: boolean;
  }

  let { host, catalog, items = $bindable(), edge }: Props = $props();

  let pasted = $state("");
  let filter = $state("");
  let resolving = $state(false);
  let error = $state<string | null>(null);
  const icons = new SvelteMap<string, string>();

  const shown = $derived(
    catalog.filter(
      (entry) =>
        (edge || !entry.id.startsWith("edge:")) &&
        (filter.trim() === "" ||
          `${entry.name} ${entry.description} ${entry.category}`.toLowerCase().includes(filter.trim().toLowerCase())),
    ),
  );
  const has = (id: string) => items.some((item) => item.id === id);

  async function addPasted(event: SubmitEvent) {
    event.preventDefault();
    const input = pasted.trim();
    if (!input) return;
    resolving = true;
    error = null;
    try {
      const found = await resolveExtension(host.key, input);
      if (found.store === "edge" && !edge) {
        error = `${found.name} is from the Edge Add-ons store, so it works only in Microsoft Edge. Look for it in the Chrome Web Store instead.`;
        return;
      }

      if (found.iconDataUrl) icons.set(found.profileId, found.iconDataUrl);
      items = addItem(items, { id: found.profileId, name: found.name });
      pasted = "";
    } catch (e) {
      error = errorMessage(e);
    } finally {
      resolving = false;
    }
  }
</script>

<div class="extensions">
  <form class="paste" onsubmit={addPasted}>
    <input
      aria-label="Extension link or id"
      placeholder="Paste a Chrome Web Store or Edge Add-ons link, or an extension id"
      bind:value={pasted}
    />
    <button type="submit" disabled={resolving || pasted.trim() === ""}>{resolving ? "Looking up…" : "Add"}</button>
  </form>
  {#if error}<p class="error" role="alert">{error}</p>{/if}

  {#if items.length > 0}
    <ul class="chosen">
      {#each items as item (item.id)}
        <li>
          {#if icons.get(item.id)}<img src={icons.get(item.id)} alt="" />{:else}<span class="placeholder" aria-hidden="true"></span>{/if}
          <span>{item.name ?? item.id}</span>
          <code>{item.id}</code>
          <button type="button" onclick={() => (items = removeItem(items, item.id))}>Remove</button>
        </li>
      {/each}
    </ul>
  {/if}

  <input type="search" aria-label="Search the extension catalog" placeholder="Search the catalog" bind:value={filter} />
  <ul class="catalog">
    {#each shown as entry (entry.id)}
      <li>
        <span><strong>{entry.name}</strong> <span class="muted small">{entry.description}</span></span>
        <button type="button" disabled={has(entry.id)} onclick={() => (items = addItem(items, { id: entry.id, name: entry.name }))}>
          {has(entry.id) ? "Added" : "Add"}
        </button>
      </li>
    {/each}
  </ul>
</div>

<style>
  .extensions {
    display: flex;
    flex-direction: column;
    gap: 0.4rem;
  }

  .paste {
    flex-direction: row !important;
  }

  .paste input {
    flex: 1;
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
    padding: 0.25rem 0;
    border-bottom: 1px solid var(--border);
  }

  li > span:not(.placeholder) {
    flex: 1;
  }

  .catalog {
    max-height: 14rem;
    overflow-y: auto;
  }

  img,
  .placeholder {
    width: 20px;
    height: 20px;
    border-radius: 4px;
    background: var(--idle-bg);
  }

  code {
    font-size: 0.75rem;
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
