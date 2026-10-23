<script lang="ts">
  import type { HostEntry, Vm } from "$lib/api/client";
  import type { Detail } from "$lib/catalogBrowser";
  import type { SoftwareDraft } from "$lib/softwareDraft.svelte";
  import BrowserSection from "./BrowserSection.svelte";
  import InstallSection from "./InstallSection.svelte";
  import RemoveSection from "./RemoveSection.svelte";
  import TweaksSection from "./TweaksSection.svelte";

  interface Props {
    host: HostEntry;
    vms: Vm[];
    draft: SoftwareDraft;
    /** One of SOFTWARE_TABS. */
    tab: string;
    selected: string | null;
    onselect: (detail: Detail) => void;
  }

  let { host, vms, draft, tab, selected, onselect }: Props = $props();
</script>

{#if tab === "packages"}
  <InstallSection {host} bind:install={draft.install} {selected} {onselect} />
{:else if tab === "remove"}
  <RemoveSection
    {host}
    {vms}
    catalog={draft.catalog?.appx ?? []}
    bind:appx={draft.appx}
    bind:capabilities={draft.capabilities}
    bind:features={draft.features}
    {selected}
    {onselect}
  />
{:else if tab === "tweaks"}
  <TweaksSection catalog={draft.catalog?.tweaks ?? []} bind:tweakIds={draft.tweakIds} bind:custom={draft.custom} {selected} {onselect} />
{:else}
  <BrowserSection
    {host}
    browsers={draft.catalog?.browsers ?? []}
    browser={draft.browser}
    browserPolicies={draft.browserPolicies}
    extensionCatalog={draft.extensionCatalog}
    bind:browserApp={draft.browserApp}
    bind:extensions={draft.extensions}
    policies={draft.policies}
    {selected}
    {onselect}
  />
{/if}
