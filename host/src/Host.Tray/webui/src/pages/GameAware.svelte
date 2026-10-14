<script lang="ts">
  import Card from "../components/Card.svelte";
  import { tray } from "../store.svelte";
  import type { GameAwareSettings, GameVmAction, TrayViewState } from "../bridge";
  import { ACTION_LABELS, addPrograms, effectiveAction, gameAwareStatus, withVmAction } from "../text";

  interface Props {
    view: TrayViewState;
  }

  let { view }: Props = $props();

  let allowText = $state("");
  let ignoreText = $state("");
  let launcherText = $state("");

  const info = $derived(view.gameAware);
  const status = $derived(gameAwareStatus(info));
  const busy = $derived(tray.requested.includes("gameAwareApply"));

  function save(change: (current: GameAwareSettings) => GameAwareSettings) {
    if (!info) return;
    tray.send({ type: "gameAwareApply", settings: change(info.settings) }, "gameAwareApply");
  }

  function addAllow(event: SubmitEvent) {
    event.preventDefault();
    const text = allowText;
    allowText = "";
    save((s) => ({ ...s, allowlist: addPrograms(s.allowlist, text) }));
  }

  function addLauncher(event: SubmitEvent) {
    event.preventDefault();
    const text = launcherText;
    launcherText = "";
    save((s) => ({ ...s, launchers: addPrograms(s.launchers ?? [], text) }));
  }

  function addIgnore(event: SubmitEvent) {
    event.preventDefault();
    const text = ignoreText;
    ignoreText = "";
    save((s) => {
      const ignoreList = addPrograms(s.ignoreList, text);
      return { ...s, ignoreList, allowlist: s.allowlist.filter((name) => !ignoreList.includes(name)) };
    });
  }
</script>

