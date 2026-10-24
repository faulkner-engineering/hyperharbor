<script lang="ts">
  import {
    errorMessage,
    openStoreSearch,
    resolveExtension,
    type ExtensionCatalogItem,
    type HostEntry,
    type ProfileItem,
    type SetupProfileCatalog,
  } from "$lib/api/client";
  import type { BrowseEntry, Detail } from "$lib/catalogBrowser";
  import { addItem, policyValue, removeItem } from "$lib/setupProfiles";
  import type { SvelteMap } from "svelte/reactivity";
  import CatalogBrowser from "./CatalogBrowser.svelte";
  import ProfileChips from "./ProfileChips.svelte";
  import Tabs from "./Tabs.svelte";

  type Policy = SetupProfileCatalog["policies"][number];
  type BrowserCatalogItem = SetupProfileCatalog["browsers"][number];

  interface Props {
    host: HostEntry;
    browsers: BrowserCatalogItem[];
    /** The chosen browser, or null for none. */
    browser: BrowserCatalogItem | null;
    browserPolicies: Policy[];
    extensionCatalog: ExtensionCatalogItem[];
    /** The chosen browser's winget id, or "" for none. */
    browserApp: string;
    extensions: ProfileItem[];
    policies: SvelteMap<string, string>;
    selected: string | null;
    onselect: (detail: Detail) => void;
  }

  let {
    host,
    browsers,
    browser,
    browserPolicies,
    extensionCatalog,
    browserApp = $bindable(),
    extensions = $bindable(),
    policies,
    selected,
    onselect,
  }: Props = $props();

  let view = $state("extensions");
  let resolving = $state(false);
  let error = $state<string | null>(null);

  const edge = $derived(browser?.id === "edge");
  const entries = $derived<BrowseEntry[]>(
    extensionCatalog
      .filter((entry) => edge || !entry.id.startsWith("edge:"))
      .map((entry) => ({ key: entry.id, name: entry.name, category: entry.category, description: entry.description })),
  );
  const has = (id: string) => extensions.some((item) => item.id === id);
  const looksLikeExtension = (text: string) => /^(https?:\/\/|edge:)?[a-p]{32}$/.test(text) || /^https?:\/\//.test(text);

  function setIn(id: string, name: string | undefined, on: boolean) {
    extensions = on ? addItem(extensions, { id, name }) : removeItem(extensions, id);
  }

  function detailFor(id: string, name?: string): Detail {
    const entry = extensionCatalog.find((item) => item.id === id);
    return {
      key: id,
      title: entry?.name ?? name ?? id,
      code: id,
      category: entry?.category,
      description: entry?.description ?? "Added from its store page.",
      note: entry?.edgeId ? `Also in the Edge Add-ons store as ${entry.edgeId}.` : null,
      isOn: () => has(id),
      toggle: () => setIn(id, entry?.name ?? name, !has(id)),
      onLabel: "Remove from profile",
      offLabel: "Add to profile",
    };
  }

  function policyDetail(policy: Policy): Detail {
    return {
      key: `policy:${policy.key}`,
      title: policy.name,
      code: policy.key,
      description: policy.description,
      isOn: () => policies.has(policy.key),
      toggle: () => togglePolicy(policy.key, policy.type, !policies.has(policy.key)),
      onLabel: "Stop setting it",
      offLabel: "Set this policy",
    };
  }

  async function lookUp(input: string) {
    resolving = true;
    error = null;
    try {
      const found = await resolveExtension(host.key, input);
      if (found.store === "edge" && !edge) {
        error = `${found.name} is from the Edge Add-ons store, so it works only in Microsoft Edge. Look for it in the Chrome Web Store instead.`;
        return;
      }
      setIn(found.profileId, found.name, true);
    } catch (e) {
      error = errorMessage(e);
    } finally {
      resolving = false;
    }
  }

  async function searchStore(store: "chrome" | "edge", query: string) {
    error = null;
    try {
      await openStoreSearch(store, query);
    } catch (e) {
      error = errorMessage(e);
    }
  }

  function togglePolicy(key: string, type: string, on: boolean) {
    if (on) policies.set(key, type === "boolean" ? "true" : "");
    else policies.delete(key);
  }
</script>

