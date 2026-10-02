<script lang="ts">
  import { onDestroy } from "svelte";
  import {
    cancelPairing,
    completePairing,
    errorMessage,
    isClientError,
    startPairing,
    type HostEntry,
  } from "$lib/api/client";

  interface Props {
    host: HostEntry;
    onpaired: () => void;
  }

  let { host, onpaired }: Props = $props();

  let stage = $state<"idle" | "starting" | "awaitingPin" | "confirming">("idle");
  let pin = $state("");
  let expiresAt = $state<number | null>(null);
  let now = $state(Date.now());
  let error = $state<string | null>(null);

  const secondsLeft = $derived(
    expiresAt === null ? 0 : Math.max(0, Math.ceil((expiresAt - now) / 1000)),
  );
  const pinValid = $derived(/^[0-9]{6}$/.test(pin));

  const ticker = setInterval(() => (now = Date.now()), 1000);
  onDestroy(() => {
    clearInterval(ticker);
    if (stage === "awaitingPin") cancelPairing(host.key);
  });

  async function begin() {
    stage = "starting";
    error = null;
    pin = "";
    try {
      const started = await startPairing(host.key);
      expiresAt = Date.parse(started.expiresAt);
      stage = "awaitingPin";
    } catch (e) {
      error = errorMessage(e);
      stage = "idle";
    }
  }

  async function confirm(event: SubmitEvent) {
    event.preventDefault();
    if (!pinValid) return;
    stage = "confirming";
    error = null;
    try {
      await completePairing(host.key, pin);
      stage = "idle";
      onpaired();
    } catch (e) {
      error = errorMessage(e);
      // A wrong PIN can be retried against the same request; anything else needs a new one.
      const retryable = isClientError(e) && e.code === "api" && e.status === 401;
      stage = retryable ? "awaitingPin" : "idle";
      pin = "";
    }
  }

  function cancel() {
    cancelPairing(host.key);
    stage = "idle";
    error = null;
  }

  function onPinInput(event: Event) {
    const input = event.currentTarget as HTMLInputElement;
    pin = input.value.replace(/[^0-9]/g, "").slice(0, 6);
    input.value = pin;
  }
</script>

<section class="panel" aria-label="Pairing">
  {#if stage === "idle" || stage === "starting"}
    <h3>Pair with {host.displayName}</h3>
    <p>
      Pairing lets this device control the host's virtual machines. A PIN will appear on
      the host; enter it here to finish.
    </p>
    <button type="button" class="primary" onclick={begin} disabled={stage === "starting"}>
      {stage === "starting" ? "Requesting PIN…" : "Pair"}
    </button>
  {:else}
    <h3>Enter the PIN shown on {host.displayName}</h3>
    <form onsubmit={confirm}>
      <input
        class="pin"
        value={pin}
        oninput={onPinInput}
        inputmode="numeric"
        autocomplete="one-time-code"
        maxlength="6"
        placeholder="000000"
        aria-label="PIN"
        disabled={stage === "confirming"}
      />
      <div class="actions">
        <button type="submit" class="primary" disabled={!pinValid || stage === "confirming"}>
          {stage === "confirming" ? "Verifying…" : "Pair"}
        </button>
        <button type="button" onclick={cancel} disabled={stage === "confirming"}>Cancel</button>
        <span class="expiry">
          {secondsLeft > 0
            ? `Expires in ${Math.floor(secondsLeft / 60)}:${String(secondsLeft % 60).padStart(2, "0")}`
            : "Expired. Cancel and start again."}
        </span>
      </div>
    </form>
  {/if}

  {#if error}<p class="error" role="alert">{error}</p>{/if}
</section>

<style>
  .panel {
    max-width: 460px;
    padding: 1.25rem;
    border: 1px solid var(--border);
    border-radius: 8px;
    background: var(--surface);
  }

  h3 {
    margin: 0 0 0.5rem;
    font-size: 1.05rem;
  }

  p {
    margin: 0 0 1rem;
    color: var(--muted);
  }

  form {
    display: flex;
    flex-direction: column;
    gap: 0.75rem;
  }

  .pin {
    width: 9ch;
    padding: 0.4rem 0.6rem;
    font-size: 1.6rem;
    font-variant-numeric: tabular-nums;
    letter-spacing: 0.2em;
    border: 1px solid var(--border);
    border-radius: 6px;
    background: var(--bg);
    color: inherit;
  }

  .actions {
    display: flex;
    align-items: center;
    gap: 0.5rem;
  }

  button {
    padding: 0.45rem 1rem;
    border: 1px solid var(--border);
    border-radius: 6px;
    background: var(--surface);
    color: inherit;
    cursor: pointer;
  }

  button.primary {
    background: var(--accent);
    border-color: var(--accent);
    color: var(--accent-fg);
  }

  button:disabled {
    opacity: 0.5;
    cursor: default;
  }

  .expiry {
    margin-left: auto;
    font-size: 0.85rem;
    color: var(--muted);
  }

  .error {
    margin: 0.75rem 0 0;
    color: var(--danger);
  }
</style>
