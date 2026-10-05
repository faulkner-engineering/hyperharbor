<script lang="ts">
  import {
    errorMessage,
    getSetupProfileCatalog,
    isClientError,
    listExtensionCatalog,
    saveSetupProfile,
    type ExtensionCatalogItem,
    type HostEntry,
    type ProfileItem,
    type RegistryTweak,
    type SetupProfile,
    type SetupProfileCatalog,
    type StoredSetupProfile,
    type ValidationIssue,
    type Vm,
  } from "$lib/api/client";
  import { ElevationCancelled, withElevation } from "$lib/lifecycle.svelte";
  import { addItem, policyValue, removeItem } from "$lib/setupProfiles";
  import { onMount, untrack } from "svelte";
  import { SvelteMap } from "svelte/reactivity";
  import AppxChecklist from "./AppxChecklist.svelte";
  import Dialog from "./Dialog.svelte";
  import ExtensionPicker from "./ExtensionPicker.svelte";
  import PackagePicker from "./PackagePicker.svelte";
  import Tabs from "./Tabs.svelte";

  interface Props {
    host: HostEntry;
    /** The profile being edited, or null for a new one. */
    profileId: string | null;
    initial: SetupProfile;
    /** Running Windows VMs, for reading provisioned packages. */
    vms: Vm[];
    onclose: (saved: StoredSetupProfile | null) => void;
  }

  let { host, profileId, initial, vms, onclose }: Props = $props();

  const start = untrack(() => initial);
  let name = $state(start.name);
  let description = $state(start.description ?? "");
  let install = $state<ProfileItem[]>(start.install ?? []);
  let appx = $state<ProfileItem[]>(start.remove?.appx ?? []);
  let capabilities = $state<ProfileItem[]>(start.remove?.capabilities ?? []);
  let features = $state<ProfileItem[]>(start.remove?.features ?? []);
  let tweakIds = $state<string[]>((start.tweaks ?? []).flatMap((tweak) => (tweak.id ? [tweak.id] : [])));
  let custom = $state<{ registry: RegistryTweak; name?: string }[]>(
    (start.tweaks ?? []).flatMap((tweak) => (tweak.registry ? [{ registry: tweak.registry, name: tweak.name }] : [])),
  );
  let browserApp = $state(start.browser?.app.id ?? "");
  let extensions = $state<ProfileItem[]>(start.browser?.extensions ?? []);
  const policies = new SvelteMap<string, string>(Object.entries(start.browser?.policies ?? {}));

  let tab = $state("packages");
  let catalog = $state<SetupProfileCatalog | null>(null);
  let extensionCatalog = $state<ExtensionCatalogItem[]>([]);
  let busy = $state(false);
  let error = $state<string | null>(null);
  let issues = $state<ValidationIssue[]>([]);

  let byId = $state("");
  let capability = $state("");
  let feature = $state("");
  let newKey = $state("HKCU\\");
  let newName = $state("");
  let newType = $state<RegistryTweak["type"]>("dword");
  let newValue = $state("");

  const browser = $derived(catalog?.browsers.find((item) => item.wingetId === browserApp || item.alias === browserApp) ?? null);
  const browserPolicies = $derived(browser ? (catalog?.policies ?? []).filter((policy) => policy.appliesTo.includes(browser.id)) : []);
  const tweakGroups = $derived(
    Object.entries(
      (catalog?.tweaks ?? []).reduce<Record<string, NonNullable<typeof catalog>["tweaks"]>>((groups, tweak) => {
        (groups[tweak.category] ??= []).push(tweak);
        return groups;
      }, {}),
    ),
  );

  onMount(async () => {
    try {
      [catalog, extensionCatalog] = await Promise.all([getSetupProfileCatalog(host.key), listExtensionCatalog(host.key)]);
    } catch (e) {
      error = errorMessage(e);
    }
  });

  function addById(event: SubmitEvent) {
    event.preventDefault();
    if (byId.trim()) install = addItem(install, { id: byId.trim() });
    byId = "";
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

  function addCustom(event: SubmitEvent) {
    event.preventDefault();
    custom = [...custom, { registry: { key: newKey.trim(), name: newName, type: newType, value: newValue.trim() } }];
    newName = "";
    newValue = "";
  }

  function toggleTweak(id: string, on: boolean) {
    tweakIds = on ? [...tweakIds, id] : tweakIds.filter((item) => item !== id);
  }

  function togglePolicy(key: string, type: string, on: boolean) {
    if (on) policies.set(key, type === "boolean" ? "true" : "");
    else policies.delete(key);
  }

  function profile(): SetupProfile {
    const tweakNames = new Map((catalog?.tweaks ?? []).map((tweak) => [tweak.id, tweak.name]));
    return {
      name: name.trim(),
      description: description.trim() || undefined,
      install,
      remove: { appx, capabilities, features },
      tweaks: [
        ...tweakIds.map((id) => ({ id, name: tweakNames.get(id) })),
        ...custom.map((item) => ({ registry: item.registry, name: item.name })),
      ],
      browser: browser
        ? {
            app: { id: browser.wingetId, name: browser.name },
            extensions,
            policies: Object.fromEntries(
              [...policies].filter(([key]) => browserPolicies.some((policy) => policy.key === key)),
            ),
          }
        : undefined,
    };
  }

  async function save() {
    busy = true;
    error = null;
    issues = [];
    try {
      onclose(await withElevation(host.key, () => saveSetupProfile(host.key, profileId, profile())));
    } catch (e) {
      if (e instanceof ElevationCancelled) return;
      error = errorMessage(e);
      issues = isClientError(e) ? e.issues : [];
    } finally {
      busy = false;
    }
  }
</script>

<Dialog title={profileId ? `Edit ${start.name}` : "New setup profile"} xl>
  <div class="editor">
    <div class="header">
      <label>Name <input bind:value={name} maxlength="60" required /></label>
      <label>Description <input bind:value={description} maxlength="500" placeholder="Optional" /></label>
    </div>

    <Tabs
      label="Profile sections"
      tabs={[
        { id: "packages", label: `Install (${install.length})` },
        { id: "remove", label: `Remove (${appx.length + capabilities.length + features.length})` },
        { id: "tweaks", label: `Tweaks (${tweakIds.length + custom.length})` },
        { id: "browser", label: `Browser${browser ? ` (${extensions.length})` : ""}` },
      ]}
      bind:selected={tab}
    />

    <div class="body">
      {#if tab === "packages"}
        <div class="columns">
          <section>
            <h4>In this profile</h4>
            {#if install.length === 0}<p class="muted">Nothing yet. Add packages from the right, or by winget id.</p>{/if}
            <ul class="items">
              {#each install as item (item.id)}
                <li>
                  <span>{item.name ?? item.id}</span><code>{item.id}</code>
                  <button type="button" onclick={() => (install = removeItem(install, item.id))}>Remove</button>
                </li>
              {/each}
            </ul>
            <form class="inline" onsubmit={addById}>
              <input aria-label="winget id, Microsoft Store id, or alias" placeholder="winget id, Store id, or alias" bind:value={byId} />
              <button type="submit" disabled={byId.trim() === ""}>Add</button>
            </form>
          </section>
          <section>
            <PackagePicker {host} added={install.map((item) => item.id)} onadd={(item) => (install = addItem(install, item))} />
          </section>
        </div>
      {:else if tab === "remove"}
        <AppxChecklist {host} {vms} bind:items={appx} />
        <div class="columns">
          <section>
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
          </section>
          <section>
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
          </section>
        </div>
      {:else if tab === "tweaks"}
        <div class="tweaks">
          {#each tweakGroups as [category, tweaks] (category)}
            <fieldset>
              <legend>{category}</legend>
              {#each tweaks as tweak (tweak.id)}
                <label class="check" title={tweak.note ?? tweak.id}>
                  <input
                    type="checkbox"
                    checked={tweakIds.includes(tweak.id)}
                    onchange={(event) => toggleTweak(tweak.id, event.currentTarget.checked)}
                  />
                  {tweak.name}
                </label>
              {/each}
            </fieldset>
          {/each}
        </div>
        <h4>Custom registry values</h4>
        <ul class="items">
          {#each custom as item, index (index)}
            <li>
              <code>{item.registry.key}\{item.registry.name || "(default)"} = {item.registry.value} ({item.registry.type})</code>
              <button type="button" onclick={() => (custom = custom.filter((_, i) => i !== index))}>Remove</button>
            </li>
          {/each}
        </ul>
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
      {:else}
        <label>
          Browser
          <select bind:value={browserApp}>
            <option value="">None</option>
            {#each catalog?.browsers ?? [] as item (item.id)}<option value={item.wingetId}>{item.name}</option>{/each}
          </select>
        </label>
        {#if browser}
          <div class="columns">
            <section>
              <h4>Extensions</h4>
              <ExtensionPicker {host} catalog={extensionCatalog} edge={browser.id === "edge"} bind:items={extensions} />
            </section>
            <section>
              <h4>Policies</h4>
              {#each browserPolicies as policy (policy.key)}
                <div class="policy">
                  <label class="check" title={policy.description ?? policy.key}>
                    <input
                      type="checkbox"
                      checked={policies.has(policy.key)}
                      onchange={(event) => togglePolicy(policy.key, policy.type, event.currentTarget.checked)}
                    />
                    {policy.name}
                  </label>
                  {#if policies.has(policy.key)}
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
                </div>
              {/each}
            </section>
          </div>
        {/if}
      {/if}
    </div>

    {#if error}
      <div class="error" role="alert">
        {error}
        {#if issues.length > 0}
          <ul>{#each issues as issue (issue.field)}<li><code>{issue.field}</code> {issue.message}</li>{/each}</ul>
        {/if}
      </div>
    {/if}
    <div class="actions">
      <button type="button" onclick={() => onclose(null)} disabled={busy}>Cancel</button>
      <button type="button" class="primary" onclick={save} disabled={busy || name.trim() === ""}>{busy ? "Saving…" : "Save"}</button>
    </div>
  </div>
</Dialog>

<style>
  .editor {
    flex: 1;
    min-height: 0;
    display: flex;
    flex-direction: column;
    gap: 0.4rem;
  }

  .header {
    display: grid;
    grid-template-columns: 1fr 2fr;
    gap: 0.75rem;
  }

  .header label,
  .policy {
    display: flex;
    flex-direction: column;
    gap: 0.2rem;
  }

  .body {
    flex: 1;
    min-height: 0;
    overflow-y: auto;
  }

  .columns {
    display: grid;
    grid-template-columns: 1fr 1fr;
    gap: 1rem;
  }

  h4 {
    margin: 0.5rem 0 0.4rem;
    font-size: 0.9rem;
  }

  .items {
    list-style: none;
    margin: 0 0 0.5rem;
    padding: 0;
  }

  .items li {
    display: flex;
    align-items: center;
    gap: 0.5rem;
    padding: 0.25rem 0;
    border-bottom: 1px solid var(--border);
  }

  .items li > span {
    flex: 1;
  }

  code {
    font-size: 0.8rem;
    color: var(--muted);
  }

  .inline {
    flex-direction: row !important;
    flex-wrap: wrap;
  }

  .inline input {
    flex: 1;
    min-width: 8rem;
  }

  .tweaks {
    display: grid;
    grid-template-columns: repeat(auto-fill, minmax(16rem, 1fr));
    gap: 0.5rem;
  }

  fieldset {
    border: 1px solid var(--border);
    border-radius: 6px;
    padding: 0.4rem 0.6rem;
    margin: 0;
  }

  legend {
    font-weight: 600;
    font-size: 0.85rem;
    text-transform: capitalize;
  }

  .policy {
    margin-bottom: 0.4rem;
  }

  .items button,
  .inline button {
    padding: 0.2rem 0.7rem !important;
  }
</style>
