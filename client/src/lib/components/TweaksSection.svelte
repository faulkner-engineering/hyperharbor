<script lang="ts">
  import type { RegistryTweak, TweakCatalogItem } from "$lib/api/client";
  import type { BrowseEntry, Detail } from "$lib/catalogBrowser";
  import CatalogBrowser from "./CatalogBrowser.svelte";
  import ProfileChips from "./ProfileChips.svelte";

  interface Props {
    catalog: TweakCatalogItem[];
    tweakIds: string[];
    custom: { registry: RegistryTweak; name?: string }[];
    selected: string | null;
    onselect: (detail: Detail) => void;
  }

  let { catalog, tweakIds = $bindable(), custom = $bindable(), selected, onselect }: Props = $props();

  let newKey = $state("HKCU\\");
  let newName = $state("");
  let newType = $state<RegistryTweak["type"]>("dword");
  let newValue = $state("");

  const entries = $derived<BrowseEntry[]>(
    catalog.map((tweak) => ({ key: tweak.id, name: tweak.name, category: tweak.category, description: tweak.note })),
  );
  const has = (id: string) => tweakIds.includes(id);
  const customKey = (index: number) => `custom:${index}`;
  const customLabel = (registry: RegistryTweak) => `${registry.key.split("\\").at(-1)}\\${registry.name || "(default)"} = ${registry.value}`;

  function setIn(id: string, on: boolean) {
    tweakIds = on ? [...tweakIds, id] : tweakIds.filter((item) => item !== id);
  }

  function detailFor(id: string): Detail {
    const tweak = catalog.find((item) => item.id === id);
    return {
      key: id,
      title: tweak?.name ?? id,
      category: tweak?.category,
      description: tweak?.note,
      values: tweak?.values ?? [],
      isOn: () => has(id),
      toggle: () => setIn(id, !has(id)),
      onLabel: "Turn off",
      offLabel: "Turn on",
    };
  }

  function addCustom(event: SubmitEvent) {
    event.preventDefault();
    custom = [...custom, { registry: { key: newKey.trim(), name: newName, type: newType, value: newValue.trim() } }];
    newName = "";
    newValue = "";
  }

  function remove(key: string) {
    if (key.startsWith("custom:")) {
      const index = Number(key.slice("custom:".length));
      custom = custom.filter((_, i) => i !== index);
    } else {
      setIn(key, false);
    }
  }
</script>

<ProfileChips
  label="Turned on in this profile"
  items={[
    ...tweakIds.map((id) => ({ key: id, label: catalog.find((tweak) => tweak.id === id)?.name ?? id })),
    ...custom.map((item, index) => ({ key: customKey(index), label: item.name ?? customLabel(item.registry) })),
  ]}
  onremove={remove}
  onselect={(key) => {
    if (!key.startsWith("custom:")) onselect(detailFor(key));
  }}
  empty="Nothing yet. Select a tweak to see exactly what it changes."
/>

<CatalogBrowser
  label="Tweaks"
  placeholder="Filter tweaks"
  {entries}
  isOn={has}
  ontoggle={(entry, on) => setIn(entry.key, on)}
  onselect={(entry) => onselect(detailFor(entry.key))}
  {selected}
  onLabel="Turn off"
  offLabel="Turn on"
/>

<details class="advanced">
  <summary>Custom registry value</summary>
  <form class="inline" onsubmit={addCustom}>
    <input aria-label="Registry key" bind:value={newKey} placeholder="HKCU\Software\..." />
    <input aria-label="Value name" bind:value={newName} placeholder="Value name" />
    <select aria-label="Value type" bind:value={newType}>
      <option value="dword">DWORD</option>
      <option value="qword">QWORD</option>
      <option value="string">String</option>
    </select>
    <input aria-label="Value" bind:value={newValue} placeholder="Value" />
    <button type="submit" disabled={newKey.trim().length < 6}>Add</button>
  </form>
</details>

<style>
  .advanced {
    margin-top: 1rem;
    padding-top: 0.6rem;
    border-top: 1px solid var(--border);
  }

  summary {
    cursor: pointer;
    font-weight: 600;
    font-size: 0.9rem;
    margin-bottom: 0.5rem;
  }

  .inline {
    flex-direction: row !important;
    flex-wrap: wrap;
  }

  .inline input {
    flex: 1;
    min-width: 8rem;
  }
</style>
