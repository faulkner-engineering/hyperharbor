<script lang="ts">
  import {
    errorMessage,
    hasProblemCode,
    listVmAppx,
    ProblemCodes,
    recordAppxBaseline,
    type AppxCatalogItem,
    type HostEntry,
    type ProfileItem,
    type Vm,
    type VmAppxInventory,
  } from "$lib/api/client";
  import type { BrowseEntry, Detail } from "$lib/catalogBrowser";
  import { addItem, removeItem } from "$lib/setupProfiles";
  import { toasts } from "$lib/toasts.svelte";
  import { untrack } from "svelte";
  import CatalogBrowser from "./CatalogBrowser.svelte";
  import ProfileChips from "./ProfileChips.svelte";

  interface Props {
    host: HostEntry;
    /** Running Windows VMs to read the real package list from. */
    vms: Vm[];
    /** The host's rated packages; empty from hosts before API 1.26.0. */
    catalog: AppxCatalogItem[];
    appx: ProfileItem[];
    capabilities: ProfileItem[];
    features: ProfileItem[];
    selected: string | null;
    onselect: (detail: Detail) => void;
  }

  let {
    host,
    vms,
    catalog,
    appx = $bindable(),
    capabilities = $bindable(),
    features = $bindable(),
    selected,
    onselect,
  }: Props = $props();

  const RATINGS = ["safe", "caution", "keep"];

  let vmId = $state(untrack(() => vms[0]?.id ?? ""));
  let inventory = $state<VmAppxInventory | null>(null);
  let loading = $state(false);
  let recording = $state(false);
  let error = $state<string | null>(null);
  let allowKeep = $state(false);
  let capability = $state("");
  let feature = $state("");

  interface Known {
    name: string;
    friendlyName: string;
    publisher: string;
    rating: string;
    note: string | null;
  }

  // The catalog, with what a read VM really has added; the VM's own rating wins.
  const known = $derived.by(() => {
    const all = new Map<string, Known>(catalog.map((item) => [item.name, item]));
    for (const item of inventory?.packages ?? []) all.set(item.name, item);
    return all;
  });
  const inVm = $derived(new Set((inventory?.packages ?? []).map((item) => item.name)));
  const entries = $derived<BrowseEntry[]>(
    [...known.values()]
      .sort((a, b) => RATINGS.indexOf(a.rating) - RATINGS.indexOf(b.rating) || a.friendlyName.localeCompare(b.friendlyName))
      .map((item) => ({
        key: item.name,
        name: item.friendlyName,
        category: item.rating,
        description: `${item.publisher}${inventory ? (inVm.has(item.name) ? " · in this VM" : " · not in this VM") : ""}${item.note ? ` · ${item.note}` : ""}`,
        keywords: item.name,
      })),
  );
  const has = (name: string) => appx.some((item) => item.id === name);
  const locked = (entry: BrowseEntry) => entry.category === "keep" && !allowKeep;

  function setIn(name: string, on: boolean) {
    appx = on ? addItem(appx, { id: name, name: known.get(name)?.friendlyName }) : removeItem(appx, name);
  }

  function detailFor(name: string): Detail {
    const item = known.get(name);
    return {
      key: name,
      title: item?.friendlyName ?? appx.find((entry) => entry.id === name)?.name ?? name,
      code: name,
      badge: item?.rating,
      description: item ? `Published by ${item.publisher}.${inventory ? (inVm.has(name) ? " This VM has it." : " This VM does not have it.") : ""}` : "Not in HyperHarbor's catalog.",
      note: item?.rating === "keep" ? `${item.note ?? "Rated keep: removing it can break Windows features."}` : item?.note,
      isOn: () => has(name),
      toggle: item?.rating === "keep" && !allowKeep && !has(name) ? null : () => setIn(name, !has(name)),
      onLabel: "Keep it installed",
      offLabel: "Remove it",
    };
  }

  function addAllSafe() {
    for (const entry of entries) if (entry.category === "safe") appx = addItem(appx, { id: entry.key, name: entry.name });
  }

  async function read() {
    if (!vmId) return;
    loading = true;
    error = null;
    try {
      inventory = await listVmAppx(host.key, vmId);
    } catch (e) {
      inventory = null;
      error = hasProblemCode(e, ProblemCodes.credentialRequired)
        ? "HyperHarbor has no administrator credential for this VM. Set it up for Remote Desktop first."
        : errorMessage(e);
    } finally {
      loading = false;
    }
  }

  async function recordBaseline() {
    recording = true;
    try {
      const baseline = await recordAppxBaseline(host.key, vmId);
      toasts.show(`Recorded the clean baseline for ${baseline.key}.`);
      await read();
    } catch (e) {
      toasts.error(errorMessage(e));
    } finally {
      recording = false;
    }
  }

  function addCapability(event: SubmitEvent) {
    event.preventDefault();
    if (capability.trim()) capabilities = addItem(capabilities, { id: capability.trim() });
    capability = "";
  }

  function addFeature(event: SubmitEvent) {
    event.preventDefault();
    if (feature.trim()) features = addItem(features, { id: feature.trim() });
    feature = "";
  }
