<script lang="ts">
  import {
    captureSetupProfile,
    errorMessage,
    hasProblemCode,
    isClientError,
    ProblemCodes,
    saveSetupProfile,
    type DraftItem,
    type HostEntry,
    type ProfileDraft,
    type StoredSetupProfile,
    type ValidationIssue,
    type Vm,
  } from "$lib/api/client";
  import { ElevationCancelled, withElevation } from "$lib/lifecycle.svelte";
  import { draftToProfile, initialChoices, type CaptureChoices } from "$lib/setupProfiles";
  import { onMount } from "svelte";
  import Dialog from "./Dialog.svelte";

  interface Props {
    host: HostEntry;
    vm: Vm;
    onclose: (saved: StoredSetupProfile | null) => void;
  }

  let { host, vm, onclose }: Props = $props();

  let draft = $state<ProfileDraft | null>(null);
  let choices = $state<CaptureChoices | null>(null);
  let error = $state<string | null>(null);
  let issues = $state<ValidationIssue[]>([]);
  let saving = $state(false);

  const browser = $derived(draft?.browsers.find((item) => item.app === choices?.browser) ?? null);

  onMount(async () => {
    try {
      draft = await captureSetupProfile(host.key, vm.id);
      choices = initialChoices(draft);
    } catch (e) {
      error = hasProblemCode(e, ProblemCodes.credentialRequired)
        ? `HyperHarbor has no administrator credential for ${vm.name}. Set it up for Remote Desktop first.`
        : errorMessage(e);
    }
  });

  /** Sets are replaced, not changed in place, so the checkboxes follow. */
  function toggle(column: "install" | "removeAppx" | "tweaks" | "extensions", id: string, on: boolean) {
    if (!choices) return;
    const current = [...choices[column]];
    choices = { ...choices, [column]: new Set(on ? [...current, id] : current.filter((item) => item !== id)) };
  }

  function all(column: "install" | "removeAppx" | "tweaks" | "extensions", items: DraftItem[], on: boolean) {
    if (!choices) return;
    choices = { ...choices, [column]: new Set(on ? items.map((item) => item.id) : []) };
  }

  function chooseBrowser(app: string | null) {
    if (!choices || !draft) return;
    const chosen = draft.browsers.find((item) => item.app === app);
    choices = {
      ...choices,
      browser: app,
      extensions: new Set((chosen?.extensions ?? []).filter((item) => item.selected).map((item) => item.id)),
    };
  }

  async function save() {
    if (!draft || !choices) return;
    saving = true;
    error = null;
    issues = [];
    try {
      const profile = draftToProfile(draft, choices);
      onclose(await withElevation(host.key, () => saveSetupProfile(host.key, null, profile)));
    } catch (e) {
      if (e instanceof ElevationCancelled) return;
      error = errorMessage(e);
      issues = isClientError(e) ? e.issues : [];
    } finally {
      saving = false;
    }
  }
</script>

