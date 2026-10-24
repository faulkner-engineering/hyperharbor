<script lang="ts">
  import { onDestroy, onMount } from "svelte";
  import {
    errorMessage,
    getGameAware,
    notAGame,
    setGameAware,
    type GameAwareInfo,
    type GameAwareSettings,
    type GameVmAction,
    type HostEntry,
  } from "$lib/api/client";
  import { ACTION_LABELS, addPrograms, effectiveAction, gamingNotice, withVmAction } from "$lib/gameAware";
  import { ElevationCancelled, withElevation } from "$lib/lifecycle.svelte";

  interface Props {
    host: HostEntry;
  }

  let { host }: Props = $props();

  const POLL_MS = 5000;

  let info = $state<GameAwareInfo | null>(null);
  let loadError = $state<string | null>(null);
  let saving = $state(false);
  let error = $state<string | null>(null);
  let notice = $state<string | null>(null);
  let allowText = $state("");
  let ignoreText = $state("");
  let launcherText = $state("");
  let timer: ReturnType<typeof setInterval> | undefined;

  const gaming = $derived(info ? gamingNotice(info.state) : null);

  async function load() {
    try {
      info = await getGameAware(host.key);
      loadError = null;
    } catch (e) {
      loadError = errorMessage(e);
    }
  }

  onMount(() => {
    void load();
    timer = setInterval(() => void load(), POLL_MS);
  });
  onDestroy(() => clearInterval(timer));

  /** Saves a changed copy of the settings; the host answers with what it stored. */
  async function save(change: (current: GameAwareSettings) => GameAwareSettings) {
    if (!info || saving) return;
    saving = true;
    error = null;
    notice = null;
    try {
      const next = change(info.settings);
      const stored = await withElevation(host.key, () => setGameAware(host.key, next));
      info = { ...info, settings: stored };
    } catch (e) {
      if (!(e instanceof ElevationCancelled)) error = errorMessage(e);
    } finally {
      saving = false;
    }
  }

  async function notGame() {
    saving = true;
    error = null;
    try {
      const result = await notAGame(host.key);
      notice = result.ignored ? `${result.ignored} will not count as a game again.` : "No game was being detected.";
      await load();
    } catch (e) {
      error = errorMessage(e);
    } finally {
      saving = false;
    }
  }

  const addAllow = () => {
    const text = allowText;
    allowText = "";
    return save((s) => ({ ...s, allowlist: addPrograms(s.allowlist, text) }));
  };

  const addLauncher = () => {
    const text = launcherText;
    launcherText = "";
    return save((s) => ({ ...s, launchers: addPrograms(s.launchers ?? [], text) }));
  };

  const addIgnore = () => {
    const text = ignoreText;
    ignoreText = "";
    return save((s) => {
      const ignoreList = addPrograms(s.ignoreList, text);
      return { ...s, ignoreList, allowlist: s.allowlist.filter((name) => !ignoreList.includes(name)) };
    });
  };
</script>