<div class="browsers" role="radiogroup" aria-label="Browser">
  <button type="button" role="radio" aria-checked={browserApp === ""} class:active={browserApp === ""} onclick={() => (browserApp = "")}>None</button>
  {#each browsers as item (item.id)}
    <button type="button" role="radio" aria-checked={browser?.id === item.id} class:active={browser?.id === item.id} onclick={() => (browserApp = item.wingetId)}>
      {item.name}
    </button>
  {/each}
</div>

{#if !browser}
  <p class="muted">Choose a browser to install it with extensions and policies.</p>
{:else}
  <Tabs
    label="Browser settings"
    tabs={[
      { id: "extensions", label: `Extensions (${extensions.length})` },
      { id: "policies", label: `Policies (${policies.size})` },
    ]}
    bind:selected={view}
  />
  {#if view === "extensions"}
    <ProfileChips
      label="Extensions in this profile"
      items={extensions.map((item) => ({ key: item.id, label: item.name ?? item.id }))}
      onremove={(key) => setIn(key, undefined, false)}
      onselect={(key) => onselect(detailFor(key, extensions.find((item) => item.id === key)?.name))}
      empty="Nothing yet. Pick from the catalog, or paste a store link into the filter."
    />
    {#if error}<p class="error" role="alert">{error}</p>{/if}
    <CatalogBrowser
      label="Extension catalog"
      placeholder="Filter extensions, or paste a store link or id"
      {entries}
      isOn={has}
      ontoggle={(entry, on) => setIn(entry.key, entry.name, on)}
      onselect={(entry) => onselect(detailFor(entry.key, entry.name))}
      {selected}
    >
      {#snippet footer(query)}
        {#if looksLikeExtension(query)}
          <div class="beyond">
            <button type="button" onclick={() => lookUp(query)} disabled={resolving}>{resolving ? "Looking up…" : "Look up this extension"}</button>
          </div>
        {:else if query.length > 0}
          <div class="beyond">
            <div class="caption">Find more in the store</div>
            <div class="actions-row">
              <button type="button" onclick={() => searchStore("chrome", query)}>Search the Chrome Web Store for "{query}"</button>
              {#if edge}
                <button type="button" onclick={() => searchStore("edge", query)}>Search Edge Add-ons for "{query}"</button>
              {/if}
            </div>
            <p class="muted hint">The store opens in your browser. Copy the extension's address from there and paste it into the filter above.</p>
          </div>
        {/if}
      {/snippet}
    </CatalogBrowser>
  {:else}
    <ul class="policies" aria-label="Policies">
      {#each browserPolicies as policy (policy.key)}
        {@const on = policies.has(policy.key)}
        <li class:selected={selected === `policy:${policy.key}`}>
          <div class="policy-row">
            <button type="button" class="pick" onclick={() => onselect(policyDetail(policy))}>
              <span class="name" class:on>{policy.name}</span>
              {#if policy.description}<span class="description">{policy.description}</span>{/if}
            </button>
            <button type="button" aria-label="{on ? 'Stop setting' : 'Set'} {policy.name}" onclick={() => togglePolicy(policy.key, policy.type, !on)}>
              {on ? "Remove" : "Set"}
            </button>
          </div>
          {#if on}
            {#if policy.type === "boolean"}
              <select aria-label={policy.name} value={policies.get(policy.key)} onchange={(event) => policies.set(policy.key, policyValue("boolean", event.currentTarget.value))}>
                <option value="true">On</option>
                <option value="false">Off</option>
              </select>
            {:else if policy.values}
              <select aria-label={policy.name} value={policies.get(policy.key)} onchange={(event) => policies.set(policy.key, event.currentTarget.value)}>
                <option value="" disabled>Choose…</option>
                {#each Object.entries(policy.values) as [value, label] (value)}<option {value}>{label}</option>{/each}
              </select>
            {:else}
              <input
                aria-label={policy.name}
                type={policy.type === "integer" ? "number" : "text"}
                value={policies.get(policy.key)}
                oninput={(event) => policies.set(policy.key, policyValue(policy.type, event.currentTarget.value))}
              />
            {/if}
          {/if}
        </li>
      {/each}
    </ul>
  {/if}
{/if}

<style>
  .browsers {
    display: flex;
    flex-wrap: wrap;
    gap: 0.35rem;
    margin-bottom: 0.75rem;
  }

  .browsers button.active {
    border-color: var(--accent) !important;
    background: color-mix(in srgb, var(--accent) 14%, transparent);
    color: var(--accent);
  }

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

  .hint {
    margin: 0.45rem 0 0;
    font-size: 0.8rem;
  }

  .policies {
    list-style: none;
    margin: 0;
    padding: 0;
  }

  .policies li {
    display: flex;
    flex-direction: column;
    gap: 0.3rem;
    padding-bottom: 0.4rem;
    border-bottom: 1px solid var(--border);
  }

  .policies li.selected {
    background: color-mix(in srgb, var(--accent) 8%, transparent);
    box-shadow: inset 2px 0 0 var(--accent);
  }

  .policy-row {
    display: flex;
    align-items: center;
    gap: 0.5rem;
  }

  .pick {
    flex: 1;
    min-width: 0;
    display: flex;
    flex-direction: column;
    align-items: flex-start;
    padding: 0.4rem !important;
    border: none !important;
    background: transparent !important;
    text-align: left;
  }

  .name {
    font-weight: 600;
  }

  .name.on {
    color: var(--accent);
  }

  .description {
    font-size: 0.8rem;
    color: var(--muted);
  }

  .policies select,
  .policies input {
    margin: 0 0.4rem;
  }
</style>