</script>

<ProfileChips
  label="Removed by this profile"
  items={appx.map((item) => ({ key: item.id, label: item.name ?? item.id }))}
  onremove={(key) => (appx = removeItem(appx, key))}
  onselect={(key) => onselect(detailFor(key))}
  empty="Nothing yet. Safe apps are a good start."
/>

{#if vms.length > 0}
  <div class="vm-row">
    <label for="remove-vm">Compare with</label>
    <select id="remove-vm" bind:value={vmId}>
      {#each vms as vm (vm.id)}<option value={vm.id}>{vm.name}</option>{/each}
    </select>
    <button type="button" onclick={read} disabled={loading}>{loading ? "Reading…" : inventory ? "Read again" : "Read its apps"}</button>
  </div>
{/if}
{#if error}<p class="error" role="alert">{error}</p>{/if}
{#if inventory}
  <p class="muted">
    Windows {inventory.build} {inventory.edition}.
    {#if inventory.baseline}
      Compared with the clean baseline{inventory.baseline.approximate ? ` for ${inventory.baseline.edition}` : ""}.
    {:else}
      No clean baseline for this build yet.
      <button type="button" class="link" onclick={recordBaseline} disabled={recording}>Record this VM as clean</button>
    {/if}
  </p>
{/if}

<CatalogBrowser
  label="Built-in apps"
  placeholder="Filter built-in apps"
  {entries}
  isOn={has}
  ontoggle={(entry, on) => setIn(entry.key, on)}
  onselect={(entry) => onselect(detailFor(entry.key))}
  {selected}
  onLabel="Undo"
  offLabel="Remove"
  badge={(entry) => entry.category}
  disabled={locked}
  order={RATINGS}
>
  {#snippet toolbar()}
    <div class="tools">
      <button type="button" onclick={addAllSafe}>Remove all safe</button>
      <label class="check"><input type="checkbox" bind:checked={allowKeep} /> Allow removing apps rated keep</label>
    </div>
  {/snippet}
</CatalogBrowser>
{#if entries.length === 0}
  <p class="muted">This host does not list built-in apps yet. Read them from a running VM above.</p>
{/if}

<details class="advanced">
  <summary>Windows capabilities and optional features <span class="n">{capabilities.length + features.length}</span></summary>
  <h4>Windows capabilities</h4>
  <ul class="items">
    {#each capabilities as item (item.id)}
      <li><code>{item.id}</code><button type="button" onclick={() => (capabilities = removeItem(capabilities, item.id))}>Remove</button></li>
    {/each}
  </ul>
  <form class="inline" onsubmit={addCapability}>
    <input aria-label="Capability name" placeholder="For example Browser.InternetExplorer~~~~0.0.11.0" bind:value={capability} />
    <button type="submit" disabled={capability.trim() === ""}>Add</button>
  </form>
  <h4>Optional features</h4>
  <ul class="items">
    {#each features as item (item.id)}
      <li><code>{item.id}</code><button type="button" onclick={() => (features = removeItem(features, item.id))}>Remove</button></li>
    {/each}
  </ul>
  <form class="inline" onsubmit={addFeature}>
    <input aria-label="Feature name" placeholder="For example MicrosoftWindowsPowerShellV2Root" bind:value={feature} />
    <button type="submit" disabled={feature.trim() === ""}>Add</button>
  </form>
</details>

<style>
  .vm-row,
  .tools {
    display: flex;
    align-items: center;
    flex-wrap: wrap;
    gap: 0.5rem;
  }

  .vm-row {
    margin-bottom: 0.4rem;
  }

  .tools button {
    padding: 0.2rem 0.7rem !important;
  }

  .advanced {
    margin-top: 1rem;
    padding-top: 0.6rem;
    border-top: 1px solid var(--border);
  }

  summary {
    cursor: pointer;
    font-weight: 600;
    font-size: 0.9rem;
  }

  .n {
    margin-left: 0.3rem;
    font-family: ui-monospace, "Cascadia Mono", Consolas, monospace;
    font-size: 0.75rem;
    color: var(--accent);
  }

  h4 {
    margin: 0.6rem 0 0.3rem;
    font-size: 0.85rem;
  }

  .items {
    list-style: none;
    margin: 0 0 0.4rem;
    padding: 0;
  }

  .items li {
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 0.5rem;
    padding: 0.2rem 0;
    border-bottom: 1px solid var(--border);
  }

  code {
    font-size: 0.8rem;
    color: var(--muted);
  }

  .inline {
    flex-direction: row !important;
  }

  .inline input {
    flex: 1;
  }

  .link {
    padding: 0 !important;
    border: none !important;
    background: none !important;
    color: var(--accent) !important;
    text-decoration: underline;
  }
</style>
