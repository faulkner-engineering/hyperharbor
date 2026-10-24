<script lang="ts">
  import type { ValidationIssue } from "$lib/api/client";
  import type { Detail } from "$lib/catalogBrowser";
  import type { PaletteName } from "$lib/machine/assets";
  import { prefersReducedMotion } from "$lib/machine/fallback";
  import type { ControlState } from "$lib/machine/highlights";
  import type { WorkspaceSection } from "$lib/softwareDraft.svelte";
  import type { Snippet } from "svelte";
  import DetailCard from "./DetailCard.svelte";
  import Dialog from "./Dialog.svelte";
  import MachineView, { type MachineStatus, type MotionSetting } from "./MachineView.svelte";

  interface Props {
    title: string;
    sections: WorkspaceSection[];
    /** The selected section's id. */
    tab: string;
    /** The catalog entry whose details the machine view shows. */
    detail: Detail | null;
    /** Form state the machine's reactions follow. */
    controls: ControlState;
    /** What the HUD line says after the section name. */
    hud: string;
    error: string | null;
    issues: ValidationIssue[];
    busy: boolean;
    canSave: boolean;
    onsave: () => void;
    oncancel: () => void;
    /** Name, description, and other fields above the workspace. */
    header: Snippet;
    /** The selected section's form. */
    children: Snippet;
  }

  let {
    title,
    sections,
    tab = $bindable(),
    detail = $bindable(),
    controls,
    hud,
    error,
    issues,
    busy,
    canSave,
    onsave,
    oncancel,
    header,
    children,
  }: Props = $props();

  const PALETTE_KEY = "hh.machinePalette";
  const MOTION_KEY = "hh.machineMotion";

  let palette = $state<PaletteName>(stored(PALETTE_KEY) === "green" ? "green" : "amber");
  let motion = $state<MotionSetting>(storedMotion());
  let machineStatus = $state<MachineStatus>("loading");
  const motionOn = $derived(motion === "system" ? !prefersReducedMotion() : motion === "on");
  const current = $derived(sections.find((item) => item.id === tab) ?? sections[0]);
  const line = $derived(
    [
      current.label.toUpperCase(),
      hud,
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

  function choose(id: string) {
    tab = id;
    detail = null;
  }
</script>

<Dialog {title} terminal accent={palette === "green" ? "var(--hh-crt-green-glow)" : "var(--hh-crt-amber-glow)"}>
  <div class="editor" data-palette={palette} class:calm={!motionOn}>
    <div class="header">{@render header()}</div>

    <div class="workspace">
      <nav class="sections">
        <div class="caps" id="profile-sections">Profile sections</div>
        <div role="tablist" aria-labelledby="profile-sections" aria-orientation="vertical">
          {#each sections as item (item.id)}
            {#if item.group}<div class="group" role="presentation">{item.group}</div>{/if}
            <button
              type="button"
              role="tab"
              id="section-{item.id}"
              aria-selected={tab === item.id}
              aria-controls="section-panel"
              class:active={tab === item.id}
              onclick={() => choose(item.id)}
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
        <MachineView section={current.section} {controls} {palette} {motion} bind:status={machineStatus} />
        <div class="hud" aria-live="polite">{line}</div>
        {#if detail}<DetailCard {detail} onclose={() => (detail = null)} />{/if}
      </div>

      <div class="panel" id="section-panel" role="tabpanel" aria-labelledby="section-{tab}">
        <div class="caps">{current.label}</div>
        {#key tab}
          <div class="body enter">
            {@render children()}
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
      <button type="button" onclick={oncancel} disabled={busy}>Cancel</button>
      <button type="button" class="primary" onclick={onsave} disabled={busy || !canSave}>{busy ? "Saving…" : "Save"}</button>
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
    grid-template-columns: 1fr 2fr auto;
    gap: 0.75rem;
    align-items: end;
  }

  .header :global(label) {
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
  .group,
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

  .group {
    margin: 0.5rem 0 0.1rem;
    font-size: 0.68rem;
    letter-spacing: 0.14em;
    text-transform: uppercase;
    color: color-mix(in srgb, var(--accent) 70%, var(--muted));
  }

  .group:first-child {
    margin-top: 0;
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

    .header {
      grid-template-columns: 1fr;
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
