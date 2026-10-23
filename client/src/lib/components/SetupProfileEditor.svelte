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
  import type { PaletteName } from "$lib/machine/assets";
  import { prefersReducedMotion } from "$lib/machine/fallback";
  import AppxChecklist from "./AppxChecklist.svelte";
  import Dialog from "./Dialog.svelte";
  import ExtensionPicker from "./ExtensionPicker.svelte";
  import MachineView, { type MachineStatus, type MotionSetting } from "./MachineView.svelte";
  import PackagePicker from "./PackagePicker.svelte";

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

  const PALETTE_KEY = "hh.machinePalette";
  const MOTION_KEY = "hh.machineMotion";

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
  let palette = $state<PaletteName>(stored(PALETTE_KEY) === "green" ? "green" : "amber");
  let motion = $state<MotionSetting>(storedMotion());
  let machineStatus = $state<MachineStatus>("loading");
  const motionOn = $derived(motion === "system" ? !prefersReducedMotion() : motion === "on");

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
  const removals = $derived(appx.length + capabilities.length + features.length);
  const tweakCount = $derived(tweakIds.length + custom.length);
  /** The section list, which drives both the machine (section is a key of targets.json) and the form. */
  const sections = $derived([
    { id: "packages", label: "Install", hint: "Drive bay: one drive per app", section: "packages", count: install.length },
    { id: "remove", label: "Remove", hint: "Drive bay: debloat", section: "debloat", count: removals },
    { id: "tweaks", label: "Tweaks", hint: "The whole machine", section: "overview", count: tweakCount },
    { id: "browser", label: "Browser", hint: "Screen: tabs", section: "browser", count: browserApp ? extensions.length : null },
  ]);
  const current = $derived(sections.find((item) => item.id === tab) ?? sections[0]);
  const hud = $derived(
    [
      current.label.toUpperCase(),
      tab === "packages"
        ? `${plural(install.length, "package")}, ${Math.min(install.length, 4)} of 4 drives lit`
        : tab === "remove"
          ? plural(removals, "removal")
          : tab === "tweaks"
            ? plural(tweakCount, "tweak")
            : browser
              ? `${browser.name}, ${plural(extensions.length, "extension")}`
              : "no browser",
      machineStatus === "still-no-webgl"
        ? "still: no WebGL"
        : machineStatus === "still-slow"
          ? "still: low frame rate"
          : machineStatus === "still-error"
            ? "still: the 3D view did not start"
            : null,
    ]
      .filter(Boolean)
      .join(" · "),
  );

  const browserPolicies = $derived(browser ? (catalog?.policies ?? []).filter((policy) => policy.appliesTo.includes(browser.id)) : []);
  const tweakGroups = $derived(
    Object.entries(
      (catalog?.tweaks ?? []).reduce<Record<string, NonNullable<typeof catalog>["tweaks"]>>((groups, tweak) => {
        (groups[tweak.category] ??= []).push(tweak);
        return groups;
      }, {}),
    ),
  );

  function plural(count: number, noun: string): string {
    return `${count} ${noun}${count === 1 ? "" : "s"}`;
  }

  function stored(key: string): string | null {
    try {
      return localStorage.getItem(key);
    } catch {
      return null;
    }
  }

  function store(key: string, value: string) {
    try {
      localStorage.setItem(key, value);
    } catch {
      // Per-device conveniences; the defaults are fine without storage.
    }
  }

  function storedMotion(): MotionSetting {
    const value = stored(MOTION_KEY);
    return value === "on" || value === "off" ? value : "system";
  }

  function setPalette(green: boolean) {
    palette = green ? "green" : "amber";
    store(PALETTE_KEY, palette);
  }

  function setMotion(on: boolean) {
    motion = on ? "on" : "off";
    store(MOTION_KEY, motion);
  }

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

