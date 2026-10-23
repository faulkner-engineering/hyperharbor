<script lang="ts">
  import { listBackupTargets, type BackupTarget, type HostEntry } from "$lib/api/client";
  import type { MachineDraft } from "$lib/machineDraft.svelte";
  import { onMount } from "svelte";
  import Choice from "./Choice.svelte";

  interface Props {
    host: HostEntry;
    draft: MachineDraft;
  }

  let { host, draft }: Props = $props();

  const DEFAULT_TRIGGERS = { schedule: true, onShutdown: false, debounceMinutes: 10 };

  let targets = $state<BackupTarget[]>([]);
  const mode = $derived(draft.backup === null ? null : draft.backup.enabled);
  const target = $derived(targets.find((item) => item.name === draft.backup?.target) ?? null);

  onMount(async () => {
    try {
      targets = await listBackupTargets(host.key);
    } catch {
      // The names are suggestions; any name can be typed.
    }
  });

  function setMode(enabled: boolean | null) {
    draft.backup =
      enabled === null
        ? null
        : { ...(draft.backup ?? {}), enabled, target: draft.backup?.target ?? targets[0]?.name ?? null, triggers: draft.backup?.triggers ?? { ...DEFAULT_TRIGGERS } };
  }
</script>

<Choice
  label="Backups"
  options={[
    { value: null, label: "As it is" },
    { value: true, label: "On" },
    { value: false, label: "Off" },
  ]}
  value={mode}
  onchange={setMode}
  hint="The schedule, retention, and storage belong to the backup target; the profile chooses the target and when to back up."
/>

{#if draft.backup?.enabled}
  {@const backup = draft.backup}
  <label>
    Backup target
    <input list="profile-targets" value={backup.target ?? ""} oninput={(event) => (backup.target = event.currentTarget.value || null)} placeholder="The target's name on the host" />
    <datalist id="profile-targets">
      {#each targets as item (item.id)}<option value={item.name}></option>{/each}
    </datalist>
  </label>
  {#if target}
    <small class="muted">{target.schedule ? `Scheduled at ${target.schedule.time}.` : "This target has no schedule."}</small>
  {:else if backup.target}
    <small class="muted">This host has no target named {backup.target}; a host that does will use it.</small>
  {/if}
  {@const triggers = backup.triggers ?? DEFAULT_TRIGGERS}
  <label class="check">
    <input type="checkbox" checked={triggers.schedule !== false} onchange={(event) => (backup.triggers = { ...triggers, schedule: event.currentTarget.checked })} />
    On the target's schedule
  </label>
  <label class="check">
    <input type="checkbox" checked={triggers.onShutdown === true} onchange={(event) => (backup.triggers = { ...triggers, onShutdown: event.currentTarget.checked })} />
    After the VM shuts down
  </label>
  <Choice
    label="With Performance mode on"
    options={[
      { value: null, label: "Target default" },
      { value: "requireOff", label: "Wait until the VM is off" },
      { value: "allowLive", label: "Back up while running" },
    ]}
    bind:value={backup.gpuMode}
  />
{/if}

<style>
  label:not(.check) {
    display: flex;
    flex-direction: column;
    gap: 0.2rem;
    margin-top: 0.4rem;
  }

  small {
    display: block;
    margin: 0.2rem 0 0.6rem;
  }
</style>
