<script lang="ts">
  import { onDestroy, onMount } from "svelte";
  import {
    errorMessage,
    getRestoreTest,
    setRestoreTestSettings,
    startRestoreTest,
    type HostEntry,
    type RestoreTestSettings,
    type RestoreTestStatus,
  } from "$lib/api/client";
  import { intervalText, restoreTestLine, restoreTestSummary } from "$lib/backup";

  interface Props {
    host: HostEntry;
  }

  let { host }: Props = $props();

  const POLL_MS = 5000;
  const RESULTS_SHOWN = 8;

  let status = $state<RestoreTestStatus | null>(null);
  let error = $state<string | null>(null);
  let busy = $state(false);
  let intervalDays = $state(30);
  let windowStart = $state("03:00");
  let windowEnd = $state("06:00");
  let timer: ReturnType<typeof setInterval> | undefined;

  const changed = $derived(
    status !== null &&
      (status.settings.intervalDays !== intervalDays ||
        status.settings.window.start !== windowStart ||
        status.settings.window.end !== windowEnd),
  );

  async function load() {
    try {
      const loaded = await getRestoreTest(host.key);
      // Keep what the user is typing; take the host's values only when nothing was changed here.
      if (status === null || !changed) {
        intervalDays = loaded.settings.intervalDays;
        windowStart = loaded.settings.window.start;
        windowEnd = loaded.settings.window.end;
      }
      status = loaded;
      error = null;
    } catch (e) {
      error = errorMessage(e);
    }
  }

  onMount(() => {
    void load();
    timer = setInterval(() => void load(), POLL_MS);
  });
  onDestroy(() => clearInterval(timer));

  async function save(settings: RestoreTestSettings) {
    busy = true;
    try {
      status = await setRestoreTestSettings(host.key, settings);
      intervalDays = status.settings.intervalDays;
      windowStart = status.settings.window.start;
      windowEnd = status.settings.window.end;
      error = null;
    } catch (e) {
      error = errorMessage(e);
    } finally {
      busy = false;
    }
  }

  const toggle = () => status && save({ ...status.settings, enabled: !status.settings.enabled });

  const saveChanges = () =>
    status && save({ enabled: status.settings.enabled, intervalDays, window: { start: windowStart, end: windowEnd } });

  async function runNow() {
    busy = true;
    try {
      status = await startRestoreTest(host.key);
      error = null;
    } catch (e) {
      error = errorMessage(e);
    } finally {
      busy = false;
    }
  }
</script>

<section class="restore-test" aria-label="Restore test">
  <h4>Restore test</h4>
  {#if status}
    <p class="muted">
      {status.settings.enabled ? `Runs ${intervalText(status.settings.intervalDays)}` : "Off. When on, it runs"} between
      {status.settings.window.start} and {status.settings.window.end}, when no game or backup is running. Each VM's latest backup is
      restored as a throwaway VM with no network, started, and checked, then deleted.
    </p>
    <p>{restoreTestSummary(status)}</p>
    <div class="buttons">
      <button type="button" onclick={toggle} disabled={busy}>{status.settings.enabled ? "Turn off" : "Turn on"}</button>
      <button type="button" onclick={runNow} disabled={busy || status.running || status.testable === 0}>Run test now</button>
    </div>
    <div class="settings">
      <label>
        Every
        <input type="number" min="1" max="365" step="1" bind:value={intervalDays} aria-label="Days between restore tests" />
        days
      </label>
      <label>
        between
        <input type="time" bind:value={windowStart} aria-label="Restore test window start" />
        and
        <input type="time" bind:value={windowEnd} aria-label="Restore test window end" />
      </label>
      <button type="button" onclick={saveChanges} disabled={busy || !changed}>Save</button>
    </div>
    {#if status.results.length > 0}
      <ul class="results">
        {#each status.results.slice(0, RESULTS_SHOWN) as result (result.runId)}
          <li class={result.outcome}>{restoreTestLine(result)}</li>
        {/each}
      </ul>
    {/if}
  {/if}
  {#if error}<p class="error" role="alert">{error}</p>{/if}
</section>

<style>
  .restore-test {
    margin: 0.5rem 0;
  }

  h4 {
    margin: 0.5rem 0;
  }

  .buttons,
  .settings {
    display: flex;
    flex-wrap: wrap;
    align-items: center;
    gap: 0.5rem;
    margin: 0.5rem 0;
  }

  input[type="number"] {
    width: 4.5rem;
  }

  .results {
    margin: 0 0 0.5rem 1.2rem;
    padding: 0;
    font-size: 0.9rem;
  }

  .results .failed {
    color: var(--danger, #c0392b);
  }
</style>
