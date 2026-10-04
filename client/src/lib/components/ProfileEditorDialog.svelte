<script lang="ts">
  import { untrack } from "svelte";
  import {
    errorMessage,
    isClientError,
    saveUnattendProfile,
    type HostEntry,
    type UnattendProfile,
    type UnattendProfileRequest,
    type ValidationIssue,
  } from "$lib/api/client";
  import { ElevationCancelled, withElevation } from "$lib/lifecycle.svelte";
  import Dialog from "./Dialog.svelte";

  interface Props {
    host: HostEntry;
    /** Null creates a new profile. */
    profileId: string | null;
    initial: UnattendProfileRequest;
    onclose: (saved: UnattendProfile | null) => void;
  }

  let { host, profileId, initial, onclose }: Props = $props();

  // The form edits a copy; nothing changes on the host until Save.
  const start = untrack(() => initial);
  let name = $state(start.name);
  let adminAccountName = $state(start.adminAccountName ?? "hhadmin");
  let timeZone = $state(start.timeZone ?? "");
  let locale = $state(start.locale ?? "en-US");
  let defaultEdition = $state(start.windows?.defaultEdition ?? "Windows 11 Pro");
  let bypassHardwareChecks = $state(start.windows?.bypassHardwareChecks ?? false);
  let disableTelemetry = $state(start.windows?.disableTelemetry ?? true);
  let disableAdvertisingId = $state(start.windows?.disableAdvertisingId ?? true);
  let disableLocation = $state(start.windows?.disableLocation ?? true);
  let disableConsumerFeatures = $state(start.windows?.disableConsumerFeatures ?? true);
  let sshKeys = $state((start.linux?.sshAuthorizedKeys ?? []).join("\n"));
  let packages = $state((start.linux?.packages ?? []).join(" "));
  let installDesktop = $state(start.linux?.installDesktop ?? true);

  const os = start.os;
  let busy = $state(false);
  let error = $state<string | null>(null);
  let issues = $state<ValidationIssue[]>([]);

  function issueFor(field: string): string | undefined {
    return issues.find((issue) => issue.field === field)?.message;
  }

  function request(): UnattendProfileRequest {
    const common = {
      name: name.trim(),
      os,
      adminAccountName: adminAccountName.trim(),
      timeZone: timeZone.trim() === "" ? null : timeZone.trim(),
      locale: locale.trim(),
    };
    return os === "windows"
      ? {
          ...common,
          windows: {
            defaultEdition: defaultEdition.trim(),
            bypassHardwareChecks,
            disableTelemetry,
            disableAdvertisingId,
            disableLocation,
            disableConsumerFeatures,
          },
          linux: null,
        }
      : {
          ...common,
          windows: null,
          linux: {
            sshAuthorizedKeys: sshKeys
              .split("\n")
              .map((key) => key.trim())
              .filter((key) => key !== ""),
            packages: packages.split(/[\s,]+/).filter((item) => item !== ""),
            installDesktop,
          },
        };
  }

  async function save(event: SubmitEvent) {
    event.preventDefault();
    busy = true;
    error = null;
    issues = [];
    try {
      const saved = await withElevation(host.key, () => saveUnattendProfile(host.key, profileId, request()));
      onclose(saved);
    } catch (e) {
      if (e instanceof ElevationCancelled) return;
      error = errorMessage(e);
      issues = isClientError(e) ? e.issues : [];
    } finally {
      busy = false;
    }
  }
</script>

<Dialog title={profileId === null ? `New ${os === "windows" ? "Windows" : "Linux"} profile` : `Edit ${start.name}`} wide>
  <form onsubmit={save}>
    <p class="muted">
      Profiles never hold passwords. Each install gets a one-time administrator password that the host changes once
      setup is done.
    </p>

    <label for="profile-name">Name</label>
    <input id="profile-name" bind:value={name} maxlength="60" autocomplete="off" />
    {#if issueFor("name")}<p class="error">{issueFor("name")}</p>{/if}

    <div class="grid">
      <div>
        <label for="profile-admin">Administrator account</label>
        <input id="profile-admin" bind:value={adminAccountName} maxlength={os === "windows" ? 20 : 32} autocomplete="off" spellcheck="false" />
        {#if issueFor("adminAccountName")}<p class="error">{issueFor("adminAccountName")}</p>{/if}
      </div>
      <div>
        <label for="profile-locale">Language</label>
        <input id="profile-locale" bind:value={locale} maxlength="6" autocomplete="off" spellcheck="false" />
        {#if issueFor("locale")}<p class="error">{issueFor("locale")}</p>{/if}
      </div>
    </div>

    <label for="profile-zone">Time zone</label>
    <input
      id="profile-zone"
      bind:value={timeZone}
      placeholder={os === "windows" ? "The host's (for example Central Standard Time)" : "The host's (for example America/Chicago)"}
      autocomplete="off"
      spellcheck="false"
    />
    {#if issueFor("timeZone")}<p class="error">{issueFor("timeZone")}</p>{/if}

    {#if os === "windows"}
      <label for="profile-edition">Default edition</label>
      <input id="profile-edition" bind:value={defaultEdition} maxlength="100" autocomplete="off" />
      {#if issueFor("windows.defaultEdition")}<p class="error">{issueFor("windows.defaultEdition")}</p>{/if}

      <label class="check">
        <input type="checkbox" bind:checked={bypassHardwareChecks} />
        Skip the Windows 11 TPM, Secure Boot, and memory checks
      </label>
      <label class="check"><input type="checkbox" bind:checked={disableTelemetry} /> Turn off optional diagnostic data</label>
      <label class="check"><input type="checkbox" bind:checked={disableAdvertisingId} /> Turn off the advertising ID</label>
      <label class="check"><input type="checkbox" bind:checked={disableLocation} /> Turn off location</label>
      <label class="check"><input type="checkbox" bind:checked={disableConsumerFeatures} /> Turn off suggested apps</label>
    {:else}
      <label for="profile-keys">SSH public keys (one per line)</label>
      <textarea id="profile-keys" bind:value={sshKeys} rows="3" spellcheck="false" placeholder="ssh-ed25519 AAAA… you@laptop"></textarea>
      {#if issueFor("linux.sshAuthorizedKeys")}<p class="error">{issueFor("linux.sshAuthorizedKeys")}</p>{/if}

      <label for="profile-packages">Extra packages</label>
      <input id="profile-packages" bind:value={packages} autocomplete="off" spellcheck="false" placeholder="git build-essential curl" />
      {#if issueFor("linux.packages")}<p class="error">{issueFor("linux.packages")}</p>{/if}

      <label class="check">
        <input type="checkbox" bind:checked={installDesktop} />
        Install a desktop (Xfce and xrdp), so Remote Desktop works
      </label>
    {/if}

    {#if error}<p class="error" role="alert">{error}</p>{/if}
    <div class="actions">
      <button type="button" onclick={() => onclose(null)} disabled={busy}>Cancel</button>
      <button type="submit" class="primary" disabled={busy || name.trim() === ""}>{busy ? "Saving…" : "Save"}</button>
    </div>
  </form>
</Dialog>

<style>
  .grid {
    display: grid;
    grid-template-columns: 1fr 1fr;
    gap: 0.4rem 1rem;
  }

  .grid > div {
    display: flex;
    flex-direction: column;
    gap: 0.3rem;
  }

  textarea {
    resize: vertical;
    font-family: ui-monospace, "Cascadia Mono", Consolas, monospace;
    font-size: 0.8rem;
  }
</style>
