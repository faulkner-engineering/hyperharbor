<script lang="ts">
  import Card from "../components/Card.svelte";
  import ConfirmDialog from "../components/ConfirmDialog.svelte";
  import { tray } from "../store.svelte";
  import type { TrayViewState } from "../bridge";

  interface Props {
    view: TrayViewState;
  }

  let { view }: Props = $props();

  let confirming = $state<"console" | "packageSearch" | null>(null);

  const consoleBusy = $derived(tray.isBusy("console"));
  const searchBusy = $derived(tray.isBusy("packageSearch"));

  function start(which: "console" | "packageSearch") {
    confirming = null;
    tray.send({ type: which === "console" ? "setUpConsole" : "setUpPackageSearch" }, which);
  }
</script>

<div class="cards">
  <Card
    icon="lock"
    title="Admin passphrase"
    status={view.passphraseConfigured === null
      ? "Unknown while the service is not running."
      : view.passphraseConfigured
        ? "Set"
        : "Not set"}
    tone={view.passphraseConfigured ? "ok" : view.passphraseConfigured === false ? "warn" : "neutral"}
  >
    {view.passphraseConfigured === false
      ? "Paired devices cannot create, change, or delete VMs until you set one."
      : "Paired devices enter it before they create, change, or delete VMs."}
    {#snippet action()}
      <button type="button" class="btn" disabled={!view.connected} onclick={() => (tray.passphraseOpen = true)}>
        {view.passphraseConfigured ? "Change…" : "Set passphrase…"}
      </button>
    {/snippet}
  </Card>

  <Card
    icon="console"
    title="VM console"
    status={consoleBusy ? "Setting up… approve the administrator prompt" : view.consoleReady ? "Set up" : "Not set up"}
    tone={consoleBusy ? "busy" : view.consoleReady ? "ok" : "warn"}
  >
    {view.consoleReady
      ? "Paired devices can open the console of any running VM."
      : "Paired devices cannot open VM consoles until you set it up. Windows asks for administrator permission."}
    {#snippet action()}
      <button type="button" class="btn" disabled={consoleBusy} onclick={() => (confirming = "console")}>
        {view.consoleReady ? "Set up again…" : "Set up…"}
      </button>
    {/snippet}
  </Card>

  <Card
    icon="search"
    title="Package search"
    status={searchBusy ? "Installing… this can take a few minutes" : view.packageSearchReady ? "Set up" : "Not set up"}
    tone={searchBusy ? "busy" : view.packageSearchReady ? "ok" : "warn"}
  >
    {view.packageSearchReady
      ? "Paired devices can search winget packages for setup profiles."
      : "Searching packages for setup profiles needs PowerShell 7 and the WinGet PowerShell module."}
    {#snippet action()}
      {#if !view.packageSearchReady}
        <button type="button" class="btn" disabled={searchBusy} onclick={() => (confirming = "packageSearch")}>Set up…</button>
      {/if}
    {/snippet}
  </Card>
</div>

{#if confirming === "console"}
  <ConfirmDialog
    title="Set up console access"
    message="HyperHarbor creates a standard local account on this PC for each HyperHarbor user (for example hhc-owner). It cannot sign in to Windows; paired devices use it only to open VM consoles, and its password changes every time they do. Windows asks for administrator permission."
    confirmLabel="Set up"
    onconfirm={() => start("console")}
    oncancel={() => (confirming = null)}
  />
{:else if confirming === "packageSearch"}
  <ConfirmDialog
    title="Set up package search"
    message="HyperHarbor installs PowerShell 7 and the WinGet PowerShell module (Microsoft.WinGet.Client) for all users on this PC, downloaded from Microsoft and the PowerShell Gallery, so the host can search winget packages for setup profiles. Windows asks for administrator permission."
    confirmLabel="Install"
    onconfirm={() => start("packageSearch")}
    oncancel={() => (confirming = null)}
  />
{/if}

<style>
  .cards {
    display: grid;
    gap: 0.8rem;
  }
</style>
