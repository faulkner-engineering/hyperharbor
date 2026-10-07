<script lang="ts">
  import { fade, slide } from "svelte/transition";
  import type { TrayViewState } from "../bridge";
  import { tray } from "../store.svelte";
  import { groupChanges, leanSummary, metricsText, runDelta } from "../text";

  interface Props {
    view: TrayViewState;
  }

  let { view }: Props = $props();

  const lean = $derived(view.hostLean);
  const working = $derived(tray.isBusy("hostLean") || (lean?.busy ?? null) !== null);
  const plan = $derived(lean?.dryRun ?? null);
  const groups = $derived(plan ? groupChanges(plan) : []);
  const run = $derived(lean?.lastRun ?? null);
  const delta = $derived(run ? runDelta(run) : null);
  const canApply = $derived(!working && plan !== null && plan.canApply && plan.changes.length > 0);

  function dryRun(source: "lean" | "undo") {
    tray.send({ type: "hostLeanDryRun", source }, "hostLean");
  }

  function apply() {
    if (plan) tray.send({ type: "hostLeanApply", source: plan.source }, "hostLean");
  }
</script>

{#if !lean}
  <section class="panel">
    <p class="muted">{view.connected ? "Reading the Lean host status…" : "Unknown while the service is not running."}</p>
  </section>
{:else}
  <section class="panel hero">
    <div class="head">
      <div>
        <p class="muted small">Profile</p>
        <h2>{lean.profileName}</h2>
      </div>
      {#if lean.supported}
        <div class="buttons">
          {#if lean.undoAvailable}
            <button type="button" class="btn" disabled={working} onclick={() => dryRun("undo")}>Undo last apply…</button>
          {/if}
          <button type="button" class="btn" disabled={working} onclick={() => dryRun("lean")}>
            {#if working && lean.busy === "dryRun"}<span class="spinner"></span> Reading…{:else}Dry run{/if}
          </button>
          <button type="button" class="btn primary" disabled={!canApply} onclick={apply}>
            {#if working && (lean.busy === "apply" || lean.busy === "undo")}<span class="spinner"></span> Working…{:else if plan?.source === "undo"}Apply undo{:else}Apply{/if}
          </button>
        </div>
      {/if}
    </div>

    <p class="summary">
      {#key leanSummary(lean)}
        <span in:fade={{ duration: 200 }}>{leanSummary(lean)}</span>
      {/key}
    </p>

    <div class="about muted">
      <p>
        This trims the PC for gaming: it removes consumer apps, disables services and startup entries nobody needs, turns off
        Widgets, Copilot, search highlights, Game Bar, and Game DVR, sets the High performance power plan so only the network
        adapter, keyboards, and mice can wake the PC, and uninstalls OneDrive and known RGB lighting suites.
      </p>
      <p>
        Nothing changes until you review a dry run. The first apply makes a restore point and a registry export. Settings,
        services, startup entries, and power settings can be undone; removed apps and uninstalled programs cannot.
      </p>
    </div>
  </section>

  {#if lean.supported}
    <section class="panel settings">
      <label class="toggle">
        <input
          type="checkbox"
          checked={lean.scheduleEnabled}
          disabled={!lean.applied}
          onchange={(event) => tray.send({ type: "setHostLeanSchedule", enabled: event.currentTarget.checked })}
        />
        <span>Re-apply every month</span>
      </label>
      <p class="muted">
        {#if !lean.applied}
          Available after the first apply.
        {:else if lean.scheduleEnabled}
          {lean.nextScheduled ? `Next re-apply: ${new Date(lean.nextScheduled).toLocaleDateString()}.` : "Scheduled."} Windows updates can bring
          removed apps and settings back; the re-apply fixes them.
        {:else}
          Off. Windows updates can bring removed apps and settings back.
        {/if}
      </p>
    </section>
  {/if}

  {#if plan}
    <section class="panel results" transition:slide={{ duration: 220 }}>
      <h3>{plan.source === "undo" ? "Undo dry run" : "Dry run"}: {plan.changes.length === 1 ? "1 change" : `${plan.changes.length} changes`}</h3>
      <p class="muted small">
        Read {new Date(plan.at).toLocaleString()}. {plan.alreadyInPlace} already in place.
      </p>

      {#each groups as group (group.handler)}
        <details>
          <summary>{group.label} <span class="count">{group.lines.length}</span></summary>
          <ul>
            {#each group.lines as line, index (index)}
              <li><strong>{line.item}</strong> <span class="muted">{line.text}</span></li>
            {/each}
          </ul>
        </details>
      {/each}

      {#if plan.kept.length > 0}
        <details>
          <summary>Kept on purpose <span class="count">{plan.kept.length}</span></summary>
          <ul>
            {#each plan.kept as note, index (index)}
              <li class="muted">{note}</li>
            {/each}
          </ul>
        </details>
      {/if}

      {#if plan.problems.length > 0}
        <div class="problems">
          <p class="warn">Could not plan:</p>
          <ul>
            {#each plan.problems as problem, index (index)}
              <li class="warn">{problem}</li>
            {/each}
          </ul>
        </div>
      {/if}
    </section>
  {/if}

  {#if run}
    <section class="panel results">
      <h3>Last {run.source === "undo" ? "undo" : "apply"}{run.scheduled ? " (monthly)" : ""}</h3>
      <p class="muted small">
        {new Date(run.at).toLocaleString()}. {run.changed === 1 ? "1 change" : `${run.changed} changes`} made{run.problems.length > 0
          ? `, ${run.problems.length === 1 ? "1 problem" : `${run.problems.length} problems`}`
          : ""}.
      </p>
      {#if run.restorePoint}<p class="muted small">Restore point and registry export: {run.restorePoint}</p>{/if}
      {#if run.before && run.after}
        <dl class="metrics">
          <dt>Before</dt>
          <dd>{metricsText(run.before)}</dd>
          <dt>After</dt>
          <dd>{metricsText(run.after)}</dd>
          {#if delta}
            <dt>Change</dt>
            <dd>{delta.memory} memory, {delta.processes} processes</dd>
          {/if}
        </dl>
      {/if}
      {#if run.problems.length > 0}
        <ul>
          {#each run.problems as problem, index (index)}
            <li class="warn">{problem}</li>
          {/each}
        </ul>
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
    gap: 1rem;
  }

  .head {
    display: flex;
    justify-content: space-between;
    align-items: flex-start;
    gap: 1rem;
  }

  h2 {
    font-size: 1.4rem;
    font-weight: 600;
    letter-spacing: -0.01em;
  }

  h3 {
    font-size: 1rem;
    font-weight: 600;
    margin-bottom: 0.3rem;
  }

  .small {
    font-size: 0.82rem;
  }

  .buttons {
    display: flex;
    gap: 0.5rem;
    flex-wrap: wrap;
    justify-content: flex-end;
  }

  .summary {
    min-height: 1.5em;
  }

  .about {
    display: grid;
    gap: 0.5rem;
    font-size: 0.9rem;
  }

  .settings {
    display: grid;
    gap: 0.4rem;
  }

  .toggle {
    display: flex;
    gap: 0.6rem;
    align-items: center;
    font-weight: 600;
  }

  .results {
    display: grid;
    gap: 0.4rem;
  }

  details {
    border-top: 1px solid var(--border);
    padding: 0.45rem 0;
  }

  summary {
    cursor: pointer;
    font-weight: 600;
  }

  .count {
    margin-left: 0.4rem;
    font-weight: 400;
    color: var(--muted, inherit);
  }

  ul {
    margin: 0.4rem 0 0.2rem 1.1rem;
    display: grid;
    gap: 0.2rem;
    font-size: 0.9rem;
  }

  .metrics {
    display: grid;
    grid-template-columns: auto 1fr;
    gap: 0.2rem 1rem;
    font-size: 0.9rem;
  }

  dt {
    font-weight: 600;
  }

  .warn {
    color: var(--warn-fg);
  }
</style>
