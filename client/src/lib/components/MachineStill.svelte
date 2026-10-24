<script lang="ts">
  import { loadExplodedStill, readPalette, type ExplodedStill } from "$lib/machine/assets";
  import type { ResolvedHighlight } from "$lib/machine/highlights";
  import { onMount } from "svelte";

  interface Props {
    highlight: ResolvedHighlight;
    reducedMotion: boolean;
  }

  let { highlight, reducedMotion }: Props = $props();

  let still = $state<ExplodedStill | null>(null);
  const glow = $derived(new Set(highlight.glow));
  const spin = $derived(new Set(highlight.spin));
  const colors = readPalette();

  onMount(async () => {
    try {
      still = await loadExplodedStill();
    } catch {
      // Without the still the view stays an empty screen; the form beside it carries everything.
    }
  });
</script>

<div
  class="still"
  class:reduced={reducedMotion}
  style:--line={colors.line}
  style:--glow={colors.glow}
  style:--screen={colors.background}
>
  {#if still}
    <svg viewBox={still.viewBox} preserveAspectRatio="xMidYMid meet" fill="none" stroke-linecap="round">
      {#each still.parts as part (part.node)}
        <path
          d={part.d}
          data-node={part.node}
          class:glow={glow.has(part.node)}
          class:blink={highlight.blink === part.node}
          class:pulse={highlight.pulse === part.node}
          class:spin={spin.has(part.node)}
        />
      {/each}
    </svg>
  {/if}
</div>

<style>
  .still {
    position: absolute;
    inset: 0;
    background: var(--screen);
    display: flex;
    align-items: center;
    justify-content: center;
    padding: 1rem;
    box-sizing: border-box;
  }

  /* Scanlines and a vignette, a static stand-in for the CRT pass. */
  .still::after {
    content: "";
    position: absolute;
    inset: 0;
    pointer-events: none;
    background:
      repeating-linear-gradient(to bottom, rgb(0 0 0 / 0.18) 0 1px, transparent 1px 3px),
      radial-gradient(ellipse at center, transparent 55%, rgb(0 0 0 / 0.55) 100%);
  }

  svg {
    width: 100%;
    height: 100%;
  }

  path {
    stroke: var(--line);
    stroke-width: 1;
    vector-effect: non-scaling-stroke;
  }

  path.glow {
    stroke: var(--glow);
    stroke-width: 1.6;
    filter: drop-shadow(0 0 3px var(--glow));
  }

  /* Same reactions as the 3D view: blink toggles at 2 Hz, pulse fades, spin runs dashes around the fan. */
  path.blink {
    animation: blink 1s linear infinite;
  }

  path.pulse {
    animation: pulse 2.1s ease-in-out infinite;
  }

  path.spin {
    stroke-dasharray: 5 4;
    animation: spin 0.6s linear infinite;
  }

  /* Reduced motion stops the spin; blink and pulse stay, since they show state. */
  .reduced path.spin {
    animation: none;
  }

  @keyframes blink {
    0%,
    49.9% {
      opacity: 1;
    }
    50%,
    100% {
      opacity: 0;
    }
  }

  @keyframes pulse {
    0%,
    100% {
      opacity: 1;
    }
    50% {
      opacity: 0.1;
    }
  }

  @keyframes spin {
    to {
      stroke-dashoffset: -9;
    }
  }
</style>
