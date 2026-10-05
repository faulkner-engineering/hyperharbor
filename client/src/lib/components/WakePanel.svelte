<script lang="ts">
  import { onDestroy, onMount } from "svelte";
  import {
    errorMessage,
    fixWake,
    getWakeReadiness,
    isOffline,
    listVms,
    startWakeTest,
    wakeHost,
    type HostEntry,
    type WakeReadiness,
  } from "$lib/api/client";
  import { ElevationCancelled, withElevation } from "$lib/lifecycle.svelte";

  interface Props {
    host: HostEntry;
  }

  let { host }: Props = $props();

  const POLL_MS = 3000;
  const TEST_DELAY_SECONDS = 15;
  const SETTLE_MS = 15_000;
  const WAKE_TIMEOUT_MS = 120_000;
  const APPROVAL_TIMEOUT_MS = 90_000;

  type TestStage =
    | { kind: "idle" }
    | { kind: "confirm" }
    | { kind: "waitingForSleep"; sleepAt: number }
    | { kind: "settling" }
    | { kind: "waking"; sentAt: number }
    | { kind: "passed"; seconds: number }
    | { kind: "failed"; message: string };

  let readiness = $state<WakeReadiness | null>(null);
  let loadError = $state<string | null>(null);
  let fixState = $state<"idle" | "applying" | "awaitingApproval">("idle");
  let fixMessage = $state<string | null>(null);
  let test = $state<TestStage>({ kind: "idle" });
  let now = $state(Date.now());

  let cancelled = false;
  const ticker = setInterval(() => (now = Date.now()), 1000);
  onDestroy(() => {
    cancelled = true;
    clearInterval(ticker);
  });

  const fixable = $derived(
    readiness?.checks.filter((check) => check.status !== "pass" && check.autoFixAvailable) ?? [],
  );
  const testBusy = $derived(
    test.kind === "waitingForSleep" || test.kind === "settling" || test.kind === "waking",
  );

  onMount(() => {
    load();
  });

  async function load() {
    try {
      readiness = await getWakeReadiness(host.key);
      loadError = null;
    } catch (error) {
      loadError = errorMessage(error);
    }
  }

  async function applyFixes() {
    fixState = "applying";
    fixMessage = null;
    const before = JSON.stringify(readiness);
    try {
      // Fixes change host settings, so they need the admin passphrase.
      const outcome = await withElevation(host.key, () =>
        fixWake(
          host.key,
          fixable.map((check) => check.id),
        ),
      );
      if (outcome.status === "applied" && outcome.readiness) {
        readiness = outcome.readiness;
        fixState = "idle";
        return;
      }

      // The host tray asks the user to approve with a UAC prompt; watch for the settings to change.
      fixState = "awaitingApproval";
      const deadline = Date.now() + APPROVAL_TIMEOUT_MS;
      while (!cancelled && Date.now() < deadline) {
        await delay(POLL_MS);
        const latest = await getWakeReadiness(host.key);
        if (JSON.stringify(latest) !== before) {
          readiness = latest;
          fixState = "idle";
          fixMessage = "Settings updated on the host.";
          return;
        }
      }
      fixState = "idle";
      fixMessage = "No change yet. If the request was approved, refresh in a moment.";
    } catch (error) {
      fixState = "idle";
      fixMessage = error instanceof ElevationCancelled ? null : errorMessage(error);
    }
  }

  async function runTest() {
    try {
      const scheduled = await startWakeTest(host.key, TEST_DELAY_SECONDS);
      const sleepAt = Date.parse(scheduled.sleepAt);
      test = { kind: "waitingForSleep", sleepAt };

      // Wait for the host to stop answering.
      const sleepDeadline = sleepAt + 60_000;
      while (!(await hostIsOffline())) {
        if (cancelled) return;
        if (Date.now() > sleepDeadline) {
          test = { kind: "failed", message: "The host did not go to sleep. It may be prevented by an open app or a power setting." };
          return;
        }
        await delay(POLL_MS);
      }

      // Give the host time to finish entering sleep before waking it.
      test = { kind: "settling" };
      await delay(SETTLE_MS);
      if (cancelled) return;

      await wakeHost(host.key);
      const sentAt = Date.now();
      test = { kind: "waking", sentAt };

      while (Date.now() - sentAt < WAKE_TIMEOUT_MS) {
        if (cancelled) return;
        await delay(POLL_MS);
        if (!(await hostIsOffline())) {
          test = { kind: "passed", seconds: Math.round((Date.now() - sentAt) / 1000) };
          return;
        }
      }

      test = {
        kind: "failed",
        message:
          "The host did not wake within 2 minutes. Wake it by hand. If the checks above pass, the usual cause is a BIOS or UEFI setting: see \"Also check the host's BIOS or UEFI settings\" above.",
      };
    } catch (error) {
      test = { kind: "failed", message: errorMessage(error) };
    }
  }

  async function hostIsOffline(): Promise<boolean> {
    try {
      await listVms(host.key);
      return false;
    } catch (error) {
      if (isOffline(error)) return true;
      throw error;
    }
  }

  function delay(ms: number) {
    return new Promise((resolve) => setTimeout(resolve, ms));
  }
