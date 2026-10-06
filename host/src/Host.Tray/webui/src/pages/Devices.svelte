<script lang="ts">
  import { flip } from "svelte/animate";
  import { fade } from "svelte/transition";
  import ConfirmDialog from "../components/ConfirmDialog.svelte";
  import Icon from "../components/Icon.svelte";
  import type { TrayViewDevice, TrayViewState } from "../bridge";
  import { tray } from "../store.svelte";
  import { deviceCount, shortFingerprint } from "../text";

  interface Props {
    view: TrayViewState;
  }

  let { view }: Props = $props();

  let removing = $state<TrayViewDevice | null>(null);

  function remove(device: TrayViewDevice) {
    removing = null;
    tray.send({ type: "removeDevice", deviceId: device.id }, `device:${device.id}`);
  }
</script>

<p class="muted intro">
  {deviceCount(view.devices.length)} To pair another device, open the HyperHarbor client on it and choose this PC; a PIN
  appears here.
</p>

{#if view.devices.length > 0}
  <ul class="list">
    {#each view.devices as device (device.id)}
      {@const busy = tray.isBusy(`device:${device.id}`)}
      <li animate:flip={{ duration: 200 }} out:fade={{ duration: 150 }} class:busy>
        <div class="icon"><Icon name="devices" /></div>
        <div>
          <strong>{device.name}</strong>
          <p class="muted">
            Paired {new Date(device.pairedAt).toLocaleString()} · <span class="mono">{shortFingerprint(device.fingerprint)}</span>
          </p>
        </div>
        <button type="button" class="btn danger" disabled={busy || !view.connected} onclick={() => (removing = device)}>
          {#if busy}<span class="spinner"></span> Removing…{:else}Remove…{/if}
        </button>
      </li>
    {/each}
  </ul>
{/if}

{#if removing}
  <ConfirmDialog
    title="Remove {removing.name}?"
    message="It can no longer reach this PC until it is paired again."
    confirmLabel="Remove"
    danger
    onconfirm={() => removing && remove(removing)}
    oncancel={() => (removing = null)}
  />
{/if}

<style>
  .intro {
    margin-bottom: 1rem;
  }

  .list {
    display: grid;
    gap: 0.6rem;
    margin: 0;
    padding: 0;
    list-style: none;
  }

  li {
    display: grid;
    grid-template-columns: auto 1fr auto;
    gap: 0.9rem;
    align-items: center;
    padding: 0.8rem 1rem;
    border: 1px solid var(--border);
    border-radius: var(--radius);
    background: var(--surface);
    box-shadow: var(--shadow);
    transition: opacity 200ms var(--ease);
  }

  li.busy {
    opacity: 0.6;
  }

  .icon {
    display: grid;
    place-items: center;
    width: 36px;
    height: 36px;
    border-radius: 9px;
    background: var(--hover);
  }

  p {
    margin-top: 0.1rem;
    font-size: 0.88rem;
  }
</style>
