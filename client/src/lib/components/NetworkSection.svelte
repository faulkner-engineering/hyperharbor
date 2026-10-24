<script lang="ts">
  import { listSwitches, type HostEntry, type VirtualSwitch } from "$lib/api/client";
  import type { MachineDraft } from "$lib/machineDraft.svelte";
  import { onMount } from "svelte";
  import Choice from "./Choice.svelte";

  interface Props {
    host: HostEntry;
    draft: MachineDraft;
  }

  let { host, draft }: Props = $props();

  let switches = $state<VirtualSwitch[]>([]);

  onMount(async () => {
    try {
      switches = await listSwitches(host.key);
    } catch {
      // The names are suggestions; any name can be typed.
    }
  });
</script>

<label>
  Virtual switch
  <input
    list="profile-switches"
    value={draft.network.switch ?? ""}
    oninput={(event) => (draft.network.switch = event.currentTarget.value || null)}
    placeholder="The host's default switch"
  />
  <datalist id="profile-switches">
    {#each switches as item (item.id)}<option value={item.name}></option>{/each}
  </datalist>
</label>
<small class="muted">By name, so the profile works on another host with a switch of the same name; applying it says when a host has none.</small>

<Choice
  label="MAC address spoofing"
  options={[
    { value: null, label: "As it is" },
    { value: true, label: "On" },
    { value: false, label: "Off" },
  ]}
  bind:value={draft.network.macAddressSpoofing}
  hint="Needed when the VM runs its own VMs or containers on the network."
/>

<style>
  label {
    display: flex;
    flex-direction: column;
    gap: 0.2rem;
  }

  small {
    display: block;
    margin: 0.2rem 0 0.8rem;
  }
</style>
