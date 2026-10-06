<script lang="ts">
  interface Props {
    /** 0 to 1, or null for work whose length is unknown (a sliding bar). */
    fraction: number | null;
    label: string;
  }

  let { fraction, label }: Props = $props();
</script>

<div
  class="track"
  role="progressbar"
  aria-label={label}
  aria-valuemin={0}
  aria-valuemax={100}
  aria-valuenow={fraction === null ? undefined : Math.round(fraction * 100)}
>
  {#if fraction === null}
    <div class="bar sliding"></div>
  {:else}
    <div class="bar" style:width="{Math.max(2, fraction * 100)}%"></div>
  {/if}
</div>

<style>
  .track {
    position: relative;
    height: 6px;
    overflow: hidden;
    border-radius: 999px;
    background: var(--hover);
  }

  .bar {
    height: 100%;
    border-radius: inherit;
    background: var(--accent);
    transition: width 400ms var(--ease);
  }

  .sliding {
    position: absolute;
    width: 30%;
    animation: slide 1.4s var(--ease) infinite;
  }

  @keyframes slide {
    from {
      left: -30%;
    }

    to {
      left: 100%;
    }
  }
</style>
