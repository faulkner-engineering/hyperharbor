<script lang="ts">
  import { fly } from "svelte/transition";
  import Icon, { type IconName } from "./components/Icon.svelte";
  import PassphraseDialog from "./components/PassphraseDialog.svelte";
  import Toasts from "./components/Toasts.svelte";
  import Devices from "./pages/Devices.svelte";
  import Logs from "./pages/Logs.svelte";
  import Overview from "./pages/Overview.svelte";
  import Storage from "./pages/Storage.svelte";
  import Updates from "./pages/Updates.svelte";
  import { tray, type Page } from "./store.svelte";

  const pages: { id: Page; label: string; icon: IconName; heading: string }[] = [
    { id: "overview", label: "Overview", icon: "overview", heading: "Overview" },
    { id: "devices", label: "Devices", icon: "devices", heading: "Paired devices" },
    { id: "storage", label: "Storage", icon: "storage", heading: "Storage" },
    { id: "updates", label: "Updates", icon: "updates", heading: "Updates" },
    { id: "logs", label: "Logs", icon: "logs", heading: "Logs" },
  ];

  const current = $derived(pages.find((page) => page.id === tray.page) ?? pages[0]);
  const state = $derived(tray.state);
  const updateReady = $derived(state?.update?.activity === "ready");
</script>

<div class="window">
  <nav aria-label="Sections">
    <div class="brand">
      <span class="mark">H</span>
      <span>HyperHarbor</span>
    </div>
    {#each pages as page (page.id)}
      <button
        type="button"
        class="nav"
        class:selected={tray.page === page.id}
        aria-current={tray.page === page.id ? "page" : undefined}
        onclick={() => (tray.page = page.id)}
      >
        <Icon name={page.icon} />
        <span>{page.label}</span>
        {#if page.id === "devices" && state}<span class="count">{state.devices.length}</span>{/if}
        {#if page.id === "updates" && updateReady}<span class="badge" title="An update is ready"></span>{/if}
      </button>
    {/each}
  </nav>

  <main>
    <header>
      <h1>{current.heading}</h1>
      {#if state}
        <span class="pill" class:ok={state.connected} role="status">
          <span class="dot"></span>
          {state.connected ? "Service running" : "Service not running"}
        </span>
      {/if}
    </header>

    {#if state && !state.connected}
      <p class="banner" transition:fly={{ y: -8, duration: 200 }}>
        The HyperHarbor Host service is not running. Start it from Services, or with Start-HyperHarbor.ps1 for a host
        that is not installed. This window updates once it runs.
      </p>
    {/if}

    <div class="scroll">
      {#if !state}
        <div class="skeleton" aria-busy="true">
          <div></div>
          <div></div>
          <div></div>
        </div>
      {:else}
        {#key tray.page}
          <div class="page" in:fly={{ y: 10, duration: 220, opacity: 0 }}>
            {#if tray.page === "overview"}
              <Overview view={state} />
            {:else if tray.page === "devices"}
              <Devices view={state} />
            {:else if tray.page === "storage"}
              <Storage view={state} />
            {:else if tray.page === "updates"}
              <Updates view={state} />
            {:else}
              <Logs view={state} />
            {/if}
          </div>
        {/key}
      {/if}
    </div>
  </main>
</div>

{#if tray.passphraseOpen && state}
  <PassphraseDialog replacing={state.passphraseConfigured === true} onclose={() => (tray.passphraseOpen = false)} />
{/if}

<Toasts />

<style>
  .window {
    display: grid;
    grid-template-columns: 210px 1fr;
    height: 100%;
  }

  nav {
    display: flex;
    flex-direction: column;
    gap: 0.2rem;
    padding: 1rem 0.7rem;
    border-right: 1px solid var(--border);
    background: var(--surface-2);
  }

  .brand {
    display: flex;
    align-items: center;
    gap: 0.6rem;
    margin: 0 0.4rem 1rem;
    font-weight: 600;
  }

  .mark {
    display: grid;
    place-items: center;
    width: 28px;
    height: 28px;
    border-radius: 8px;
    background: var(--accent);
    color: var(--accent-fg);
    font-weight: 700;
  }

  .nav {
    position: relative;
    display: flex;
    align-items: center;
    gap: 0.7rem;
    padding: 0.55rem 0.7rem;
    border: 0;
    border-radius: 8px;
    background: none;
    color: var(--muted);
    text-align: left;
    transition:
      background 150ms var(--ease),
      color 150ms var(--ease);
  }

  .nav:hover {
    background: var(--hover);
    color: var(--text);
  }

  .nav.selected {
    background: var(--selected);
    color: var(--text);
    font-weight: 600;
  }

  /* The accent bar on the selected section. */
  .nav.selected::before {
    content: "";
    position: absolute;
    left: -0.7rem;
    top: 25%;
    bottom: 25%;
    width: 3px;
    border-radius: 0 3px 3px 0;
    background: var(--accent);
  }

  .count {
    margin-left: auto;
    padding: 0 0.45rem;
    border-radius: 999px;
    background: var(--hover);
    font-size: 0.78rem;
  }

  .badge {
    width: 8px;
    height: 8px;
    margin-left: auto;
    border-radius: 50%;
    background: var(--accent);
  }

  main {
    display: flex;
    flex-direction: column;
    min-width: 0;
    min-height: 0;
  }

  header {
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 1rem;
    padding: 1.2rem 1.6rem 0.9rem;
  }

  h1 {
    font-size: 1.35rem;
    font-weight: 600;
  }

  .pill {
    display: inline-flex;
    align-items: center;
    gap: 0.45rem;
    padding: 0.25rem 0.7rem;
    border-radius: 999px;
    background: var(--error-bg);
    color: var(--danger);
    font-size: 0.82rem;
    font-weight: 600;
    transition:
      background 300ms var(--ease),
      color 300ms var(--ease);
  }

  .pill.ok {
    background: var(--ok-bg);
    color: var(--ok-fg);
  }

  .dot {
    width: 7px;
    height: 7px;
    border-radius: 50%;
    background: currentColor;
  }

  .banner {
    margin: 0 1.6rem 0.8rem;
    padding: 0.7rem 0.9rem;
    border-radius: var(--radius);
    background: var(--warn-bg);
    color: var(--warn-fg);
  }

  .scroll {
    flex: 1;
    overflow-y: auto;
    padding: 0.2rem 1.6rem 1.6rem;
  }

  .page {
    max-width: 820px;
  }

  .skeleton {
    display: grid;
    gap: 0.8rem;
    max-width: 820px;
  }

  .skeleton div {
    height: 88px;
    border-radius: var(--radius);
    background: linear-gradient(90deg, var(--surface) 25%, var(--hover) 50%, var(--surface) 75%);
    background-size: 200% 100%;
    animation: shimmer 1.2s linear infinite;
  }

  @keyframes shimmer {
    to {
      background-position: -200% 0;
    }
  }
</style>
