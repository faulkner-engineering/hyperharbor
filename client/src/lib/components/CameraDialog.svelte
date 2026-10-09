<script lang="ts">
  import { onMount } from "svelte";
  import Dialog from "./Dialog.svelte";
  import {
    errorMessage,
    getCameraPrefs,
    getCameraShutter,
    getCameraStatus,
    isCameraSourceOutdated,
    setCameraPrefs,
    setCameraShutter,
    removeCameraSharing,
    setupCameraSharing,
    type CameraMode,
    type CameraPrefs,
    type CameraStatus,
    type HostEntry,
    type UnfocusedBehavior,
    type Vm,
  } from "$lib/api/client";

  interface Props {
    host: HostEntry;
    vm: Vm;
    onclose: () => void;
  }

  let { host, vm, onclose }: Props = $props();

  /** How often the picture's state is refreshed while the dialog is open. */
  const REFRESH_MS = 1500;

  let prefs = $state<CameraPrefs | null>(null);
  let status = $state<CameraStatus | null>(null);
  let shutterClosed = $state(false);
  let saving = $state(false);
  let settingUp = $state(false);
  let confirmingRemove = $state(false);
  let removing = $state(false);
  let error = $state<string | null>(null);
  /** The installed camera component is older than the one this version ships with. */
  let outdated = $state(false);

  async function checkOutdated() {
    try {
      outdated = await isCameraSourceOutdated();
    } catch {
      outdated = false;
    }
  }

  const options: { value: UnfocusedBehavior; label: string; detail: string }[] = [
    { value: "freeze", label: "Freeze the picture", detail: "The VM keeps the last frame it had. Uses the least power." },
    { value: "blur", label: "Blur the picture", detail: "The VM sees a blurred version of live video." },
    { value: "keepLive", label: "Keep it live", detail: "The VM keeps getting live video even when you are working in another window." },
  ];

  const modeText: Record<CameraMode, string> = {
    live: "live video",
    frozen: "a frozen picture",
    blurred: "a blurred picture",
    shuttered: "black (the shutter is closed)",
  };

  /** This VM's open session, if it has one. The key is "<host id>/<VM id>". */
  let session = $derived(status?.sessions.find((s) => s.vmKey.endsWith(`/${vm.id}`)) ?? null);
  /** Windows 11, but the camera source is not registered yet. */
  let needsSetup = $derived(status !== null && status.sharingAvailable && !status.sourceInstalled);
  let sharingUnavailable = $derived(status !== null && (!status.sharingAvailable || !status.sourceInstalled));
  let cameraProblem = $derived(
    status !== null && (status.camera === "busy" || status.camera === "unavailable") ? status.cameraMessage : null,
  );

  async function refresh() {
    try {
      status = await getCameraStatus();
    } catch {
      // The next refresh tries again; the dialog still works without a live status.
    }
  }

  onMount(() => {
    (async () => {
      try {
        const [loaded, closed] = await Promise.all([getCameraPrefs(host.key, vm.id), getCameraShutter(host.key, vm.id)]);
        prefs = loaded;
        shutterClosed = closed;
      } catch (e) {
        error = errorMessage(e);
      }
      await refresh();
      await checkOutdated();
    })();
    const timer = setInterval(refresh, REFRESH_MS);
    return () => clearInterval(timer);
  });

  async function setUp() {
    settingUp = true;
    error = null;
    try {
      await setupCameraSharing();
      await refresh();
      await checkOutdated();
    } catch (e) {
      error = errorMessage(e);
    } finally {
      settingUp = false;
    }
  }

  async function remove() {
    removing = true;
    error = null;
    try {
      await removeCameraSharing();
      confirmingRemove = false;
      await refresh();
    } catch (e) {
      error = errorMessage(e);
    } finally {
      removing = false;
    }
  }

  async function toggleShutter() {
    error = null;
    const closed = !shutterClosed;
    try {
      await setCameraShutter(host.key, vm.id, closed);
      shutterClosed = closed;
      await refresh();
    } catch (e) {
      error = errorMessage(e);
    }
  }

  async function save(event: SubmitEvent) {
    event.preventDefault();
    if (prefs === null) return;
    saving = true;
    error = null;
    try {
      await setCameraPrefs(host.key, vm.id, prefs);
      onclose();
    } catch (e) {
      error = errorMessage(e);
    } finally {
      saving = false;
    }
  }
</script>

