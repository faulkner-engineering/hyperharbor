<script lang="ts">
  import { getCurrentWindow } from "@tauri-apps/api/window";
  import BrandMark from "$lib/brand/BrandMark.svelte";

  // The window has no Windows title bar (decorations off in tauri.conf.json); this bar replaces it. Empty space and
  // the brand drag the window and maximize it on double-click (data-tauri-drag-region); the buttons are the window's
  // own minimize, maximize or restore, and close, drawn with the Windows icon font so they look like the system's.
  const appWindow = getCurrentWindow();

  /** Segoe Fluent Icons (Windows 11) and Segoe MDL2 Assets (Windows 10) share these code points. */
  const glyphs = {
    minimize: String.fromCharCode(0xe921),
    maximize: String.fromCharCode(0xe922),
    restore: String.fromCharCode(0xe923),
    close: String.fromCharCode(0xe8bb),
  };

  let maximized = $state(false);

  $effect(() => {
    let stop: (() => void) | undefined;
    let ended = false;
    const read = async () => {
      maximized = await appWindow.isMaximized();
    };
    read();
    appWindow.onResized(read).then((unlisten) => {
      if (ended) unlisten();
      else stop = unlisten;
    });
    return () => {
      ended = true;
      stop?.();
    };
  });
</script>

<div class="titlebar" data-tauri-drag-region>
  <div class="brand" data-tauri-drag-region>
    <BrandMark size="1.2em" decorative />
    <span data-tauri-drag-region>HyperHarbor</span>
  </div>
  <div class="controls">
    <button type="button" aria-label="Minimize" title="Minimize" onclick={() => appWindow.minimize()}>{glyphs.minimize}</button>
    <button
      type="button"
      aria-label={maximized ? "Restore" : "Maximize"}
      title={maximized ? "Restore" : "Maximize"}
      onclick={() => appWindow.toggleMaximize()}>{maximized ? glyphs.restore : glyphs.maximize}</button
    >
    <button type="button" class="close" aria-label="Close" title="Close" onclick={() => appWindow.close()}>{glyphs.close}</button>
  </div>
</div>

<style>
  .titlebar {
    grid-column: 1 / -1;
    display: grid;
    grid-template-columns: 260px 1fr auto;
    height: var(--titlebar-height);
    position: relative;
    z-index: 60;
    background: var(--bg);
    border-bottom: 1px solid var(--border);
    user-select: none;
  }

  .brand {
    display: flex;
    align-items: center;
    gap: 0.45rem;
    padding: 0 1rem;
    font-weight: 600;
    background: var(--surface);
    border-right: 1px solid var(--border);
  }

  .controls {
    grid-column: 3;
    display: flex;
  }

  .controls button {
    width: 46px;
    height: 100%;
    padding: 0;
    border: none;
    border-radius: 0;
    background: transparent;
    color: var(--text);
    font-family: "Segoe Fluent Icons", "Segoe MDL2 Assets", sans-serif;
    font-size: 10px;
  }

  .controls button:hover {
    background: var(--hover);
  }

  .controls button.close:hover {
    background: #c42b1c;
    color: #ffffff;
  }

  .controls button:focus-visible {
    outline: 2px solid var(--accent);
    outline-offset: -2px;
  }
</style>
