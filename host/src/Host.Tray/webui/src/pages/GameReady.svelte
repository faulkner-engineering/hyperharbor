<script lang="ts">
  import type { IconName } from "../components/Icon.svelte";
  import Card from "../components/Card.svelte";
  import ConfirmDialog from "../components/ConfirmDialog.svelte";
  import { tray } from "../store.svelte";
  import type { GameReadyCard, TrayViewState } from "../bridge";
  import { cardBusy, cardStatus, cardTone, connectionLabel, gameReadySummary, wakeLabel, wakeTestText } from "../text";

  interface Props {
    view: TrayViewState;
  }

  let { view }: Props = $props();

  const TEST_DELAY_SECONDS = 10;

  const icons: Record<string, IconName> = {
    wakeOnLan: "devices",
    controllerWake: "gamepad",
    fastStartup: "power",
    usbSelectiveSuspend: "plug",
    usbPowerInSleep: "info",
  };

  let confirmingTest = $state(false);
  // The countdown text follows the clock while a test counts down.
  let now = $state(Date.now());

  const game = $derived(view.gameReady);
  const test = $derived(game?.wakeTest ?? null);
  const testRunning = $derived(test?.state === "countdown" || test?.state === "sleeping");
  const reading = $derived(game?.busy === "reading" || tray.requested.includes("gameReadyRefresh"));

  $effect(() => {
    if (test?.state !== "countdown") return;
    const timer = setInterval(() => (now = Date.now()), 500);
    return () => clearInterval(timer);
  });

  function refresh() {
    tray.send({ type: "gameReadyRefresh" }, "gameReadyRefresh");
  }

  function fix(card: GameReadyCard) {
    tray.send({ type: "gameReadyFix", cardId: card.id }, `gameReadyFix:${card.id}`);
  }

  function startTest() {
    confirmingTest = false;
    tray.send({ type: "gameReadyTestWake", delaySeconds: TEST_DELAY_SECONDS }, "gameReadyTest");
  }

  function fixing(card: GameReadyCard): boolean {
    return (game !== null && cardBusy(game, card)) || tray.requested.includes(`gameReadyFix:${card.id}`);
  }
</script>