<Dialog title={profileId ? `Edit ${start.name}` : "New setup profile"} terminal>
  <div class="editor" data-palette={palette}>
    <div class="header">
      <label>Name <input bind:value={name} maxlength="60" required /></label>
      <label>Description <input bind:value={description} maxlength="500" placeholder="Optional" /></label>
    </div>

    <div class="workspace">
      <nav class="sections">
        <div class="caps" id="profile-sections">Profile sections</div>
        <div role="tablist" aria-labelledby="profile-sections" aria-orientation="vertical">
          {#each sections as item (item.id)}
            <button
              type="button"
              role="tab"
              id="section-{item.id}"
              aria-selected={tab === item.id}
              aria-controls="section-panel"
              class:active={tab === item.id}
              onclick={() => (tab = item.id)}
            >
              <span class="label">{item.label}{#if item.count !== null}<span class="count">{item.count}</span>{/if}</span>
              <small>{item.hint}</small>
            </button>
          {/each}
        </div>
        <div class="view-options">
          <label class="check"><input type="checkbox" checked={palette === "green"} onchange={(event) => setPalette(event.currentTarget.checked)} /> Green terminal</label>
          <label class="check" title="Starts from the Windows animation effects setting">
            <input type="checkbox" checked={motionOn} onchange={(event) => setMotion(event.currentTarget.checked)} /> Motion
          </label>
        </div>
      </nav>

      <div class="stage">
        <MachineView section={current.section} controls={{ items: install.length }} {palette} {motion} bind:status={machineStatus} />
        <div class="hud" aria-live="polite">{hud}</div>
      </div>

      <div class="panel" id="section-panel" role="tabpanel" aria-labelledby="section-{tab}">
        <div class="caps">{current.label}</div>
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
      </div>
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
    --accent: var(--hh-crt-amber-glow);
    flex: 1;
    min-height: 0;
    display: flex;
    flex-direction: column;
    gap: 0.4rem;
  }

  .editor[data-palette="green"] {
    --accent: var(--hh-crt-green-glow);
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

  .workspace {
    flex: 1;
    min-height: 0;
    display: grid;
    grid-template-columns: 13rem minmax(0, 1fr) minmax(24rem, 34%);
    border: 1px solid var(--border);
    border-radius: 8px;
    overflow: hidden;
  }

  .caps,
  .hud,
  .count {
    font-family: ui-monospace, "Cascadia Mono", Consolas, monospace;
  }

  .caps {
    font-size: 0.75rem;
    letter-spacing: 0.12em;
    text-transform: uppercase;
    color: var(--muted);
    margin: 0 0 0.6rem;
  }

  .sections {
    display: flex;
    flex-direction: column;
    padding: 0.9rem 0.75rem;
    border-right: 1px solid var(--border);
    background: var(--surface);
    overflow-y: auto;
  }

  .sections [role="tablist"] {
    display: flex;
    flex-direction: column;
    gap: 0.4rem;
  }

  .sections [role="tab"] {
    display: block;
    width: 100%;
    text-align: left;
    padding: 0.55rem 0.7rem;
    border-radius: 8px;
    background: transparent;
  }

  .sections [role="tab"]:hover,
  .sections [role="tab"].active {
    border-color: var(--accent);
  }

  .sections [role="tab"].active {
    background: color-mix(in srgb, var(--accent) 10%, transparent);
  }

  .label {
    display: flex;
    justify-content: space-between;
    align-items: baseline;
  }

  .count {
    font-size: 0.8rem;
    color: var(--accent);
  }

  .sections small {
    display: block;
    color: var(--muted);
    font-size: 0.75rem;
  }

  .view-options {
    margin-top: auto;
    padding-top: 0.75rem;
  }

  .stage {
    position: relative;
    display: grid;
    min-height: 0;
  }

  .hud {
    position: absolute;
    left: 0.9rem;
    bottom: 0.7rem;
    font-size: 0.75rem;
    letter-spacing: 0.06em;
    color: var(--accent);
    text-shadow: 0 0 6px color-mix(in srgb, var(--accent) 60%, transparent);
    pointer-events: none;
  }

  .panel {
    display: flex;
    flex-direction: column;
    min-height: 0;
    padding: 0.9rem 1rem;
    border-left: 1px solid var(--border);
    background: var(--surface);
  }

  .body {
    flex: 1;
    min-height: 0;
    overflow-y: auto;
  }

  .columns {
    display: grid;
    grid-template-columns: repeat(auto-fit, minmax(15rem, 1fr));
    gap: 1rem;
  }

  @media (max-width: 900px) {
    .workspace {
      grid-template-columns: 1fr;
      grid-template-rows: auto 12rem 1fr;
    }

    .sections [role="tablist"] {
      flex-direction: row;
      flex-wrap: wrap;
    }

    .sections [role="tab"] {
      width: auto;
    }
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
