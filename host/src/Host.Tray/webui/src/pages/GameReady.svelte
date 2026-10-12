<script lang="ts">
  import type { IconName } from "../components/Icon.svelte";
  import Card from "../components/Card.svelte";
  import ConfirmDialog from "../components/ConfirmDialog.svelte";
  import Modal from "../components/Modal.svelte";
  import { tray } from "../store.svelte";
  import type { GameReadyCard, PowerModeId, TrayViewState } from "../bridge";
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
    consoleSignIn: "lock",
    powerMode: "power",
    ambientScreen: "clock",
  };

  // Chords that are hard to press by accident. The stored value is the buttons joined by "+".
  const chords: { value: string; label: string }[] = [
    { value: "lb+rb+start", label: "LB + RB + Start" },
    { value: "back+start", label: "Back + Start" },
    { value: "lb+rb+back", label: "LB + RB + Back" },
    { value: "ls+rs", label: "Left stick + Right stick (press both in)" },
  ];

  const powerModes: { id: PowerModeId; name: string; detail: string }[] = [
    { id: "consoleIdle", name: "Console idle", detail: "Never sleeps on its own, so a controller press brings the TV back at once. The display turns off to save power." },
    { id: "balanced", name: "Balanced", detail: "Windows follows the power plan as it was before." },
    { id: "sleepWhenIdle", name: "Sleep when idle", detail: "Short timers for a desk PC: display off after 5 minutes, sleep after 15." },
  ];

  let confirmingTest = $state(false);
  let confirmingSleep = $state(false);
  let confirmingDisable = $state(false);
  let deleteUser = $state(false);
  // The form starts from what the service proposes and keeps what the person typed afterwards.
  let signInUser = $state("");
  let signInLauncher = $state("");
  let signInSeeded = $state(false);
  // The power mode form starts from the saved choice and keeps what the person typed afterwards.
  let pmMode = $state<PowerModeId>("balanced");
  let pmDisplay = $state(10);
  let pmSchedule = $state(false);
  let pmSleep = $state("01:00");
  let pmWake = $state("17:00");
  let pmWatts = $state(60);
  let pmRate = $state(0.15);
  let pmSeeded = $state(false);
  // The ambient screen form starts from the saved choice too.
  let amEnabled = $state(false);
  let amMinutes = $state(5);
  let amHost = $state(true);
  let amVms = $state(true);
  let amDevices = $state(false);
  let amSoftLock = $state(false);
  let amChord = $state("lb+rb+start");
  let amSeeded = $state(false);
  // The countdown text follows the clock while a test counts down.
  let now = $state(Date.now());

  const game = $derived(view.gameReady);
  const test = $derived(game?.wakeTest ?? null);
  const testRunning = $derived(test?.state === "countdown" || test?.state === "sleeping");
  const reading = $derived(game?.busy === "reading" || tray.requested.includes("gameReadyRefresh"));

  const signIn = $derived(game?.consoleSignIn ?? null);
  const signInBusy = $derived(game?.busy === "consoleSignIn" || tray.requested.includes("gameReadyConsoleSignIn"));
  const chosenLauncher = $derived(signIn?.launchers.find((launcher) => launcher.id === signInLauncher) ?? null);

  $effect(() => {
    if (signIn && !signInSeeded) {
      signInUser = signIn.userName;
      signInLauncher = signIn.launcher;
      signInSeeded = true;
    }
  });

  const power = $derived(game?.powerMode ?? null);
  const powerBusy = $derived(game?.busy === "powerMode" || tray.requested.includes("gameReadyPowerMode"));

  $effect(() => {
    if (power && !pmSeeded) {
      pmMode = power.mode;
      pmDisplay = power.displayMinutes;
      pmSchedule = power.scheduleEnabled;
      pmSleep = power.sleepTime;
      pmWake = power.wakeTime;
      pmWatts = power.watts;
      pmRate = power.ratePerKwh;
      pmSeeded = true;
    }
  });

  const ambient = $derived(game?.ambient ?? null);
  const ambientBusy = $derived(game?.busy === "ambient" || tray.requested.includes("gameReadyAmbient"));
  const chordOptions = $derived(chords.some((option) => option.value === amChord) ? chords : [...chords, { value: amChord, label: amChord }]);

  $effect(() => {
    if (ambient && !amSeeded) {
      amEnabled = ambient.enabled;
      amMinutes = ambient.idleMinutes;
      amHost = ambient.showHostStatus;
      amVms = ambient.showRunningVms;
      amDevices = ambient.showDevices;
      amSoftLock = ambient.softLock;
      amChord = ambient.chord;
      amSeeded = true;
    }
  });

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

  function enableSignIn() {
    tray.send(
      { type: "gameReadyConsoleSignInEnable", userName: signInUser.trim(), launcher: signInLauncher },
      "gameReadyConsoleSignIn",
    );
  }

  function disableSignIn() {
    confirmingDisable = false;
    tray.send({ type: "gameReadyConsoleSignInDisable", deleteUser }, "gameReadyConsoleSignIn");
    deleteUser = false;
  }

  function applyPower() {
    tray.send(
      {
        type: "gameReadyPowerModeApply",
        mode: pmMode,
        displayMinutes: Number(pmDisplay),
        scheduleEnabled: pmSchedule,
        sleepTime: pmSleep,
        wakeTime: pmWake,
        watts: Number(pmWatts),
        ratePerKwh: Number(pmRate),
      },
      "gameReadyPowerMode",
    );
  }

  function applyAmbient() {
    tray.send(
      {
        type: "gameReadyAmbientApply",
        enabled: amEnabled,
        idleMinutes: Number(amMinutes),
        showHostStatus: amHost,
        showRunningVms: amVms,
        showDevices: amDevices,
        softLock: amSoftLock,
        chord: amChord,
      },
      "gameReadyAmbient",
    );
  }

  function sleepNow() {
    confirmingSleep = false;
    tray.send({ type: "gameReadySleepNow" }, "gameReadySleepNow");
  }

  function money(value: number): string {
    return value.toFixed(2);
  }

  function load(value: number | null | undefined): string {
    return value == null ? "not available" : `${value}%`;
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

        {#if card.id === "consoleSignIn" && signIn}
          <form
            class="signin"
            onsubmit={(event) => {
              event.preventDefault();
              enableSignIn();
            }}
          >
            <label>
              <span>User name</span>
              <input type="text" bind:value={signInUser} maxlength="20" disabled={signIn.configured || signInBusy} autocomplete="off" spellcheck="false" />
            </label>
            <label>
              <span>Launcher</span>
              <select bind:value={signInLauncher} disabled={signInBusy}>
                {#each signIn.launchers as launcher (launcher.id)}
                  <option value={launcher.id} disabled={!launcher.available}>{launcher.name}{launcher.available ? "" : " (not installed)"}</option>
                {/each}
              </select>
            </label>
            {#if chosenLauncher && !chosenLauncher.available && chosenLauncher.reason}
              <p class="warn small">{chosenLauncher.reason}</p>
            {/if}
          </form>
        {/if}

        {#if card.id === "powerMode" && power}
          <form
            class="power"
            onsubmit={(event) => {
              event.preventDefault();
              applyPower();
            }}
          >
            <fieldset disabled={powerBusy}>
              <legend class="sr-only">Power mode</legend>
              {#each powerModes as option (option.id)}
                <label class="mode">
                  <input type="radio" name="power-mode" value={option.id} bind:group={pmMode} />
                  <span><strong>{option.name}</strong> <span class="muted">{option.detail}</span></span>
                </label>
              {/each}
            </fieldset>

            {#if pmMode === "consoleIdle"}
              <label class="field">
                <span>Display off after (minutes)</span>
                <input type="number" min="1" max="720" step="1" bind:value={pmDisplay} disabled={powerBusy} />
              </label>
              <label class="check">
                <input type="checkbox" bind:checked={pmSchedule} disabled={powerBusy} />
                <span>Sleep on a schedule (otherwise the host never sleeps on its own)</span>
              </label>
              {#if pmSchedule}
                <label class="field">
                  <span>Sleep at</span>
                  <input type="time" bind:value={pmSleep} disabled={powerBusy} />
                </label>
                <label class="field">
                  <span>Wake at</span>
                  <input type="time" bind:value={pmWake} disabled={powerBusy} />
                </label>
              {/if}
            {/if}

            <label class="field">
              <span>Idle power draw (watts)</span>
              <input type="number" min="1" max="2000" step="1" bind:value={pmWatts} disabled={powerBusy} />
            </label>
            <label class="field">
              <span>Price per kWh</span>
              <input type="number" min="0" max="100" step="any" bind:value={pmRate} disabled={powerBusy} />
            </label>
          </form>

          <ul class="plain" aria-label="Estimated cost">
            <li>
              Always ready at {power.estimate.watts} W: about {power.estimate.alwaysReadyKwh} kWh and {money(power.estimate.alwaysReadyMonthlyCost)} a month.
            </li>
            <li>
              Asleep from {power.sleepTime} to {power.wakeTime} (about {power.estimate.sleepWatts} W asleep): {power.estimate.scheduledKwh} kWh and {money(power.estimate.scheduledMonthlyCost)} a month.
            </li>
            <li class="muted">These are estimates from the figures above, not measurements.</li>
            {#if power.mode === "consoleIdle"}
              <li>Measured now: CPU {load(power.cpuPercent)}, GPU {load(power.gpuPercent)}.</li>
            {/if}
          </ul>
        {/if}

        {#if card.id === "ambientScreen" && ambient}
          <form
            class="power"
            onsubmit={(event) => {
              event.preventDefault();
              applyAmbient();
            }}
          >
            <label class="check first">
              <input type="checkbox" bind:checked={amEnabled} disabled={ambientBusy || !ambient.supported} />
              <span>Show a clock screen when the host is idle (Console idle mode)</span>
            </label>
            <label class="field">
              <span>Start after (minutes)</span>
              <input type="number" min="1" max="120" step="1" bind:value={amMinutes} disabled={ambientBusy || !ambient.supported} />
            </label>
            <label class="check">
              <input type="checkbox" bind:checked={amHost} disabled={ambientBusy || !ambient.supported} />
              <span>Show the host status</span>
            </label>
            <label class="check">
              <input type="checkbox" bind:checked={amVms} disabled={ambientBusy || !ambient.supported} />
              <span>Show the running virtual machines</span>
            </label>
            <label class="check">
              <input type="checkbox" bind:checked={amDevices} disabled={ambientBusy || !ambient.supported} />
              <span>Show the paired devices</span>
            </label>
            <label class="check">
              <input type="checkbox" bind:checked={amSoftLock} disabled={ambientBusy || !ambient.supported} />
              <span>Soft lock: a controller needs a chord to dismiss the screen (not a security feature)</span>
            </label>
            {#if amSoftLock}
              <label class="field">
                <span>Unlock chord</span>
                <select bind:value={amChord} disabled={ambientBusy || !ambient.supported}>
                  {#each chordOptions as option (option.value)}
                    <option value={option.value}>{option.label}</option>
                  {/each}
                </select>
              </label>
            {/if}
          </form>

          <ul class="plain" aria-label="Measured cost">
            <li>
              {#if ambient.measuredSeconds != null}
                Measured over the last {ambient.measuredSeconds} seconds of the screen: CPU {load(ambient.cpuPercent)}, GPU {load(ambient.gpuPercent)}.
              {:else}
                Cost: measured after the screen has run for a minute.
              {/if}
            </li>
            <li>HDMI-CEC adapter: {ambient.cecDevice ?? "none found"} (standby over CEC is not built yet).</li>
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
            {#if card.id === "consoleSignIn" && signIn}
              <button
                type="button"
                class="btn primary"
                disabled={signInBusy || !!card.fixBlockedReason || !chosenLauncher?.available || signInUser.trim().length === 0}
                onclick={enableSignIn}
              >
                {#if signInBusy}<span class="spinner"></span> Working…{:else if signIn.configured}Repair{:else}Set up{/if}
              </button>
              {#if signIn.configured}
                <button type="button" class="btn" disabled={signInBusy || !!card.fixBlockedReason} onclick={() => (confirmingDisable = true)}>
                  Disable…
                </button>
              {/if}
            {/if}
            {#if card.id === "ambientScreen" && ambient}
              <button type="button" class="btn primary" disabled={ambientBusy || !ambient.supported || !!card.fixBlockedReason} onclick={applyAmbient}>
                {#if ambientBusy}<span class="spinner"></span> Working…{:else}Apply{/if}
              </button>
            {/if}
            {#if card.id === "powerMode" && power}
              <button type="button" class="btn primary" disabled={powerBusy || !!card.fixBlockedReason} onclick={applyPower}>
                {#if powerBusy}<span class="spinner"></span> Working…{:else}Apply{/if}
              </button>
              <button type="button" class="btn" onclick={() => (confirmingSleep = true)}>Sleep now…</button>
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

{#if confirmingDisable}
  <Modal title="Turn off Console sign-in?" onclose={() => (confirmingDisable = false)}>
    <p>Windows stops signing in automatically, and the sign-in requirement after sleep is restored. The next start shows the sign-in screen.</p>
    <label class="check">
      <input type="checkbox" bind:checked={deleteUser} />
      <span>Also delete the {signIn?.userName ?? "Console"} user and its profile (its files are removed).</span>
    </label>
    {#snippet actions()}
      <button type="button" class="btn" onclick={() => (confirmingDisable = false)}>Cancel</button>
      <button type="button" class="btn {deleteUser ? 'danger' : 'primary'}" onclick={disableSignIn}>{deleteUser ? "Disable and delete" : "Disable"}</button>
    {/snippet}
  </Modal>
{/if}

{#if confirmingSleep}
  <ConfirmDialog
    title="Put this PC to sleep?"
    message="This PC goes to sleep a few seconds after you confirm, and remote sessions end. A controller press or the network wakes it again if that is set up."
    confirmLabel="Sleep now"
    onconfirm={sleepNow}
    oncancel={() => (confirmingSleep = false)}
  />
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

  .signin {
    display: grid;
    gap: 0.5rem;
    margin-top: 0.6rem;
  }

  .signin label {
    display: grid;
    grid-template-columns: 7rem minmax(0, 16rem);
    gap: 0.8rem;
    align-items: center;
    font-weight: 600;
  }

  .power {
    display: grid;
    gap: 0.5rem;
    margin-top: 0.6rem;
  }

  .power fieldset {
    display: grid;
    gap: 0.4rem;
    margin: 0;
    padding: 0;
    border: 0;
  }

  .mode {
    display: flex;
    gap: 0.6rem;
    align-items: flex-start;
  }

  .field {
    display: grid;
    grid-template-columns: 14rem minmax(0, 10rem);
    gap: 0.8rem;
    align-items: center;
    font-weight: 600;
  }

  .plain {
    margin-top: 0.6rem;
  }

  .sr-only {
    position: absolute;
    width: 1px;
    height: 1px;
    overflow: hidden;
    clip-path: inset(50%);
  }

  .check {
    display: flex;
    gap: 0.6rem;
    align-items: flex-start;
    margin-top: 0.8rem;
  }

  .power .check {
    margin-top: 0;
  }
</style>
