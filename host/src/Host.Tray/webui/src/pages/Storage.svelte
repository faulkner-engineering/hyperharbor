<script lang="ts">
  import Card from "../components/Card.svelte";
  import type { FolderKind, TrayViewState } from "../bridge";
  import { tray } from "../store.svelte";

  interface Props {
    view: TrayViewState;
  }

  let { view }: Props = $props();

  const folders = $derived<{ kind: FolderKind; title: string; folder: string | null; detail: string }[]>([
    {
      kind: "vm",
      title: "VM storage",
      folder: view.vmFolder ? `${view.vmFolder.folder}${view.vmFolder.isDefault ? " (the Hyper-V default)" : ""}` : null,
      detail: "New VMs are created here, each in its own folder. Existing VMs stay where they are.",
    },
    {
      kind: "iso",
      title: "ISO library",
      folder: view.isoFolder,
      detail: "Images clients add are stored here. Images already in an earlier folder stay there.",
    },
    {
      kind: "backup",
      title: "Disk exports",
      folder: view.backupFolder,
      detail:
        "Copies of a stopped VM's disks (for example before Performance mode) go here when a client does not choose a folder. " +
        "These are not the VM backups; those go to the targets set up from a client (Host menu, Backups).",
    },
  ]);
</script>

<div class="cards">
  {#each folders as item (item.kind)}
    {@const busy = tray.isBusy(`folder:${item.kind}`)}
    <Card
      icon="folder"
      title={item.title}
      status={busy ? "Changing…" : (item.folder ?? (view.connected ? "Loading…" : "Unknown while the service is not running."))}
      tone={busy ? "busy" : "neutral"}
    >
      {item.detail}
      {#snippet action()}
        <button
          type="button"
          class="btn"
          disabled={!view.connected || item.folder === null || busy}
          onclick={() => tray.send({ type: "chooseFolder", folder: item.kind })}>Change…</button
        >
      {/snippet}
    </Card>
  {/each}
</div>

<style>
  .cards {
    display: grid;
    gap: 0.8rem;
  }

  .cards :global(.status) {
    user-select: text;
    word-break: break-all;
  }
</style>