{#snippet column(title: string, key: "install" | "removeAppx" | "tweaks" | "extensions", items: DraftItem[], empty: string)}
  <div class="group">
    <div class="group-head">
      <h4>{title}</h4>
      {#if items.length > 0}
        <span class="links">
          <button type="button" class="link" onclick={() => all(key, items, true)}>All</button>
          <button type="button" class="link" onclick={() => all(key, items, false)}>None</button>
        </span>
      {/if}
    </div>
    {#if items.length === 0}<p class="muted">{empty}</p>{/if}
    {#each items as item (item.id)}
      <label class="check" title={item.note ?? item.id}>
        <input type="checkbox" checked={choices?.[key].has(item.id)} onchange={(event) => toggle(key, item.id, event.currentTarget.checked)} />
        <span class="item">
          {item.name}
          {#if item.name !== item.id}<code>{item.id}</code>{/if}
          {#if item.note}<small>{item.note}</small>{/if}
        </span>
      </label>
    {/each}
  </div>
{/snippet}

<Dialog title="Capture a setup profile from {vm.name}" xl>
  <div class="review">
    {#if !draft && !error}
      <p class="muted" role="status">
        Reading {vm.name}: installed packages, provisioned apps, settings, and browser extensions. This takes a minute or two.
      </p>
    {/if}

    {#if draft && choices}
      <p class="muted">
        Windows {draft.build} {draft.edition}. Untick anything the profile should not have, name it, and save. Nothing in
        the VM changes.
      </p>
      {#each draft.warnings as warning (warning)}<p class="warning">{warning}</p>{/each}

      <div class="columns">
        <section>
          {@render column("Install", "install", draft.install, "winget found nothing to install.")}
          {#if draft.otherPrograms.length > 0}
            <h4>Installed programs</h4>
            <p class="muted">Not matched to winget packages; add the ones you want in the editor.</p>
            <ul class="programs">{#each draft.otherPrograms as program (program)}<li>{program}</li>{/each}</ul>
          {/if}
        </section>
        <section>
          {@render column("Remove", "removeAppx", draft.removeAppx, draft.baseline ? "Nothing was removed from the clean install." : "No clean baseline to compare with.")}
        </section>
        <section>
          <h4>Browser</h4>
          <label class="check">
            <input type="radio" name="browser" checked={choices.browser === null} onchange={() => chooseBrowser(null)} /> None
          </label>
          {#each draft.browsers as item (item.app)}
            <label class="check">
              <input type="radio" name="browser" checked={choices.browser === item.app} onchange={() => chooseBrowser(item.app)} />
              {item.name} ({item.extensions.length} extensions)
            </label>
          {/each}
          {#if browser}
            {@render column(`${browser.name} extensions`, "extensions", browser.extensions, "No store extensions.")}
          {/if}
          {@render column("Tweaks", "tweaks", draft.tweaks, "No catalog tweaks are set in this VM.")}
        </section>
      </div>
    {/if}

    {#if error}
      <div class="error" role="alert">
        {error}
        {#if issues.length > 0}<ul>{#each issues as issue (issue.field)}<li><code>{issue.field}</code> {issue.message}</li>{/each}</ul>{/if}
      </div>
    {/if}

    <div class="actions">
      {#if choices}
        <label class="name">Name <input bind:value={choices.name} maxlength="60" /></label>
      {/if}
      <button type="button" onclick={() => onclose(null)} disabled={saving}>Cancel</button>
      <button type="button" class="primary" onclick={save} disabled={!draft || saving || !choices?.name.trim()}>
        {saving ? "Saving…" : "Save profile"}
      </button>
    </div>
  </div>
</Dialog>

<style>
  .review {
    flex: 1;
    min-height: 0;
    display: flex;
    flex-direction: column;
    gap: 0.4rem;
  }

  .columns {
    flex: 1;
    min-height: 0;
    display: grid;
    grid-template-columns: repeat(3, 1fr);
    gap: 1rem;
  }

  section {
    overflow-y: auto;
    border: 1px solid var(--border);
    border-radius: 6px;
    padding: 0.5rem 0.75rem;
  }

  .group-head {
    display: flex;
    align-items: baseline;
    justify-content: space-between;
  }

  h4 {
    margin: 0.4rem 0;
    font-size: 0.9rem;
  }

  .item {
    display: flex;
    flex-direction: column;
  }

  code,
  small {
    font-size: 0.75rem;
    color: var(--muted);
  }

  .programs {
    margin: 0;
    padding-left: 1rem;
    font-size: 0.85rem;
  }

  .links {
    display: flex;
    gap: 0.5rem;
  }

  button.link {
    padding: 0 !important;
    border: none !important;
    background: none !important;
    text-decoration: underline;
    font-size: 0.8rem;
  }

  .name {
    display: flex;
    align-items: center;
    gap: 0.4rem;
    margin-right: auto;
  }
</style>