<section class="panel" aria-label="Game-aware mode">
  <h3>Game-aware mode</h3>
  {#if loadError}<p class="error" role="alert">{loadError}</p>{/if}

  {#if info}
    <p class="muted">
      When a game is in the foreground on the host, HyperHarbor pauses (or saves) the running VMs so the game has the PC to
      itself, and resumes exactly those it changed when the game ends. A VM with a GPU partition is always paused.
    </p>

    <label class="check">
      <input
        type="checkbox"
        checked={info.settings.enabled}
        disabled={saving}
        onchange={(event) => save((s) => ({ ...s, enabled: event.currentTarget.checked }))}
      />
      Pause VMs while a game is running on the host
    </label>

    {#if gaming}
      <p class="warning" role="status">{gaming}</p>
      <button type="button" onclick={notGame} disabled={saving}>This was not a game</button>
    {:else if info.state.enabled}
      <p class="muted" role="status">No game is running.{info.state.reason ? ` ${info.state.reason}` : ""}</p>
    {/if}

    <label for="ga-default">By default, VMs are</label>
    <select
      id="ga-default"
      value={info.settings.defaultAction}
      disabled={saving}
      onchange={(event) => save((s) => ({ ...s, defaultAction: event.currentTarget.value as GameVmAction }))}
    >
      {#each Object.entries(ACTION_LABELS) as [action, label] (action)}<option value={action}>{label}</option>{/each}
    </select>

    {#if info.vms.length > 0}
      <table class="vms">
        <thead><tr><th>Virtual machine</th><th>While a game runs</th><th>Even if I am connected</th></tr></thead>
        <tbody>
          {#each info.vms as vm (vm.vmId)}
            {@const effective = effectiveAction(info, vm.vmId)}
            <tr>
              <td>{vm.name}</td>
              <td>
                <select
                  aria-label="Action for {vm.name}"
                  value={effective.action}
                  disabled={saving || effective.forced}
                  title={effective.forced ? "A VM with a GPU partition is always paused." : ""}
                  onchange={(event) =>
                    save((s) => withVmAction(s, vm.vmId, event.currentTarget.value as GameVmAction, effective.alwaysPause))}
                >
                  {#each Object.entries(ACTION_LABELS) as [action, label] (action)}<option value={action}>{label}</option>{/each}
                </select>
              </td>
              <td>
                <input
                  type="checkbox"
                  aria-label="Apply to {vm.name} even while I am connected"
                  checked={effective.alwaysPause}
                  disabled={saving}
                  onchange={(event) => save((s) => withVmAction(s, vm.vmId, effective.action, event.currentTarget.checked))}
                />
              </td>
            </tr>
          {/each}
        </tbody>
      </table>
      <p class="muted">A VM someone is connected to is left running unless "even if I am connected" is ticked.</p>
    {/if}

    <h4>Programs that count as games</h4>
    <p class="muted">
      Full-screen games are found by themselves, and so are borderless windows started by Steam, Epic, Battle.net, Xbox, GOG,
      or Playnite. Add programs that are missed.
    </p>
    <ul class="chips">
      {#each info.settings.allowlist as name (name)}
        <li>
          {name}
          <button type="button" aria-label="Remove {name} from the allowlist" disabled={saving} onclick={() => save((s) => ({ ...s, allowlist: s.allowlist.filter((n) => n !== name) }))}>×</button>
        </li>
      {/each}
    </ul>
    <form onsubmit={(event) => { event.preventDefault(); void addAllow(); }}>
      <input type="text" aria-label="Add a program to the allowlist" placeholder="emulator.exe" bind:value={allowText} disabled={saving} />
      <button type="submit" disabled={saving || allowText.trim() === ""}>Add</button>
    </form>

    <h4>Programs that never count</h4>
    <ul class="chips">
      {#each info.settings.ignoreList as name (name)}
        <li>
          {name}
          <button type="button" aria-label="Remove {name} from the ignore list" disabled={saving} onclick={() => save((s) => ({ ...s, ignoreList: s.ignoreList.filter((n) => n !== name) }))}>×</button>
        </li>
      {/each}
    </ul>
    <form onsubmit={(event) => { event.preventDefault(); void addIgnore(); }}>
      <input type="text" aria-label="Add a program to the ignore list" placeholder="powerpnt.exe" bind:value={ignoreText} disabled={saving} />
      <button type="submit" disabled={saving || ignoreText.trim() === ""}>Add</button>
    </form>

    <h4>Game launchers</h4>
    <p class="muted">
      Steam, Epic, Battle.net, Xbox, GOG, and Playnite are built in. A game one of them starts counts as a game; the launcher's
      own window does not. Add another launcher here.
    </p>
    <ul class="chips">
      {#each info.settings.launchers ?? [] as name (name)}
        <li>
          {name}
          <button type="button" aria-label="Remove {name} from the launchers" disabled={saving} onclick={() => save((s) => ({ ...s, launchers: (s.launchers ?? []).filter((n) => n !== name) }))}>×</button>
        </li>
      {/each}
    </ul>
    <form onsubmit={(event) => { event.preventDefault(); void addLauncher(); }}>
      <input type="text" aria-label="Add a launcher" placeholder="itch.exe" bind:value={launcherText} disabled={saving} />
      <button type="submit" disabled={saving || launcherText.trim() === ""}>Add</button>
    </form>

    {#if notice}<p role="status">{notice}</p>{/if}
    {#if error}<p class="error" role="alert">{error}</p>{/if}
  {:else if !loadError}
    <p class="muted">Reading the settings…</p>
  {/if}
</section>

<style>
  .panel {
    margin: 1rem 0;
  }

  h3,
  h4 {
    margin: 0.5rem 0;
  }

  .vms {
    width: 100%;
    border-collapse: collapse;
    font-size: 0.9rem;
    margin: 0.5rem 0;
  }

  .vms th,
  .vms td {
    text-align: left;
    padding: 0.25rem 0.5rem;
  }

  .chips {
    display: flex;
    flex-wrap: wrap;
    gap: 0.4rem;
    margin: 0.25rem 0;
    padding: 0;
    list-style: none;
  }

  .chips li {
    padding: 0.1rem 0.5rem;
    border: 1px solid var(--border);
    border-radius: 999px;
    font-size: 0.85rem;
  }

  .chips button {
    padding: 0 0.2rem;
    border: none;
    background: none;
    cursor: pointer;
    color: inherit;
  }
</style>
