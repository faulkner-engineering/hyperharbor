<script lang="ts">
  import { onMount } from "svelte";
  import Dialog from "./Dialog.svelte";
  import {
    errorMessage,
    getMonitorChoice,
    listMonitors,
    setMonitorChoice,
    type HostEntry,
    type Monitor,
    type MonitorMode,
    type Vm,
  } from "$lib/api/client";
  import { areContiguous } from "$lib/monitors";

  interface Props {
    host: HostEntry;
    vm: Vm;
    onclose: () => void;
  }

  let { host, vm, onclose }: Props = $props();

  let monitors = $state<Monitor[] | null>(null);
  let mode = $state<MonitorMode>("single");
  let selected = $state<string[]>([]);
  let saving = $state(false);
  let error = $state<string | null>(null);

  /** Saved monitors that are not attached now. They stay in the choice for when they return. */
  let missing = $derived(
    monitors === null ? [] : selected.filter((key) => !monitors!.some((m) => m.key === key)),
  );
  let chosen = $derived(monitors?.filter((m) => selected.includes(m.key)) ?? []);
  let gapWarning = $derived(mode === "selected" && !areContiguous(chosen));

  /** The diagram: every monitor scaled into a box that keeps the desktop's proportions. */
  let layout = $derived.by(() => {
    if (monitors === null || monitors.length === 0) return null;
    const left = Math.min(...monitors.map((m) => m.x));
    const top = Math.min(...monitors.map((m) => m.y));
    const right = Math.max(...monitors.map((m) => m.x + m.width));
    const bottom = Math.max(...monitors.map((m) => m.y + m.height));
    const width = right - left;
    const height = bottom - top;
    return {
      aspect: `${width} / ${height}`,
      // At most 260px tall, so a vertical stack of monitors does not fill the dialog.
      width: `min(100%, ${Math.round((260 * width) / height)}px)`,
      boxes: monitors.map((m) => ({
        monitor: m,
        style:
          `left: ${((m.x - left) / width) * 100}%; top: ${((m.y - top) / height) * 100}%; ` +
          `width: ${(m.width / width) * 100}%; height: ${(m.height / height) * 100}%;`,
      })),
    };
  });

  onMount(async () => {
    try {
      const [list, choice] = await Promise.all([listMonitors(), getMonitorChoice(host.key, vm.id)]);
      monitors = list;
      mode = choice.mode;
      selected = choice.selected;
    } catch (e) {
      error = errorMessage(e);
      monitors = [];
    }
  });

  function used(monitor: Monitor): boolean {
    if (mode === "all") return true;
    if (mode === "selected") return selected.includes(monitor.key);
    return false;
  }

  /** Clicking a monitor switches to the selected mode, starting from what was in use. */
  function toggle(monitor: Monitor) {
    if (mode !== "selected") {
      selected = mode === "all" ? monitors!.map((m) => m.key) : [];
      mode = "selected";
    }
    selected = selected.includes(monitor.key)
      ? selected.filter((key) => key !== monitor.key)
      : [...selected, monitor.key];
  }

  async function save(event: SubmitEvent) {
    event.preventDefault();
    saving = true;
    error = null;
    try {
      // Monitors that are not attached are kept only while the selected mode is in use.
      await setMonitorChoice(host.key, vm.id, { mode, selected: mode === "selected" ? selected : [] });
      onclose();
    } catch (e) {
      error = errorMessage(e);
    } finally {
      saving = false;
    }
  }
</script>

<Dialog title="Monitors for {vm.name}" wide>
  <form onsubmit={save}>
    <p class="muted">
      Which of this device's monitors the Remote Desktop session fills when you connect to this VM.
      It is saved on this device; the VM console always uses one monitor.
    </p>

    <label class="check">
      <input type="radio" name="mode" value="single" bind:group={mode} />
      One monitor (the one the session window opens on)
    </label>
    <label class="check">
      <input type="radio" name="mode" value="all" bind:group={mode} />
      All monitors
    </label>
    <label class="check">
      <input type="radio" name="mode" value="selected" bind:group={mode} />
      Only the monitors I select
    </label>

    {#if monitors === null}
      <p class="muted">Reading monitors…</p>
    {:else if layout}
      <div class="desktop" style:aspect-ratio={layout.aspect} style:width={layout.width}>
        {#each layout.boxes as box (box.monitor.key)}
          <button
            type="button"
            class="monitor"
            class:used={used(box.monitor)}
            style={box.style}
            aria-pressed={used(box.monitor)}
            title="{box.monitor.name}, {box.monitor.width} × {box.monitor.height} (mstsc ID {box.monitor.mstscId})"
            onclick={() => toggle(box.monitor)}
          >
            <span class="name">{box.monitor.name}</span>
            <span class="detail">{box.monitor.width} × {box.monitor.height}</span>
            {#if box.monitor.primary}<span class="detail">Main display</span>{/if}
          </button>
        {/each}
      </div>
      <p class="muted hint">Click monitors to choose them. The session's taskbar goes on your main display when it is chosen.</p>
    {/if}

    {#if mode === "selected" && chosen.length === 0}
      <p class="warning">Choose at least one monitor that is connected now.</p>
    {:else if gapWarning}
      <p class="warning">
        These monitors are not next to each other. Remote Desktop may not use all of them; choose
        monitors that touch.
      </p>
    {/if}
    {#if mode === "selected" && missing.length > 0}
      <p class="muted">
        {missing.length === 1 ? "One chosen monitor is" : `${missing.length} chosen monitors are`} not
        connected now and will be used again when {missing.length === 1 ? "it is" : "they are"}.
      </p>
    {/if}
    {#if error}<p class="error">{error}</p>{/if}

    <div class="actions">
      <button type="button" onclick={onclose}>Cancel</button>
      <button
        type="submit"
        class="primary"
        disabled={saving || monitors === null || (mode === "selected" && chosen.length === 0)}
        >Save</button
      >
    </div>
  </form>
</Dialog>

<style>
  .desktop {
    position: relative;
    margin: 0.5rem auto 0;
  }

  .desktop .monitor {
    position: absolute;
    box-sizing: border-box;
    display: flex;
    flex-direction: column;
    align-items: center;
    justify-content: center;
    gap: 0.1rem;
    padding: 0.25rem;
    overflow: hidden;
    border: 2px solid var(--border);
    background: var(--bg);
    font-size: 0.8rem;
    text-align: center;
  }

  .desktop .monitor.used {
    border-color: var(--accent);
    background: color-mix(in srgb, var(--accent) 25%, var(--surface));
  }

  .name {
    font-weight: 600;
  }

  .detail {
    color: var(--muted);
  }

  .hint {
    margin-top: 0.25rem;
    font-size: 0.8rem;
  }
</style>
