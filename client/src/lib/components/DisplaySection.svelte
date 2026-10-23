<script lang="ts">
  import type { MachineDraft } from "$lib/machineDraft.svelte";
  import Choice from "./Choice.svelte";

  interface Props {
    draft: MachineDraft;
  }

  let { draft }: Props = $props();

  const guest = $derived(draft.display.guest);
  const connection = $derived(draft.display.connection);
  const wallpaperKind = $derived(guest.wallpaper?.image != null ? "image" : guest.wallpaper?.color != null ? "color" : null);
  const REDIRECTS = [
    { key: "clipboard", label: "Clipboard" },
    { key: "printers", label: "Printers" },
    { key: "microphone", label: "Microphone" },
    { key: "camera", label: "Camera" },
  ] as const;

  function setWallpaper(kind: string | null) {
    guest.wallpaper = kind === "color" ? { color: guest.wallpaper?.color ?? "#0B0D10" } : kind === "image" ? { image: guest.wallpaper?.image ?? "" } : null;
  }
</script>

<h4>Inside the VM</h4>
<p class="muted">Remote Desktop sets the resolution and scaling from the window you connect from.</p>
<Choice
  label="Theme"
  options={[
    { value: null, label: "As installed" },
    { value: "dark", label: "Dark" },
    { value: "light", label: "Light" },
  ]}
  bind:value={guest.theme}
/>
<div class="color-row">
  <label class="check">
    <input type="checkbox" checked={guest.accentColor != null} onchange={(event) => (guest.accentColor = event.currentTarget.checked ? "#E39B2E" : null)} />
    Accent color
  </label>
  {#if guest.accentColor != null}
    <input type="color" aria-label="Accent color" value={guest.accentColor} oninput={(event) => (guest.accentColor = event.currentTarget.value.toUpperCase())} />
  {/if}
</div>
<Choice
  label="Wallpaper"
  options={[
    { value: null, label: "As installed" },
    { value: "color", label: "Solid color" },
    { value: "image", label: "Image" },
  ]}
  value={wallpaperKind}
  onchange={setWallpaper}
/>
{#if guest.wallpaper?.color != null}
  <input type="color" aria-label="Wallpaper color" value={guest.wallpaper.color} oninput={(event) => (guest.wallpaper = { color: event.currentTarget.value.toUpperCase() })} />
{:else if guest.wallpaper?.image != null}
  <label>
    Image file name
    <input value={guest.wallpaper.image} oninput={(event) => (guest.wallpaper = { image: event.currentTarget.value })} placeholder="harbor.jpg" />
  </label>
  <small class="muted">A .jpg, .png, or .bmp the host keeps beside the profile. Adding the file arrives with creating VMs from profiles.</small>
{/if}

<h4>Connecting</h4>
<p class="muted">Defaults for this profile's VMs. A device's own choices still win.</p>
<Choice
  label="Monitors"
  options={[
    { value: null, label: "Device default" },
    { value: "one", label: "One" },
    { value: "all", label: "All" },
  ]}
  bind:value={connection.monitors}
/>
<Choice
  label="Window"
  options={[
    { value: null, label: "Device default" },
    { value: "fullScreen", label: "Full screen" },
    { value: "windowed", label: "Windowed" },
  ]}
  bind:value={connection.window}
/>
{#each REDIRECTS as redirect (redirect.key)}
  <Choice
    label={redirect.label}
    options={[
      { value: null, label: "Default" },
      { value: true, label: "Share" },
      { value: false, label: "Don't share" },
    ]}
    bind:value={connection.redirect[redirect.key]}
  />
{/each}

<style>
  h4 {
    margin: 0.4rem 0 0.2rem;
    font-size: 0.9rem;
  }

  .color-row {
    display: flex;
    align-items: center;
    gap: 0.6rem;
    margin-bottom: 0.7rem;
  }

  input[type="color"] {
    width: 3rem;
    height: 1.8rem;
    padding: 0.1rem !important;
  }

  label:not(.check) {
    display: flex;
    flex-direction: column;
    gap: 0.2rem;
  }
</style>