{#if !game}
  <section class="panel">
    <p class="muted">{view.connected ? "Reading this PC…" : "Unknown while the service is not running."}</p>
  </section>
{:else}
  <section class="panel head">
    <div>
      <p class="summary">{gameReadySummary(game)}</p>
      <p class="muted small">
        Wake the PC from a controller or the network, and keep it ready for games.
        {#if game.readAt}Read {new Date(game.readAt).toLocaleTimeString()}.{/if}
      </p>
    </div>
    <button type="button" class="btn" disabled={reading} onclick={refresh}>
      {#if reading}<span class="spinner"></span> Reading…{:else}Check again{/if}
    </button>
  </section>

  {#if game.error}
    <p class="notice" role="alert">{game.error}</p>
  {/if}

  <div class="cards">
    {#each game.cards as card (card.id)}
      <Card
        icon={icons[card.id] ?? "clock"}
        title={card.title}
        status={cardStatus(game, card)}
        tone={cardTone(game, card)}
      >
        {#if card.state === "comingSoon"}
          <p>{card.summary}</p>
        {/if}

        {#if card.checks.length > 0}
          <ul class="checks">
            {#each card.checks as check (check.id)}
              <li>
                <span class="pill {check.status}">{check.status === "pass" ? "OK" : check.status === "warn" ? "Check" : "Fix"}</span>
                <span><strong>{check.title}</strong>{#if check.detail}<span class="muted"> {check.detail}</span>{/if}</span>
              </li>
            {/each}
          </ul>
        {/if}

        {#if card.controllers.length > 0}
          <ul class="devices" aria-label="Controllers and receivers">
            {#each card.controllers as controller (controller.id)}
              <li>
                <div class="row">
                  <strong>{controller.name}</strong>
                  <span class="muted">{connectionLabel(controller)}</span>
                  <span class="pill wake-{controller.wake}">{wakeLabel(controller.wake)}</span>
                </div>
                {#if controller.note}<p class="muted note">{controller.note}</p>{/if}
              </li>
            {/each}
          </ul>
        {/if}

        {#if card.id === "controllerWake" && test}
          {@const line = wakeTestText(test, now)}
          {#if line}
            <p class="test {test.state === 'done' && test.outcome === 'controller' ? 'good' : ''}" role="status">{line}</p>
          {/if}
        {/if}

        {#if card.hints.length > 0}
          <ul class="hints">
            {#each card.hints as hint, index (index)}
              <li>{hint}</li>
            {/each}
          </ul>
        {/if}

        {#if card.fixBlockedReason}
          <p class="warn small">{card.fixBlockedReason}</p>
        {/if}

        {#snippet action()}
          <div class="actions">
            {#if card.canFix}
              <button type="button" class="btn primary" disabled={fixing(card)} onclick={() => fix(card)}>
                {#if fixing(card)}<span class="spinner"></span> Fixing…{:else}Fix{/if}
              </button>
            {/if}
            {#if card.id === "controllerWake"}
              <button
                type="button"
                class="btn"
                disabled={!card.canTestWake || testRunning || tray.requested.includes("gameReadyTest")}
                title={card.canTestWake ? undefined : "Arm a controller or receiver first."}
                onclick={() => (confirmingTest = true)}
              >
                Test wake…
              </button>
            {/if}
          </div>
        {/snippet}
      </Card>
    {/each}
  </div>
{/if}

{#if confirmingTest}
  <ConfirmDialog
    title="Put this PC to sleep?"
    message={`This PC goes to sleep ${TEST_DELAY_SECONDS} seconds after you confirm, and remote sessions end. Press a button on the controller to wake it. HyperHarbor then reports what Windows says woke it.`}
    confirmLabel="Sleep in 10 seconds"
    onconfirm={startTest}
    oncancel={() => (confirmingTest = false)}
  />
{/if}

<style>
  .panel {
    padding: 1.1rem 1.3rem;
    border: 1px solid var(--border);
    border-radius: var(--radius);
    background: var(--surface);
    box-shadow: var(--shadow);
  }

  .head {
    display: flex;
    justify-content: space-between;
    align-items: center;
    gap: 1rem;
    margin-bottom: 0.8rem;
  }

  .summary {
    font-size: 1.05rem;
    font-weight: 600;
  }

  .small {
    font-size: 0.82rem;
  }

  .cards {
    display: grid;
    gap: 0.7rem;
  }

  .notice {
    margin: 0 0 0.8rem;
    padding: 0.6rem 0.9rem;
    border-radius: var(--radius);
    background: var(--warn-bg);
    color: var(--warn-fg);
  }

  .actions {
    display: flex;
    gap: 0.5rem;
    flex-wrap: wrap;
    justify-content: flex-end;
  }

  ul {
    list-style: none;
    display: grid;
    gap: 0.35rem;
    margin: 0.4rem 0 0;
    padding: 0;
  }

  .hints {
    list-style: disc;
    padding-left: 1.1rem;
  }

  .checks li {
    display: flex;
    gap: 0.6rem;
    align-items: baseline;
  }

  .row {
    display: flex;
    gap: 0.6rem;
    align-items: baseline;
    flex-wrap: wrap;
  }

  .note {
    margin: 0.15rem 0 0;
  }

  .pill {
    padding: 0.05rem 0.55rem;
    border-radius: 999px;
    background: var(--hover);
    color: var(--muted);
    font-size: 0.78rem;
    font-weight: 600;
    white-space: nowrap;
  }

  .pill.pass,
  .pill.wake-armed {
    background: var(--ok-bg);
    color: var(--ok-fg);
  }

  .pill.warn,
  .pill.fail,
  .pill.wake-canArm {
    background: var(--warn-bg);
    color: var(--warn-fg);
  }

  .test {
    margin-top: 0.5rem;
    padding: 0.5rem 0.7rem;
    border-radius: 8px;
    background: var(--busy-bg);
    color: var(--busy-fg);
  }

  .test.good {
    background: var(--ok-bg);
    color: var(--ok-fg);
  }

  .warn {
    color: var(--warn-fg);
  }
</style>
