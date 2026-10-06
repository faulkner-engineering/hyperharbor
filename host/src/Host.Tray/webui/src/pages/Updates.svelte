<script lang="ts">
  import { fade, slide } from "svelte/transition";
  import Icon from "../components/Icon.svelte";
  import ProgressBar from "../components/ProgressBar.svelte";
  import UpdateSteps from "../components/UpdateSteps.svelte";
  import type { TrayViewState } from "../bridge";
  import { tray } from "../store.svelte";
  import { progressText, updateModeText, updateSteps, updateSummary } from "../text";

  interface Props {
    view: TrayViewState;
  }

  let { view }: Props = $props();

  const update = $derived(view.update);
  const checking = $derived(tray.isBusy("update"));
  const moving = $derived(checking || (update !== null && ["checking", "preparing", "installing"].includes(update.activity)));
  const failed = $derived(update?.activity === "idle" && (update.message?.includes("failed") ?? false));
</script>

{#if !update}
  <section class="panel">
    <p class="muted">{view.connected ? "Reading the update status…" : "Unknown while the service is not running."}</p>
  </section>
{:else}
  <section class="panel hero">
    <div class="head">
      <div>
        <p class="muted small">Installed version</p>
        <h2>{update.currentVersion}</h2>
      </div>
      {#if update.supported}
        <div class="buttons">
          {#if update.activity === "ready"}
            <button type="button" class="btn primary" onclick={() => tray.send({ type: "installUpdate" })}>
              Install {update.availableVersion} now
            </button>
          {/if}
          <button type="button" class="btn" disabled={moving} onclick={() => tray.send({ type: "checkUpdate" }, "update")}>
            {#if checking}<span class="spinner"></span> Checking…{:else}Check now{/if}
          </button>
        </div>
      {/if}
    </div>

    <p class="summary" class:failed>
      {#key updateSummary(update, checking)}
        <span in:fade={{ duration: 200 }}>{updateSummary(update, checking)}</span>
      {/key}
    </p>

    {#if update.supported && (moving || update.activity === "ready")}
      <div transition:slide={{ duration: 220 }}><UpdateSteps steps={updateSteps(update, checking)} /></div>
    {/if}

    {#if update.activity === "preparing" && update.progress}
      {@const progress = progressText(update.progress)}
      <div class="progress" transition:slide={{ duration: 220 }}>
        <ProgressBar fraction={progress.fraction} label="Preparing version {update.availableVersion}" />
        <p class="muted small">{progress.text}</p>
      </div>
    {/if}

    {#if update.notesUrl && update.availableVersion}
      <button type="button" class="btn link notes" onclick={() => update.notesUrl && tray.send({ type: "openUrl", url: update.notesUrl })}>
        What's new in {update.availableVersion} <Icon name="external" size={14} />
      </button>
    {/if}
  </section>

  {#if update.supported}
    <section class="panel settings">
      <label>
        <span>Release channel</span>
        <select value={update.channel} onchange={(event) => tray.send({ type: "setChannel", channel: event.currentTarget.value })} disabled={moving}>
          {#each update.channels as channel (channel)}
            <option value={channel}>{channel}</option>
          {/each}
        </select>
      </label>
      <p class="muted">{updateModeText(update)} Change the mode and maintenance time from a client's Updates panel.</p>
      {#if update.lastCheck}<p class="muted">Last checked {new Date(update.lastCheck).toLocaleString()}.</p>{/if}
      {#if update.lastResult}<p class="muted">Last update: {update.lastResult}</p>{/if}
      {#if update.rolledBack.length > 0}
        <p class="warn">Skipped because they did not start on this host: {update.rolledBack.join(", ")}.</p>
      {/if}
    </section>
  {/if}
{/if}

<style>
  .panel {
    padding: 1.2rem 1.3rem;
    border: 1px solid var(--border);
    border-radius: var(--radius);
    background: var(--surface);
    box-shadow: var(--shadow);
  }

  .panel + .panel {
    margin-top: 0.8rem;
  }

  .hero {
    display: grid;
    gap: 1.1rem;
  }

  .head {
    display: flex;
    justify-content: space-between;
    align-items: flex-start;
    gap: 1rem;
  }

  h2 {
    font-size: 1.6rem;
    font-weight: 600;
    letter-spacing: -0.01em;
  }

  .small {
    font-size: 0.82rem;
  }

  .buttons {
    display: flex;
    gap: 0.5rem;
  }

  .summary {
    min-height: 2.9em;
  }

  .summary.failed {
    color: var(--danger);
  }

  .progress {
    display: grid;
    gap: 0.4rem;
  }

  .notes {
    justify-self: start;
  }

  .settings {
    display: grid;
    gap: 0.5rem;
  }

  label {
    display: grid;
    grid-template-columns: auto 200px;
    gap: 1rem;
    align-items: center;
    justify-content: start;
    font-weight: 600;
  }

  .warn {
    color: var(--warn-fg);
  }
</style>