</script>

<section class="panel" aria-label="Wake-on-LAN">
  <h3>Wake-on-LAN</h3>

  {#if loadError}
    <p class="error" role="alert">{loadError}</p>
  {:else if readiness === null}
    <p class="muted">Checking the host's settings…</p>
  {:else}
    <p class="summary" class:ready={readiness.ready}>
      {readiness.ready
        ? "This host is set up to be woken over the network."
        : "This host cannot be woken over the network yet."}
    </p>

    <ul class="checks">
      {#each readiness.checks as check (check.id)}
        <li>
          <span class="status {check.status}">{check.status === "pass" ? "OK" : check.status === "warn" ? "Check" : "Fix"}</span>
          <div>
            <div class="title">{check.title}</div>
            {#if check.detail}<div class="detail">{check.detail}</div>{/if}
          </div>
        </li>
      {/each}
    </ul>

    {#if fixable.length > 0}
      <div class="row">
        <button type="button" class="primary" onclick={applyFixes} disabled={fixState !== "idle"}>
          {fixState === "applying" ? "Sending…" : `Fix ${fixable.length} setting${fixable.length === 1 ? "" : "s"}`}
        </button>
        {#if fixState === "awaitingApproval"}
          <span class="muted">Approve the change on the host. Windows will ask for administrator permission there.</span>
        {/if}
      </div>
    {/if}
    {#if fixMessage}<p class="muted">{fixMessage}</p>{/if}

    <div class="firmware" role="note">
      <p class="firmware-title">Also check the host's BIOS or UEFI settings</p>
      <p class="muted">
        Windows cannot read these, so the checks above can all pass while waking still fails.
        Setting names vary by manufacturer:
      </p>
      <ul>
        <li>Turn <strong>on</strong> "Wake on LAN", "Power On By PCI-E", or "Resume by PCI-E Device".</li>
        <li>Turn <strong>off</strong> "ErP", "ErP Ready", or "Deep Sleep".</li>
      </ul>
      <p class="muted">Run Test wake after changing them to confirm.</p>
    </div>
  {/if}

  <h4>Test wake</h4>
  {#if test.kind === "idle"}
    <p class="muted">
      Puts the host to sleep, then wakes it from this device to confirm everything works.
    </p>
    <button type="button" onclick={() => (test = { kind: "confirm" })} disabled={!host.canWake}>
      Test wake…
    </button>
    {#if !host.canWake}
      <p class="muted">The host has not reported a wired network adapter, so it cannot be woken yet.</p>
    {/if}
  {:else if test.kind === "confirm"}
    <p>
      {host.displayName} will go to sleep in {TEST_DELAY_SECONDS} seconds. Running virtual machines
      keep their state, but save any open work on the host first.
    </p>
    <div class="row">
      <button type="button" class="primary" onclick={runTest}>Put host to sleep</button>
      <button type="button" onclick={() => (test = { kind: "idle" })}>Cancel</button>
    </div>
  {:else if test.kind === "waitingForSleep"}
    <p>
      {now < test.sleepAt
        ? `Host goes to sleep in ${Math.ceil((test.sleepAt - now) / 1000)} seconds…`
        : "Waiting for the host to go to sleep…"}
    </p>
  {:else if test.kind === "settling"}
    <p>The host is asleep. Sending the wake signal shortly…</p>
  {:else if test.kind === "waking"}
    <p>Wake signal sent. Waiting for the host to respond ({Math.round((now - test.sentAt) / 1000)} s)…</p>
  {:else if test.kind === "passed"}
    <p class="ok">The host woke up {test.seconds} seconds after the wake signal.</p>
    <button type="button" onclick={() => (test = { kind: "idle" })}>Done</button>
  {:else if test.kind === "failed"}
    <p class="error" role="alert">{test.message}</p>
    <button type="button" onclick={() => (test = { kind: "idle" })}>Done</button>
  {/if}

  {#if testBusy}<p class="muted">Keep this window open until the test finishes.</p>{/if}
</section>

<style>
  .panel {
    margin-top: 1.25rem;
    padding: 1.25rem;
    border: 1px solid var(--border);
    border-radius: 8px;
    background: var(--surface);
    max-width: 640px;
  }

  h3 {
    margin: 0 0 0.75rem;
    font-size: 1.05rem;
  }

  h4 {
    margin: 1.25rem 0 0.5rem;
    font-size: 0.95rem;
  }

  p {
    margin: 0 0 0.75rem;
  }

  .summary {
    font-weight: 600;
    color: var(--danger);
  }

  .summary.ready,
  .ok {
    color: var(--ok-fg);
  }

  .checks {
    list-style: none;
    margin: 0 0 1rem;
    padding: 0;
    display: flex;
    flex-direction: column;
    gap: 0.6rem;
  }

  .checks li {
    display: flex;
    gap: 0.75rem;
    align-items: flex-start;
  }

  .status {
    flex: none;
    min-width: 3.2rem;
    text-align: center;
    padding: 0.1rem 0.4rem;
    border-radius: 999px;
    font-size: 0.75rem;
    font-weight: 700;
  }

  .status.pass {
    background: var(--ok-bg);
    color: var(--ok-fg);
  }

  .status.warn {
    background: var(--warn-bg);
    color: var(--warn-fg);
  }

  .status.fail {
    background: var(--error-bg);
    color: var(--danger);
  }

  .title {
    font-weight: 600;
  }

  .detail,
  .muted {
    color: var(--muted);
    font-size: 0.9rem;
  }

  .row {
    display: flex;
    align-items: center;
    gap: 0.75rem;
    margin-bottom: 0.75rem;
  }

  button {
    padding: 0.45rem 1rem;
    border: 1px solid var(--border);
    border-radius: 6px;
    background: var(--surface);
    color: inherit;
    cursor: pointer;
  }

  button.primary {
    background: var(--accent);
    border-color: var(--accent);
    color: var(--accent-fg);
  }

  button:disabled {
    opacity: 0.5;
    cursor: default;
  }

  .error {
    color: var(--danger);
  }

  .firmware {
    margin: 1rem 0 0;
    padding: 0.75rem 1rem;
    border-radius: 6px;
    background: var(--notice-bg);
  }

  .firmware p {
    margin: 0 0 0.4rem;
  }

  .firmware-title {
    font-weight: 600;
  }

  .firmware ul {
    margin: 0 0 0.4rem;
    padding-left: 1.2rem;
  }
</style>