<Dialog title="Camera for {vm.name}">
  <form onsubmit={save}>
    <p class="muted">
      Your camera is shared with each VM you connect to, so several VMs can show video at the same time.
      These settings are saved on this device and apply the next time you connect, except the shutter, which
      works at once.
    </p>

    {#if status?.sharingMessage}
      <p class="warning">{status.sharingMessage}</p>
    {:else if needsSetup}
      <div class="setup">
        <p class="warning">
          Camera sharing is not set up on this device yet. Until it is, only one VM at a time can use your camera.
          Setup installs a small Windows component and asks for administrator permission once.
        </p>
        <button type="button" class="primary" onclick={setUp} disabled={settingUp}>
          {settingUp ? "Setting up…" : "Set up camera sharing"}
        </button>
      </div>
    {:else if outdated}
      <div class="setup">
        <p class="warning">
          HyperHarbor was updated and comes with a newer camera component than the one installed on this device.
          Updating it asks for administrator permission and briefly stops the Windows camera service, so open
          sessions lose their camera until they reconnect.
        </p>
        <button type="button" class="primary" onclick={setUp} disabled={settingUp}>
          {settingUp ? "Updating…" : "Update camera component"}
        </button>
      </div>
    {/if}
    {#if cameraProblem}
      <p class="warning" role="alert">{cameraProblem}</p>
    {/if}

    <p class="muted local" aria-live="polite">
      {#if status?.localCamera}
        Apps on this PC can use "{status.localCamera}" now. It always shows live video.
      {:else}
        While a VM is connected, apps on this PC can also use a camera named "HyperHarbor Camera (This PC)". It always
        shows live video and goes away when the last VM disconnects. Windows has no default camera, so choose it in the
        app.
      {/if}
    </p>

    {#if prefs}
      <label class="check">
        <input type="checkbox" bind:checked={prefs.share} />
        Share my camera with this VM
      </label>

      <fieldset disabled={!prefs.share || sharingUnavailable}>
        <legend>While this VM's window is not in front</legend>
        {#each options as option (option.value)}
          <label class="check">
            <input type="radio" name="unfocused" value={option.value} bind:group={prefs.unfocused} />
            <span>
              {option.label}
              <span class="muted detail">{option.detail}</span>
            </span>
          </label>
        {/each}
      </fieldset>

      <div class="shutter">
        <div>
          <strong>Privacy shutter</strong>
          <p class="muted">While it is closed, this VM sees black, even when its window is in front.</p>
        </div>
        <button type="button" aria-pressed={shutterClosed} onclick={toggleShutter} disabled={sharingUnavailable}>
          {shutterClosed ? "Open shutter" : "Close shutter"}
        </button>
      </div>

      {#if session?.shared && session.mode}
        <p class="muted" aria-live="polite">Right now this VM sees {modeText[session.mode]}.</p>
      {:else if session}
        <p class="muted">This VM has the camera directly: only one VM can use it at a time.</p>
      {/if}
    {:else if error === null}
      <p class="muted">Reading settings…</p>
    {/if}

    {#if status?.sourceInstalled}
      <div class="remove">
        {#if confirmingRemove}
          <p class="muted">
            This removes the camera component from this device and asks for administrator permission. VMs with an
            open session lose their camera until you set sharing up again.
          </p>
          <button type="button" class="danger" onclick={remove} disabled={removing}>
            {removing ? "Removing…" : "Remove"}
          </button>
          <button type="button" onclick={() => (confirmingRemove = false)} disabled={removing}>Keep it</button>
        {:else}
          <button type="button" class="link" onclick={() => (confirmingRemove = true)}>Remove camera sharing from this device…</button>
        {/if}
      </div>
    {/if}

    {#if error}<p class="error">{error}</p>{/if}

    <div class="actions">
      <button type="button" onclick={onclose}>Cancel</button>
      <button type="submit" class="primary" disabled={saving || prefs === null}>Save</button>
    </div>
  </form>
</Dialog>

<style>
  fieldset {
    margin: 0.75rem 0 0;
    padding: 0.5rem 0.75rem;
    border: 1px solid var(--border);
  }

  fieldset:disabled {
    opacity: 0.55;
  }

  legend {
    padding: 0 0.25rem;
    font-size: 0.85rem;
    color: var(--muted);
  }

  .detail {
    display: block;
    font-size: 0.8rem;
  }

  .local {
    font-size: 0.85rem;
  }

  .setup {
    display: flex;
    flex-direction: column;
    align-items: flex-start;
    gap: 0.5rem;
  }

  .setup p {
    margin: 0;
  }

  .remove {
    display: flex;
    flex-wrap: wrap;
    align-items: center;
    gap: 0.5rem;
    margin-top: 0.75rem;
  }

  .remove p {
    flex-basis: 100%;
    margin: 0;
  }

  .remove .link {
    padding: 0;
    border: 0;
    background: none;
    color: var(--muted);
    font-size: 0.8rem;
    text-decoration: underline;
  }

  .shutter {
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 1rem;
    margin-top: 0.75rem;
    padding-top: 0.75rem;
    border-top: 1px solid var(--border);
  }

  .shutter p {
    margin: 0.1rem 0 0;
  }

  .shutter button[aria-pressed="true"] {
    border-color: var(--accent);
    background: color-mix(in srgb, var(--accent) 25%, var(--surface));
  }
</style>
