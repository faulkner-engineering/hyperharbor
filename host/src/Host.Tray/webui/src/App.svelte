<script lang="ts">
  import { onMount, tick } from "svelte";
  import Ambient from "./Ambient.svelte";
  import HostWindow from "./HostWindow.svelte";
  import PinWindow from "./PinWindow.svelte";
  import { tray } from "./store.svelte";

  // The tray opens index.html#/host or #/pin; each window shows one of them. The ambient screensaver opens #/ambient.
  const name = location.hash.replace(/^#\/?/, "");
  const route = name === "pin" ? "pin" : name === "ambient" ? "ambient" : "host";

  onMount(async () => {
    // The ambient screen runs its own page logic and sends "ready" itself.
    if (route === "ambient") return;
    tray.start();
    // The tray shows the window once the page has rendered, so it never flashes empty. (Not requestAnimationFrame:
    // the window is still hidden, and a hidden WebView2 runs no animation frames.)
    await tick();
    tray.send({ type: "ready" });
  });
</script>

{#if route === "ambient"}
  <Ambient />
{:else if route === "pin"}
  <PinWindow />
{:else}
  <HostWindow />
{/if}
