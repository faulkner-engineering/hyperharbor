<script lang="ts">
  import { onMount, tick } from "svelte";
  import { listen, send } from "./bridge";
  import type { AmbientData } from "./bridge";
  import { clockParts, driftFor, vmLine } from "./ambient";

  // The ambient screen: true black, a clock, and small optional widgets. It shows in the screensaver process, so it costs as
  // little as it can: the clock changes once a minute, nothing animates, and the position moves by a jump every few minutes
  // (against burn-in on OLED televisions), not by a transition.
  let now = $state(new Date());
  let data = $state<AmbientData>({});
  let hintVisible = $state(false);

  const clock = $derived(clockParts(now));
  const drift = $derived(driftFor(now));
  const vms = $derived(data.runningVms == null ? null : vmLine(data.runningVms));

  onMount(() => {
    let timer: ReturnType<typeof setTimeout> | undefined;
    let hintTimer: ReturnType<typeof setTimeout> | undefined;

    function schedule() {
      const current = new Date();
      // The next minute boundary, a moment after it.
      const wait = 60_000 - (current.getSeconds() * 1000 + current.getMilliseconds()) + 50;
      timer = setTimeout(() => {
        now = new Date();
        schedule();
      }, wait);
    }

    listen((message) => {
      if (message.type === "data") {
        data = message;
      } else if (message.type === "hint") {
        hintVisible = true;
        clearTimeout(hintTimer);
        hintTimer = setTimeout(() => (hintVisible = false), message.seconds * 1000);
      }
    });

    schedule();
    // The screensaver shows its window already (black); the page tells it that it has drawn.
    void tick().then(() => send({ type: "ready" }));
    return () => {
      clearTimeout(timer);
      clearTimeout(hintTimer);
    };
  });
</script>

<main class="screen" style:--dx="{drift.x}vmin" style:--dy="{drift.y}vmin">
  <div class="center">
    <p class="time">{clock.time}</p>
    <p class="date">{clock.date}</p>
  </div>

  <ul class="widgets" aria-label="Host status">
    {#if data.hostStatus}<li>{data.hostStatus}</li>{/if}
    {#if vms !== null}<li>{vms}</li>{/if}
    {#if data.devices}<li>{data.devices}</li>{/if}
    {#if data.backup}<li>{data.backup}</li>{/if}
  </ul>

  {#if data.chordHint}
    <p class="hint" class:visible={hintVisible} role="status">Hold {data.chordHint} to unlock</p>
  {/if}
</main>

<style>
  :global(html),
  :global(body) {
    background: #000;
    color: #c9c5bc;
    cursor: none;
  }

  .screen {
    position: fixed;
    inset: 0;
    display: grid;
    background: #000;
    overflow: hidden;
  }

  .center {
    place-self: center;
    translate: var(--dx) var(--dy);
    text-align: center;
  }

  .time {
    font-size: 22vmin;
    font-weight: 200;
    line-height: 1;
    letter-spacing: 0.02em;
    font-variant-numeric: tabular-nums;
  }

  .date {
    margin-top: 2vmin;
    font-size: 3.4vmin;
    color: #7d8087;
  }

  .widgets {
    position: absolute;
    left: 4vmin;
    bottom: 4vmin;
    display: grid;
    gap: 0.8vmin;
    margin: 0;
    padding: 0;
    list-style: none;
    font-size: 2.4vmin;
    color: #5e6168;
  }

  .hint {
    position: absolute;
    right: 4vmin;
    bottom: 4vmin;
    margin: 0;
    font-size: 2.4vmin;
    color: #e39b2e;
    visibility: hidden;
  }

  .hint.visible {
    visibility: visible;
  }
</style>
