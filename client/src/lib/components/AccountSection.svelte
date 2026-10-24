<script lang="ts">
  import { errorMessage, hostSupports, listUnattendProfiles, ProfilesConsolidatedApiVersion, type HostEntry, type UnattendProfile } from "$lib/api/client";
  import { DEFAULT_EDITION, defaultLinux, defaultWindows, type MachineDraft } from "$lib/machineDraft.svelte";
  import { onMount } from "svelte";

  interface Props {
    host: HostEntry;
    draft: MachineDraft;
  }

  let { host, draft }: Props = $props();

  let unattend = $state<UnattendProfile[]>([]);
  let error = $state<string | null>(null);
  const windows = $derived(draft.os === "windows");
  const sources = $derived(unattend.filter((profile) => profile.os === draft.os));

  onMount(async () => {
    // A host at 1.32.0 converted its install profiles into machine profiles, so there is nothing to fill from.
    if (hostSupports(host, ProfilesConsolidatedApiVersion)) return;
    try {
      unattend = await listUnattendProfiles(host.key);
    } catch (e) {
      error = errorMessage(e);
    }
  });

  // The OS-specific settings exist whenever the section is on, so the fields below can bind to them.
  $effect(() => {
    const account = draft.account;
    if (!account) return;
    if (windows && !account.windows) account.windows = defaultWindows();
    if (!windows && !account.linux) account.linux = defaultLinux();
  });

  function include(on: boolean) {
    draft.account = on
      ? { adminAccountName: "hhadmin", locale: "en-US", windows: windows ? defaultWindows() : null, linux: windows ? null : defaultLinux() }
      : null;
  }

  function fill(id: string) {
    const from = unattend.find((profile) => profile.id === id);
    if (from) draft.fillAccount(from);
  }

  function lines(text: string): string[] {
    return text
      .split("\n")
      .map((line) => line.trim())
      .filter((line) => line !== "");
  }
</script>

<label class="check">
  <input type="checkbox" checked={draft.account !== null} onchange={(event) => include(event.currentTarget.checked)} />
  Install {windows ? "Windows" : "Ubuntu"} unattended with these settings
</label>
<p class="muted">
  Off: the OS is installed from the console. On: HyperHarbor answers Setup's questions, creates the administrator with a
  one-time password, and rotates it when setup is done.
</p>

{#if draft.account}
  {@const account = draft.account}
  {#if sources.length > 0}
    <label>
      Fill from an unattended install profile
      <select value="" onchange={(event) => fill(event.currentTarget.value)}>
        <option value="" disabled>Choose…</option>
        {#each sources as source (source.id)}<option value={source.id}>{source.name}</option>{/each}
      </select>
    </label>
  {/if}
  {#if error}<p class="error" role="alert">{error}</p>{/if}

  <div class="grid">
    <label>Administrator account <input bind:value={account.adminAccountName} placeholder="hhadmin" /></label>
    <label>Language <input bind:value={account.locale} placeholder="en-US" /></label>
    <label>
      Time zone
      <input
        value={account.timeZone ?? ""}
        oninput={(event) => (account.timeZone = event.currentTarget.value || null)}
        placeholder={windows ? "The host's (for example Central Standard Time)" : "The host's (for example America/Chicago)"}
      />
    </label>
    {#if windows}
      <label>
        Computer name
        <input value={account.computerName ?? ""} oninput={(event) => (account.computerName = event.currentTarget.value || null)} maxlength="15" placeholder="From the VM name" />
      </label>
    {/if}
  </div>

  {#if windows && account.windows}
    {@const settings = account.windows}
    <label>Edition <input bind:value={settings.defaultEdition} placeholder={DEFAULT_EDITION} /></label>
    <label class="check"><input type="checkbox" bind:checked={settings.bypassHardwareChecks} /> Bypass Windows 11's TPM, Secure Boot, and memory checks</label>
    <fieldset>
      <legend>Privacy during setup</legend>
      <label class="check"><input type="checkbox" checked={settings.disableTelemetry ?? true} onchange={(event) => (settings.disableTelemetry = event.currentTarget.checked)} /> Send only required diagnostic data</label>
      <label class="check"><input type="checkbox" checked={settings.disableAdvertisingId ?? true} onchange={(event) => (settings.disableAdvertisingId = event.currentTarget.checked)} /> Turn off the advertising ID</label>
      <label class="check"><input type="checkbox" checked={settings.disableLocation ?? true} onchange={(event) => (settings.disableLocation = event.currentTarget.checked)} /> Turn off location</label>
      <label class="check"><input type="checkbox" checked={settings.disableConsumerFeatures ?? true} onchange={(event) => (settings.disableConsumerFeatures = event.currentTarget.checked)} /> Turn off suggested apps</label>
    </fieldset>
  {:else if !windows && account.linux}
    {@const settings = account.linux}
    <label>
      SSH public keys, one per line
      <textarea rows="3" value={(settings.sshAuthorizedKeys ?? []).join("\n")} oninput={(event) => (settings.sshAuthorizedKeys = lines(event.currentTarget.value))}></textarea>
    </label>
    <label>
      Extra apt packages
      <input value={(settings.packages ?? []).join(" ")} oninput={(event) => (settings.packages = event.currentTarget.value.split(/[\s,]+/).filter((item) => item !== ""))} placeholder="For example htop git" />
    </label>
    <label class="check"><input type="checkbox" checked={settings.installDesktop ?? true} onchange={(event) => (settings.installDesktop = event.currentTarget.checked)} /> Install a desktop and xrdp, so Remote Desktop works</label>
  {/if}
{/if}

<style>
  label:not(.check) {
    display: flex;
    flex-direction: column;
    gap: 0.2rem;
    margin-bottom: 0.6rem;
  }

  .grid {
    display: grid;
    grid-template-columns: repeat(auto-fit, minmax(12rem, 1fr));
    gap: 0 0.75rem;
  }

  fieldset {
    border: 1px solid var(--border);
    border-radius: 6px;
    padding: 0.4rem 0.7rem;
    margin: 0.4rem 0 0;
  }

  legend {
    font-size: 0.85rem;
    color: var(--muted);
  }

  textarea {
    font-family: ui-monospace, "Cascadia Mono", Consolas, monospace;
    font-size: 0.8rem;
  }
</style>
