<script lang="ts">
  import { onMount, tick } from "svelte";
  import HostWindow from "./HostWindow.svelte";
  import PinWindow from "./PinWindow.svelte";
  import { tray } from "./store.svelte";

  // The tray opens index.html#/host or #/pin; each window shows one of them.
  const route = location.hash.replace(/^#\/?/, "") === "pin" ? "pin" : "host";

  onMount(async () => {
    tray.start();
    // The tray shows the window once the page has rendered, so it never flashes empty. (Not requestAnimationFrame:
    // the window is still hidden, and a hidden WebView2 runs no animation frames.)
    await tick();
    tray.send({ type: "ready" });
  });
</script>

{#if route === "pin"}
  <PinWindow />
{:else}
  <HostWindow />
{/if}
