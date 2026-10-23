<script lang="ts">
  import { loadMachineData, readPalette, type MachineData, type PaletteName } from "$lib/machine/assets";
  import { prefersReducedMotion, watchReducedMotion, webglAvailable } from "$lib/machine/fallback";
  import { NO_HIGHLIGHT, resolveHighlight, type ControlState } from "$lib/machine/highlights";
  import type { MachineScene } from "$lib/machine/scene";
  import { onMount } from "svelte";
  import MachineStill from "./MachineStill.svelte";

  interface Props {
    /** The active profile section id, a key of targets.json and highlights.json. */
    section: string;
    /** Form state that drives the per-control reactions. */
    controls?: ControlState;
    palette?: PaletteName;
  }

  let { section, controls = {}, palette = "amber" }: Props = $props();

  type Mode = "loading" | "scene" | "still";

  let stage: HTMLDivElement;
  let mode = $state<Mode>("loading");
  let data = $state<MachineData | null>(null);
  let reducedMotion = $state(prefersReducedMotion());
  // Not reactive: effects below re-run when mode changes, which is when the scene appears or goes.
  let scene: MachineScene | null = null;

  const controlState = $derived<ControlState>({
    items: controls.items,
    tpmEnabled: controls.tpmEnabled,
    backupScheduled: controls.backupScheduled,
    performanceMode: controls.performanceMode,
  });
  const highlight = $derived(data ? resolveHighlight(data.highlights, section, controlState) : NO_HIGHLIGHT);

  /** Switches to the exploded still for the rest of the view's life (no WebGL, or too slow). */
  function fallBack() {
    scene?.dispose();
    scene = null;
    mode = "still";
  }

  onMount(() => {
    let destroyed = false;
    const unwatch = watchReducedMotion((reduced) => {
      reducedMotion = reduced;
      scene?.setReducedMotion(reduced);
    });

    (async () => {
      let loaded: MachineData;
      try {
        loaded = await loadMachineData();
      } catch {
        loaded = { targets: {}, highlights: {} };
      }
      if (destroyed) return;
      data = loaded;
      if (!webglAvailable()) {
        fallBack();
        return;
      }
      try {
        const { createMachineScene } = await import("$lib/machine/scene");
        const created = await createMachineScene(stage, loaded, {
          palette: readPalette(palette),
          reducedMotion,
          onslow: fallBack,
        });
        if (destroyed) {
          created.dispose();
          return;
        }
        scene = created;
        mode = "scene";
      } catch {
        if (!destroyed) fallBack();
      }
    })();

    return () => {
      destroyed = true;
      unwatch();
      scene?.dispose();
      scene = null;
    };
  });

  $effect(() => {
    if (mode === "scene") scene?.setSection(section);
  });

  $effect(() => {
    if (mode === "scene") scene?.setControls(controlState);
  });

  $effect(() => {
    if (mode === "scene") scene?.setPalette(readPalette(palette));
  });
</script>

<div class="machine" data-mode={mode} data-section={section} aria-hidden="true">
  <div class="stage" bind:this={stage}></div>
  {#if mode === "still"}
    <MachineStill {highlight} {palette} {reducedMotion} />
  {/if}
</div>

<style>
  .machine {
    position: relative;
    min-height: 0;
    overflow: hidden;
    border-radius: 8px;
    background: var(--hh-crt-screen);
  }

  .stage {
    position: absolute;
    inset: 0;
  }

  .stage :global(canvas) {
    display: block;
    width: 100%;
    height: 100%;
  }
</style>
