<script lang="ts">
  import {
    deleteProfileImage,
    errorMessage,
    hostSupports,
    listProfileImages,
    ProfileImagesApiVersion,
    uploadProfileImage,
    type HostEntry,
    type ProfileImage,
  } from "$lib/api/client";
  import { ElevationCancelled, withElevation } from "$lib/lifecycle.svelte";
  import type { MachineDraft } from "$lib/machineDraft.svelte";
  import { toasts } from "$lib/toasts.svelte";
  import { onMount } from "svelte";
  import Choice from "./Choice.svelte";

  interface Props {
    host: HostEntry;
    draft: MachineDraft;
  }

  let { host, draft }: Props = $props();

  const keepsImages = $derived(hostSupports(host, ProfileImagesApiVersion));
  let images = $state<ProfileImage[]>([]);
  let busy = $state(false);

  onMount(async () => {
    if (!keepsImages) return;
    try {
      images = await listProfileImages(host.key);
    } catch {
      // Without the list, a name can still be kept; the host reports a missing image when the profile is applied.
    }
  });

  async function addImage() {
    busy = true;
    try {
      const added = await withElevation(host.key, () => uploadProfileImage(host.key));
      if (added) {
        images = [...images.filter((image) => image.name !== added.name), added].sort((a, b) => a.name.localeCompare(b.name));
        guest.wallpaper = { image: added.name };
      }
    } catch (e) {
      if (!(e instanceof ElevationCancelled)) toasts.error(errorMessage(e));
    } finally {
      busy = false;
    }
  }

  async function removeImage(name: string) {
    busy = true;
    try {
      await withElevation(host.key, () => deleteProfileImage(host.key, name));
      images = images.filter((image) => image.name !== name);
    } catch (e) {
      if (!(e instanceof ElevationCancelled)) toasts.error(errorMessage(e));
    } finally {
      busy = false;
    }
  }

  function size(bytes: number) {
    return bytes >= 1024 * 1024 ? `${(bytes / 1024 / 1024).toFixed(1)} MB` : `${Math.max(1, Math.round(bytes / 1024))} KB`;
  }

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
{:else if guest.wallpaper?.image != null && !keepsImages}
  <label>
    Image file name
    <input value={guest.wallpaper.image} oninput={(event) => (guest.wallpaper = { image: event.currentTarget.value })} placeholder="harbor.jpg" />
  </label>
  <small class="muted">Update this host to add images to it; until then, the name of an image added from another device can be kept here.</small>
{:else if guest.wallpaper?.image != null}
  {@const chosen = guest.wallpaper.image}
  <div class="images" role="radiogroup" aria-label="Wallpaper image">
    {#each images as image (image.name)}
      <div class="image" class:selected={image.name === chosen}>
        <button type="button" role="radio" aria-checked={image.name === chosen} onclick={() => (guest.wallpaper = { image: image.name })}>
          <span class="name">{image.name}</span>
          <span class="muted">{size(image.sizeBytes)}</span>
        </button>
        <button type="button" class="remove" aria-label={`Delete ${image.name}`} title="Delete from the host" disabled={busy} onclick={() => removeImage(image.name)}>×</button>
      </div>
    {/each}
    <button type="button" class="add" disabled={busy} onclick={addImage}>Add image…</button>
  </div>
  {#if chosen && !images.some((image) => image.name === chosen)}
    <small class="warning">This host has no image named {chosen}. Add it, or choose another.</small>
  {:else}
    <small class="muted">JPEG, PNG, or BMP up to 20 MB, kept on the host for all your profiles. It is copied into the VM and fills the screen.</small>
  {/if}
{/if}

<h4>Connecting</h4>
<p class="muted">Used when connecting to this profile's VMs. A monitor choice made on a device for a VM still wins there.</p>
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

  .images {
    display: flex;
    flex-direction: column;
    gap: 0.25rem;
    margin-bottom: 0.3rem;
  }

  .image {
    display: flex;
    align-items: stretch;
    border: 1px solid var(--border);
  }

  .image.selected {
    border-color: var(--accent);
    background: color-mix(in srgb, var(--accent) 14%, transparent);
    color: var(--accent);
  }

  .image button[role="radio"] {
    flex: 1;
    display: flex;
    justify-content: space-between;
    gap: 0.6rem;
    text-align: left;
    border: none !important;
    background: none;
    color: inherit;
    font-size: 0.85rem;
  }

  .image .name {
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  .remove {
    border: none !important;
    background: none;
    color: var(--muted);
    padding: 0 0.6rem !important;
  }

  .add {
    align-self: flex-start;
  }

  .warning {
    color: var(--accent);
  }

  label:not(.check) {
    display: flex;
    flex-direction: column;
    gap: 0.2rem;
  }
</style>
