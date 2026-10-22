<script lang="ts">
  import { onMount } from "svelte";
  import { errorMessage, getRdpTrustStatus, removeRdpTrust, setUpRdpTrust, type RdpTrustStatus } from "$lib/api/client";

  let status = $state<RdpTrustStatus | null>(null);
  let busy = $state(false);
  let error = $state<string | null>(null);

  onMount(() => {
    getRdpTrustStatus()
      .then((current) => (status = current))
      .catch((e) => (error = errorMessage(e)));
  });

  async function trust() {
    busy = true;
    error = null;
    try {
      status = await setUpRdpTrust();
      // Approved and written, yet not in effect: something on this PC (usually Group Policy) overrides it.
      if (status.state === "notSetUp")
        error = "Windows still does not trust HyperHarbor's connections. A policy on this PC may override the setting.";
    } catch (e) {
      error = errorMessage(e);
    } finally {
      busy = false;
    }
  }

  async function stopTrusting() {
    busy = true;
    error = null;
    try {
      status = await removeRdpTrust();
    } catch (e) {
      error = errorMessage(e);
    } finally {
      busy = false;
    }
  }
</script>

{#if (status && status.state !== "unsupported") || error}
  <section aria-label="Remote Desktop connections">
    {#if status?.state === "ready"}
      <p class="muted">Remote Desktop opens HyperHarbor's connections without asking.</p>
      <button type="button" class="link" onclick={stopTrusting} disabled={busy}>
        {busy ? "Waiting for approval…" : "Stop trusting them"}
      </button>
    {:else if status?.state === "notSetUp"}
      <p>
        Remote Desktop asks what each connection may use, such as the clipboard, microphone, and camera, and starts with
        all of them off.
      </p>
      <button type="button" class="primary" onclick={trust} disabled={busy}>
        {busy ? "Waiting for approval…" : "Trust HyperHarbor connections"}
      </button>
      <p class="muted">Needs administrator approval once on this PC.</p>
    {/if}
    {#if error}<p class="error" role="alert">{error}</p>{/if}
  </section>
{/if}

<style>
  section {
    display: flex;
    flex-direction: column;
    gap: 0.5rem;
    padding-top: 1rem;
    border-top: 1px solid var(--border);
    font-size: 0.85rem;
  }

  p {
    margin: 0;
  }

  .muted {
    color: var(--muted);
  }

  .error {
    color: var(--danger);
  }

  button.primary {
    background: var(--accent);
    border-color: var(--accent);
    color: var(--accent-fg);
  }

  button.link {
    align-self: flex-start;
    padding: 0;
    border: 0;
    background: none;
    color: var(--muted);
    font: inherit;
    text-decoration: underline;
    cursor: pointer;
  }
</style>
