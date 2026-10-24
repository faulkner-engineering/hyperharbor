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
  <label class="check override">
    <input
      type="checkbox"
      checked={backup.retention != null}
      onchange={(event) => (backup.retention = event.currentTarget.checked ? { daily: 7, weekly: 4, monthly: 6 } : null)}
    />
    Keep a different number of backups than the target
  </label>
  {#if backup.retention}
    {@const retention = backup.retention}
    <div class="grid">
      <label>Daily <input type="number" min="0" value={retention.daily} oninput={(event) => (backup.retention = { ...retention, daily: Number(event.currentTarget.value) })} /></label>
      <label>Weekly <input type="number" min="0" value={retention.weekly} oninput={(event) => (backup.retention = { ...retention, weekly: Number(event.currentTarget.value) })} /></label>
      <label>Monthly <input type="number" min="0" value={retention.monthly} oninput={(event) => (backup.retention = { ...retention, monthly: Number(event.currentTarget.value) })} /></label>
    </div>
  {/if}
  <label class="check override">
    <input
      type="checkbox"
      checked={backup.conditions != null}
      onchange={(event) => (backup.conditions = event.currentTarget.checked ? { noActiveSession: true, notInGame: true } : null)}
    />
    Use other conditions than the target for when a backup may start
  </label>
  {#if backup.conditions}
    {@const conditions = backup.conditions}
    <label class="check"><input type="checkbox" checked={conditions.noActiveSession !== false} onchange={(event) => (backup.conditions = { ...conditions, noActiveSession: event.currentTarget.checked })} /> Wait while someone is connected</label>
    <label class="check"><input type="checkbox" checked={conditions.notInGame !== false} onchange={(event) => (backup.conditions = { ...conditions, notInGame: event.currentTarget.checked })} /> Wait while a game runs on the host</label>
    <label class="check">
      <input
        type="checkbox"
        checked={conditions.window != null}
        onchange={(event) => (backup.conditions = { ...conditions, window: event.currentTarget.checked ? { start: "01:00", end: "06:00" } : undefined })}
      />
      Only between
    </label>
    {#if conditions.window}
      {@const window = conditions.window}
      <div class="grid">
        <label>From <input type="time" value={window.start} oninput={(event) => (backup.conditions = { ...conditions, window: { ...window, start: event.currentTarget.value } })} /></label>
        <label>To <input type="time" value={window.end} oninput={(event) => (backup.conditions = { ...conditions, window: { ...window, end: event.currentTarget.value } })} /></label>
      </div>
    {/if}
  {/if}
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

  .override {
    margin-top: 0.7rem !important;
  }

  .grid {
    display: grid;
    grid-template-columns: repeat(auto-fit, minmax(7rem, 1fr));
    gap: 0 0.6rem;
  }
</style>
