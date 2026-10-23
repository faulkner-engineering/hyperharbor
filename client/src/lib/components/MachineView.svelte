<script lang="ts" module>
  /** "system" follows the reduced-motion preference (Windows animation effects); "on" and "off" override it. */
  export type MotionSetting = "system" | "on" | "off";

  /** What the view shows: the 3D scene, or the still and why. */
  export type MachineStatus = "loading" | "scene" | "still-no-webgl" | "still-slow" | "still-error";
</script>

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
    motion?: MotionSetting;
    status?: MachineStatus;
  }

  let { section, controls = {}, palette = "amber", motion = "system", status = $bindable("loading") }: Props = $props();

  let stage: HTMLDivElement;
  let data = $state<MachineData | null>(null);
  let systemReduced = $state(prefersReducedMotion());
  // Not reactive: effects below re-run when status changes, which is when the scene appears or goes.
  let scene: MachineScene | null = null;

  const reducedMotion = $derived(motion === "system" ? systemReduced : motion === "off");
  const still = $derived(status.startsWith("still"));
  const controlState = $derived<ControlState>({
    items: controls.items,
    tpmEnabled: controls.tpmEnabled,
    backupScheduled: controls.backupScheduled,
    performanceMode: controls.performanceMode,
  });
  const highlight = $derived(data ? resolveHighlight(data.highlights, section, controlState) : NO_HIGHLIGHT);

  /** Switches to the exploded still for the rest of the view's life. */
  function fallBack(reason: MachineStatus) {
    scene?.dispose();
    scene = null;
    status = reason;
  }

  onMount(() => {
    let destroyed = false;
    status = "loading";
    const unwatch = watchReducedMotion((reduced) => (systemReduced = reduced));

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
        fallBack("still-no-webgl");
        return;
      }
      try {
        const { createMachineScene } = await import("$lib/machine/scene");
        const created = await createMachineScene(stage, loaded, {
          palette: readPalette(palette),
          reducedMotion,
          onslow: () => fallBack("still-slow"),
        });
        if (destroyed) {
          created.dispose();
          return;
        }
        scene = created;
        status = "scene";
      } catch {
        if (!destroyed) fallBack("still-error");
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
    if (status === "scene") scene?.setSection(section);
  });

  $effect(() => {
    if (status === "scene") scene?.setControls(controlState);
  });

  $effect(() => {
    if (status === "scene") scene?.setPalette(readPalette(palette));
  });

  $effect(() => {
    if (status === "scene") scene?.setReducedMotion(reducedMotion);
  });
</script>

<div class="machine" data-mode={status === "scene" ? "scene" : still ? "still" : "loading"} data-section={section} aria-hidden="true">
  <div class="stage" bind:this={stage}></div>
  {#if still}
    <MachineStill {highlight} {palette} {reducedMotion} />
  {/if}
</div>

<style>
  .machine {
    position: relative;
    min-height: 0;
    overflow: hidden;
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
