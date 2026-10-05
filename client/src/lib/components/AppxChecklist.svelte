<script lang="ts">
  import {
    errorMessage,
    hasProblemCode,
    listVmAppx,
    ProblemCodes,
    recordAppxBaseline,
    type AppxPackage,
    type HostEntry,
    type ProfileItem,
    type Vm,
    type VmAppxInventory,
  } from "$lib/api/client";
  import { untrack } from "svelte";
  import { SvelteSet } from "svelte/reactivity";
  import { debloatPreset, groupByPublisher } from "$lib/setupProfiles";
  import { toasts } from "$lib/toasts.svelte";

  interface Props {
    host: HostEntry;
    /** Running Windows VMs to read packages from. */
    vms: Vm[];
    /** The profile's remove.appx list; updated as boxes are ticked. */
    items: ProfileItem[];
  }

  let { host, vms, items = $bindable() }: Props = $props();

  // The first VM is the starting choice; the list can change while the editor is open.
  let vmId = $state(untrack(() => vms[0]?.id ?? ""));
  let inventory = $state<VmAppxInventory | null>(null);
  let loading = $state(false);
  let recording = $state(false);
  let error = $state<string | null>(null);
  let allowKeep = $state(false);
  const selected = new SvelteSet<string>(untrack(() => items.map((item) => item.id)));

  const groups = $derived(inventory ? groupByPublisher(inventory.packages) : []);
  // Names already in the profile that this VM no longer has (removed before), so they stay listed.
  const elsewhere = $derived(inventory ? items.filter((item) => !inventory!.packages.some((appx) => appx.name === item.id)) : []);

  async function load() {
    if (!vmId) return;
    loading = true;
    error = null;
    try {
      inventory = await listVmAppx(host.key, vmId);
    } catch (e) {
      inventory = null;
      error = hasProblemCode(e, ProblemCodes.credentialRequired)
        ? "HyperHarbor has no administrator credential for this VM. Set it up for Remote Desktop first."
        : errorMessage(e);
    } finally {
      loading = false;
    }
  }

  async function recordBaseline() {
    recording = true;
    try {
      const baseline = await recordAppxBaseline(host.key, vmId);
      toasts.show(`Recorded the clean baseline for ${baseline.key}.`);
      await load();
    } catch (e) {
      toasts.error(errorMessage(e));
    } finally {
      recording = false;
    }
  }

  function sync() {
    const known = new Map((inventory?.packages ?? []).map((appx) => [appx.name, appx.friendlyName]));
    const names = new Map(items.map((item) => [item.id, item.name]));
    items = [...selected].sort().map((id) => ({ id, name: known.get(id) ?? names.get(id) ?? undefined }));
  }

  function toggle(appx: AppxPackage, on: boolean) {
    if (on) selected.add(appx.name);
    else selected.delete(appx.name);
    sync();
  }

  function preset() {
    for (const name of debloatPreset(inventory?.packages ?? [])) selected.add(name);
    sync();
  }

  function clearAll() {
    selected.clear();
    sync();
  }

  const locked = (appx: AppxPackage) => appx.rating === "keep" && !allowKeep && !selected.has(appx.name);
</script>

<div class="appx">
  {#if vms.length === 0}
    <p class="muted">Start a Windows VM that HyperHarbor set up to read its provisioned packages, or add names below by hand.</p>
  {:else}
    <div class="row">
      <label for="appx-vm">Read packages from</label>
      <select id="appx-vm" bind:value={vmId}>
        {#each vms as vm (vm.id)}<option value={vm.id}>{vm.name}</option>{/each}
      </select>
      <button type="button" onclick={load} disabled={loading}>{loading ? "Reading…" : inventory ? "Read again" : "Read packages"}</button>
    </div>
  {/if}
  {#if error}<p class="error" role="alert">{error}</p>{/if}

  {#if inventory}
    <p class="muted">
      Windows {inventory.build} {inventory.edition}.
      {#if inventory.baseline}
        Compared with the clean baseline{inventory.baseline.approximate ? ` for ${inventory.baseline.edition}` : ""}.
      {:else}
        No clean baseline for this build yet.
        <button type="button" class="link" onclick={recordBaseline} disabled={recording}>Record this VM as clean</button>
      {/if}
    </p>
    <div class="row">
      <button type="button" onclick={preset}>Debloat preset</button>
      <button type="button" onclick={clearAll}>Clear</button>
      <label class="check"><input type="checkbox" bind:checked={allowKeep} /> Allow removing packages to keep</label>
    </div>
    <div class="groups">
      {#each groups as group (group.publisher)}
        <fieldset>
          <legend>{group.publisher}</legend>
          {#each group.packages as appx (appx.name)}
            <label class="check" title={appx.note ?? appx.name}>
              <input
                type="checkbox"
                checked={selected.has(appx.name)}
                disabled={locked(appx)}
                onchange={(event) => toggle(appx, event.currentTarget.checked)}
              />
              {appx.friendlyName}
              <span class="badge {appx.rating}">{appx.rating}</span>
            </label>
          {/each}
        </fieldset>
      {/each}
    </div>
  {/if}

  {#if elsewhere.length > 0}
    <p class="muted">Also removed (not in this VM): {elsewhere.map((item) => item.name ?? item.id).join(", ")}</p>
  {/if}
</div>

<style>
  .appx {
    display: flex;
    flex-direction: column;
    gap: 0.4rem;
  }

  .row {
    display: flex;
    align-items: center;
    gap: 0.5rem;
    flex-wrap: wrap;
  }

  .groups {
    display: grid;
    grid-template-columns: repeat(auto-fill, minmax(16rem, 1fr));
    gap: 0.5rem;
    max-height: 24rem;
    overflow-y: auto;
  }

  fieldset {
    border: 1px solid var(--border);
    border-radius: 6px;
    padding: 0.4rem 0.6rem;
    margin: 0;
  }

  legend {
    font-weight: 600;
    font-size: 0.85rem;
  }

  .badge {
    margin-left: auto;
    padding: 0 0.4rem;
    border-radius: 999px;
    font-size: 0.7rem;
    background: var(--idle-bg);
    color: var(--muted);
  }

  .badge.safe {
    background: var(--ok-bg);
    color: var(--ok-fg);
  }

  .badge.caution {
    background: var(--warn-bg);
    color: var(--warn-fg);
  }

  .badge.keep {
    background: var(--error-bg);
    color: var(--danger);
  }

  button.link {
    padding: 0 !important;
    border: none !important;
    background: none !important;
    color: var(--accent-text, inherit);
    text-decoration: underline;
  }
</style>
