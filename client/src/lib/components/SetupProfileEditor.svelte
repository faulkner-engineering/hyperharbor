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
  import type { Detail } from "$lib/catalogBrowser";
  import { onMount, untrack } from "svelte";
  import { SvelteMap } from "svelte/reactivity";
  import type { PaletteName } from "$lib/machine/assets";
  import { prefersReducedMotion } from "$lib/machine/fallback";
  import BrowserSection from "./BrowserSection.svelte";
  import DetailCard from "./DetailCard.svelte";
  import Dialog from "./Dialog.svelte";
  import InstallSection from "./InstallSection.svelte";
  import MachineView, { type MachineStatus, type MotionSetting } from "./MachineView.svelte";
  import RemoveSection from "./RemoveSection.svelte";
  import TweaksSection from "./TweaksSection.svelte";

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

  /** The catalog entry whose details the machine view shows. */
  let detail = $state<Detail | null>(null);

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

<Dialog title={profileId ? `Edit ${start.name}` : "New setup profile"} terminal accent={palette === "green" ? "var(--hh-crt-green-glow)" : "var(--hh-crt-amber-glow)"}>
  <div class="editor" data-palette={palette} class:calm={!motionOn}>
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
              onclick={() => {
                tab = item.id;
                detail = null;
              }}
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
        {#if detail}<DetailCard {detail} onclose={() => (detail = null)} />{/if}
      </div>

      <div class="panel" id="section-panel" role="tabpanel" aria-labelledby="section-{tab}">
        <div class="caps">{current.label}</div>
        {#key tab}
          <div class="body enter">
            {#if tab === "packages"}
              <InstallSection {host} bind:install selected={detail?.key ?? null} onselect={(next) => (detail = next)} />
            {:else if tab === "remove"}
              <RemoveSection
                {host}
                {vms}
                catalog={catalog?.appx ?? []}
                bind:appx
                bind:capabilities
                bind:features
                selected={detail?.key ?? null}
                onselect={(next) => (detail = next)}
              />
            {:else if tab === "tweaks"}
              <TweaksSection catalog={catalog?.tweaks ?? []} bind:tweakIds bind:custom selected={detail?.key ?? null} onselect={(next) => (detail = next)} />
            {:else}
              <BrowserSection
                {host}
                browsers={catalog?.browsers ?? []}
                {browser}
                {browserPolicies}
                {extensionCatalog}
                bind:browserApp
                bind:extensions
                {policies}
                selected={detail?.key ?? null}
                onselect={(next) => (detail = next)}
              />
            {/if}
          </div>
        {/key}
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

  .header label {
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
    box-shadow: 0 0 24px color-mix(in srgb, var(--accent) 8%, transparent);
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
    box-shadow:
      inset 3px 0 0 var(--accent),
      0 0 12px var(--glow);
  }

  .sections [role="tab"].active .label {
    color: var(--accent);
    text-shadow: 0 0 8px var(--glow);
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

  /* A block cursor after the HUD line. */
  .hud::after {
    content: "";
    display: inline-block;
    width: 0.55em;
    height: 1em;
    margin-left: 0.3em;
    vertical-align: -0.15em;
    background: currentColor;
    animation: cursor 1.06s steps(1) infinite;
  }

  .enter {
    animation: enter 0.22s ease-out;
  }

  /* With Motion off the interface holds still too. */
  .calm .hud::after,
  .calm .enter {
    animation: none;
  }

  @keyframes cursor {
    50% {
      opacity: 0;
    }
  }

  @keyframes enter {
    from {
      opacity: 0;
      transform: translateX(6px);
    }
  }

  .panel {
    display: flex;
    flex-direction: column;
    min-height: 0;
    padding: 0.9rem 1rem;
    border-left: 1px solid var(--border);
    background: var(--surface);
  }

  .panel > .caps {
    color: var(--accent);
    text-shadow: 0 0 8px var(--glow);
  }

  .body {
    flex: 1;
    min-height: 0;
    overflow-y: auto;
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
</style>
