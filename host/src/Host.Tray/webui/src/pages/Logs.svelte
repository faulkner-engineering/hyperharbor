<script lang="ts">
  import Card from "../components/Card.svelte";
  import type { TrayViewState } from "../bridge";
  import { tray } from "../store.svelte";

  interface Props {
    view: TrayViewState;
  }

  let { view }: Props = $props();
</script>

<div class="cards">
  <Card icon="logs" title="Host log" status="Daily files, kept for 14 days">
    <span class="mono">{view.dataDirectory}\logs</span>
    {#snippet action()}
      <button type="button" class="btn" onclick={() => tray.send({ type: "open", target: "logs" })}>Open folder</button>
    {/snippet}
  </Card>
  <Card icon="lock" title="Audit log" status="Every change made from a client or this window">
    <span class="mono">{view.dataDirectory}\audit.log</span>
    {#snippet action()}
      <button type="button" class="btn" onclick={() => tray.send({ type: "open", target: "audit" })}>Open</button>
    {/snippet}
  </Card>
</div>

<style>
  .cards {
    display: grid;
    gap: 0.8rem;
  }

  .mono {
    user-select: text;
  }
</style>