<div class="stack">
  <Card icon="gamepad" title="Game-aware mode" status={status.status} tone={status.tone}>
    When a game is in the foreground on this PC, running VMs are paused (or saved) so the game has the PC, and exactly those
    VMs are resumed when it ends. A VM with a GPU partition is always paused. A VM someone is connected to keeps running.
    {#if info?.state.gaming}
      <span class="line">
        {info.state.changedVms.length > 0 ? `Paused or saved: ${info.state.changedVms.join(", ")}.` : "No VM was changed."}
        {#if info.state.sessionVms.length > 0}
          Left running because someone is connected: {info.state.sessionVms.join(", ")}.
        {/if}
      </span>
    {:else if info?.state.reason && info.settings.enabled}
      <span class="line">{info.state.reason}</span>
    {/if}
    {#snippet action()}
      {#if info}
        <label class="switch">
          <input
            type="checkbox"
            checked={info.settings.enabled}
            disabled={busy || !view.connected}
            onchange={(event) => save((s) => ({ ...s, enabled: event.currentTarget.checked }))}
          />
          <span>{info.settings.enabled ? "On" : "Off"}</span>
        </label>
        {#if info.state.gaming}
          <button type="button" class="btn" disabled={busy} onclick={() => tray.send({ type: "gameAwareNotAGame" }, "gameAwareApply")}>
            This was not a game
          </button>
        {/if}
      {/if}
    {/snippet}
  </Card>

  {#if info}
    <section class="block">
      <h2>Virtual machines</h2>
      <label for="ga-default">By default, running VMs are</label>
      <select
        id="ga-default"
        value={info.settings.defaultAction}
        disabled={busy}
        onchange={(event) => save((s) => ({ ...s, defaultAction: event.currentTarget.value as GameVmAction }))}
      >
        {#each Object.entries(ACTION_LABELS) as [value, label] (value)}<option {value}>{label}</option>{/each}
      </select>
      {#if info.vms.length === 0}
        <p class="muted">There are no virtual machines yet.</p>
      {:else}
        <table>
          <thead><tr><th>Virtual machine</th><th>While a game runs</th><th>Even if connected</th></tr></thead>
          <tbody>
            {#each info.vms as vm (vm.vmId)}
              {@const effective = effectiveAction(info, vm.vmId)}
              <tr>
                <td>{vm.name}</td>
                <td>
                  <select
                    aria-label="Action for {vm.name}"
                    value={effective.action}
                    disabled={busy || effective.forced}
                    title={effective.forced ? "A VM with a GPU partition is always paused." : undefined}
                    onchange={(event) => save((s) => withVmAction(s, vm.vmId, event.currentTarget.value as GameVmAction, effective.alwaysPause))}
                  >
                    {#each Object.entries(ACTION_LABELS) as [value, label] (value)}<option {value}>{label}</option>{/each}
                  </select>
                </td>
                <td>
                  <input
                    type="checkbox"
                    aria-label="Apply to {vm.name} even while someone is connected"
                    checked={effective.alwaysPause}
                    disabled={busy}
                    onchange={(event) => save((s) => withVmAction(s, vm.vmId, effective.action, event.currentTarget.checked))}
                  />
                </td>
              </tr>
            {/each}
          </tbody>
        </table>
      {/if}
    </section>

    <section class="block">
      <h2>Programs that count as games</h2>
      <p class="muted">
        Full-screen games, and borderless windows started by Steam, Epic, Battle.net, Xbox, GOG, or Playnite, are found by
        themselves. Add programs that are missed.
      </p>
      <ul class="chips">
        {#each info.settings.allowlist as name (name)}
          <li>
            {name}
            <button type="button" aria-label="Remove {name} from the allowlist" disabled={busy} onclick={() => save((s) => ({ ...s, allowlist: s.allowlist.filter((n) => n !== name) }))}>×</button>
          </li>
        {/each}
      </ul>
      <form onsubmit={addAllow}>
        <input type="text" aria-label="Add a program to the allowlist" placeholder="emulator.exe" bind:value={allowText} disabled={busy} />
        <button type="submit" class="btn" disabled={busy || allowText.trim() === ""}>Add</button>
      </form>
    </section>

    <section class="block">
      <h2>Programs that never count</h2>
      <p class="muted">For false positives, such as a presentation or a video player.</p>
      <ul class="chips">
        {#each info.settings.ignoreList as name (name)}
          <li>
            {name}
            <button type="button" aria-label="Remove {name} from the ignore list" disabled={busy} onclick={() => save((s) => ({ ...s, ignoreList: s.ignoreList.filter((n) => n !== name) }))}>×</button>
          </li>
        {/each}
      </ul>
      <form onsubmit={addIgnore}>
        <input type="text" aria-label="Add a program to the ignore list" placeholder="powerpnt.exe" bind:value={ignoreText} disabled={busy} />
        <button type="submit" class="btn" disabled={busy || ignoreText.trim() === ""}>Add</button>
      </form>
    </section>

    <section class="block">
      <h2>Game launchers</h2>
      <p class="muted">
        Steam, Epic, Battle.net, Xbox, GOG, and Playnite are built in. A game a launcher starts counts; its own window does not.
      </p>
      <ul class="chips">
        {#each info.settings.launchers ?? [] as name (name)}
          <li>
            {name}
            <button type="button" aria-label="Remove {name} from the launchers" disabled={busy} onclick={() => save((s) => ({ ...s, launchers: (s.launchers ?? []).filter((n) => n !== name) }))}>×</button>
          </li>
        {/each}
      </ul>
      <form onsubmit={addLauncher}>
        <input type="text" aria-label="Add a launcher" placeholder="itch.exe" bind:value={launcherText} disabled={busy} />
        <button type="submit" class="btn" disabled={busy || launcherText.trim() === ""}>Add</button>
      </form>
    </section>
  {/if}
</div>

<style>
  .stack {
    display: grid;
    gap: 0.8rem;
  }

  .block {
    display: grid;
    gap: 0.4rem;
  }

  h2 {
    margin: 0.4rem 0 0;
    font-size: 1rem;
  }

  .line {
    display: block;
    margin-top: 0.3rem;
  }

  table {
    width: 100%;
    border-collapse: collapse;
    font-size: 0.9rem;
  }

  th,
  td {
    text-align: left;
    padding: 0.25rem 0.5rem;
  }

  .chips {
    display: flex;
    flex-wrap: wrap;
    gap: 0.4rem;
    margin: 0;
    padding: 0;
    list-style: none;
  }

  .chips li {
    padding: 0.1rem 0.5rem;
    border: 1px solid var(--border, #444);
    border-radius: 999px;
    font-size: 0.85rem;
  }

  .chips button {
    border: none;
    background: none;
    cursor: pointer;
    color: inherit;
  }

  .switch {
    display: inline-flex;
    align-items: center;
    gap: 0.4rem;
  }
</style>
