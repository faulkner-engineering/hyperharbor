<script lang="ts">
  import { onDestroy, onMount } from "svelte";
  import { getGameAware, type HostEntry } from "$lib/api/client";
  import { gamingNotice } from "$lib/gameAware";

  interface Props {
    host: HostEntry;
  }

  let { host }: Props = $props();

  const POLL_MS = 10_000;

  let text = $state<string | null>(null);
  let timer: ReturnType<typeof setInterval> | undefined;

  async function load() {
    try {
      text = gamingNotice((await getGameAware(host.key)).state);
    } catch {
      // The notice is a courtesy: a host that cannot answer shows nothing.
      text = null;
    }
  }

  onMount(() => {
    void load();
    timer = setInterval(() => void load(), POLL_MS);
  });
  onDestroy(() => clearInterval(timer));
</script>

{#if text}
  <div class="notice" role="status">
    <p class="notice-title">{text}</p>
  </div>
{/if}
