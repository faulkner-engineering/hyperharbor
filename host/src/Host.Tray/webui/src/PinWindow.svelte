<script lang="ts">
  import { onMount } from "svelte";
  import { tray } from "./store.svelte";
  import { countdown, formatPin } from "./text";

  const pairing = $derived(tray.state?.pairing ?? null);

  let now = $state(Date.now());
  onMount(() => {
    const timer = setInterval(() => (now = Date.now()), 250);
    return () => clearInterval(timer);
  });

  // The ring empties over the PIN's lifetime, measured from when this window first saw the request.
  let shownAt = $state<{ id: string; at: number } | null>(null);
  $effect(() => {
    if (pairing && shownAt?.id !== pairing.pairingId) shownAt = { id: pairing.pairingId, at: Date.now() };
  });
  const fraction = $derived.by(() => {
    if (!pairing || !shownAt) return 1;
    const total = Date.parse(pairing.expiresAt) - shownAt.at;
    return total <= 0 ? 0 : Math.max(0, Math.min(1, (Date.parse(pairing.expiresAt) - now) / total));
  });

  const RADIUS = 52;
  const CIRCUMFERENCE = 2 * Math.PI * RADIUS;
</script>

<div class="pin-window">
  {#if pairing}
    <p class="lead">Enter this PIN on <strong>{pairing.deviceName}</strong> to pair it with this PC</p>
    <div class="pin" aria-label="PIN {pairing.pin.split('').join(' ')}">{formatPin(pairing.pin)}</div>
    <div class="ring" role="timer" aria-label="Expires in {countdown(pairing.expiresAt, now)}">
      <svg viewBox="0 0 120 120" width="72" height="72" aria-hidden="true">
        <circle cx="60" cy="60" r={RADIUS} class="track" />
        <circle
          cx="60"
          cy="60"
          r={RADIUS}
          class="progress"
          stroke-dasharray={CIRCUMFERENCE}
          stroke-dashoffset={CIRCUMFERENCE * (1 - fraction)}
        />
      </svg>
      <span>{countdown(pairing.expiresAt, now)}</span>
    </div>
    <button type="button" class="btn" onclick={() => tray.send({ type: "cancelPairing", pairingId: pairing.pairingId })}>Cancel</button>
  {:else}
    <p class="muted">Waiting for a pairing request…</p>
  {/if}
</div>

<style>
  .pin-window {
    display: grid;
    justify-items: center;
    align-content: center;
    gap: 1.1rem;
    height: 100%;
    padding: 1.5rem;
    text-align: center;
  }

  .lead {
    max-width: 320px;
    color: var(--muted);
  }

  .lead strong {
    color: var(--text);
  }

  .pin {
    font-family: "Cascadia Mono", Consolas, monospace;
    font-size: 3.2rem;
    font-weight: 700;
    letter-spacing: 0.12em;
    user-select: text;
  }

  .ring {
    position: relative;
    display: grid;
    place-items: center;
  }

  .ring span {
    position: absolute;
    font-size: 0.85rem;
    font-weight: 600;
  }

  circle {
    fill: none;
    stroke-width: 8;
  }

  .track {
    stroke: var(--border);
  }

  .progress {
    stroke: var(--accent);
    stroke-linecap: round;
    transform: rotate(-90deg);
    transform-origin: 60px 60px;
    transition: stroke-dashoffset 250ms linear;
  }
</style>
