<script lang="ts">
  import { onMount } from "svelte";
  import {
    errorMessage,
    getConnectionRoute,
    getHostTailscale,
    getTailnetSettings,
    getTailnetStatus,
    setHostTailscaleSettings,
    setTailnetSettings,
    testTailscaleReachability,
    type ConnectionRoute,
    type HostEntry,
    type HostTailscale,
    type ReachabilityStep,
    type TailnetStatus,
  } from "$lib/api/client";
  import { ElevationCancelled, withElevation } from "$lib/lifecycle.svelte";
  import { hostTailscaleSummary, reachabilityVerdict, routeText, stepMark, tailnetSummary } from "$lib/tailscale";

  interface Props {
    host: HostEntry;
    /** False for a host older than the API that reports its Tailscale state. */
    hostSupportsTailscale: boolean;
  }

  let { host, hostSupportsTailscale }: Props = $props();

  let local = $state<TailnetStatus | null>(null);
  let hostInfo = $state<HostTailscale | null>(null);
  let route = $state<ConnectionRoute | null>(null);
  let loadError = $state<string | null>(null);
  let steps = $state<ReachabilityStep[] | null>(null);
  let testing = $state(false);
  let testError = $state<string | null>(null);
  let copied = $state(false);

  let clientPort = $state("41112");
  let hostPort = $state("41112");
  let hostUrl = $state("");
  let saving = $state(false);
  let saveMessage = $state<string | null>(null);
  let saveError = $state<string | null>(null);

  async function load() {
    loadError = null;
    try {
      const [status, settings, current] = await Promise.all([getTailnetStatus(), getTailnetSettings(), getConnectionRoute(host.key)]);
      local = status;
      clientPort = String(settings.localApiPort);
      route = current;
      if (hostSupportsTailscale) {
        hostInfo = await getHostTailscale(host.key);
        hostPort = String(hostInfo.config.localApiPort);
        hostUrl = hostInfo.config.controlUrl ?? "";
      }
    } catch (error) {
      loadError = errorMessage(error);
    }
  }

  onMount(() => {
    void load();
  });

  async function runTest() {
    testing = true;
    testError = null;
    steps = null;
    try {
      steps = await testTailscaleReachability(host.key);
    } catch (error) {
      testError = errorMessage(error);
    } finally {
      testing = false;
    }
  }

  async function copyCommand() {
    if (!hostInfo) return;
    try {
      await navigator.clipboard.writeText(hostInfo.policySnippet);
      copied = true;
      setTimeout(() => (copied = false), 2500);
    } catch {
      copied = false;
    }
  }

  async function saveClient(event: SubmitEvent) {
    event.preventDefault();
    saveError = null;
    saveMessage = null;
    try {
      await setTailnetSettings(Number(clientPort));
      local = await getTailnetStatus();
      saveMessage = "Saved for this PC.";
    } catch (error) {
      saveError = errorMessage(error);
    }
  }

  async function saveHost(event: SubmitEvent) {
    event.preventDefault();
    saving = true;
    saveError = null;
    saveMessage = null;
    try {
      const url = hostUrl.trim() === "" ? null : hostUrl.trim();
      hostInfo = await withElevation(host.key, () => setHostTailscaleSettings(host.key, Number(hostPort), url));
      saveMessage = "Saved on the host.";
    } catch (error) {
      if (!(error instanceof ElevationCancelled)) saveError = errorMessage(error);
    } finally {
      saving = false;
    }
  }
</script>

<section class="panel" aria-label="Tailscale">
  <h3>Tailscale</h3>
  <p class="muted">
    Tailscale lets this PC reach {host.displayName} away from home without opening a port. HyperHarbor uses the Tailscale you
    already have installed. Remote Desktop to the VMs still goes through the host, so the VMs need no Tailscale.
  </p>

  {#if loadError}
    <p class="error" role="alert">{loadError}</p>
  {/if}

  <dl>
    <dt>This PC</dt>
    <dd>{tailnetSummary(local)}</dd>

    <dt>{host.displayName}</dt>
    <dd>
      {#if !hostSupportsTailscale}
        It runs an older HyperHarbor version. Update the host to see its Tailscale state.
      {:else}
        {hostTailscaleSummary(hostInfo)}
      {/if}
    </dd>

    <dt>Connection</dt>
    <dd>
      {routeText(route)}
      {#if route?.hint}<span class="hint">{route.hint}</span>{/if}
    </dd>
  </dl>

  {#if hostInfo && hostInfo.state === "running" && !hostInfo.tagged}
    <div class="tag">
      <p>
        Tag the host so clients look at it first on the tailnet. First add this to your Tailscale policy (Access controls in the admin
        console). Then, under Machines, choose the host, Edit ACL tags, and add <code>{hostInfo.tag}</code>.
      </p>
      <pre>{hostInfo.policySnippet}</pre>
      <p class="muted">
        Do not run <code>tailscale up --advertise-tags</code> on a host that is signed in before the policy allows the tag: Tailscale
        signs it out.
      </p>
      <button type="button" onclick={copyCommand}>{copied ? "Copied" : "Copy policy entry"}</button>
    </div>
  {/if}

  <div class="actions">
    <button type="button" class="primary" onclick={runTest} disabled={testing}>{testing ? "Testing…" : "Test reachability"}</button>
    <button type="button" onclick={load} disabled={testing}>Refresh</button>
  </div>

  {#if testError}
    <p class="error" role="alert">{testError}</p>
  {/if}
  {#if steps}
    <p class="verdict" role="status">{reachabilityVerdict(steps)}</p>
    <ul class="steps" aria-label="Reachability checks">
      {#each steps as step (step.id)}
        <li>
          <span class="mark {step.status}">{stepMark(step.status)}</span>
          <span><strong>{step.title}</strong>{#if step.detail}<span class="muted"> {step.detail}</span>{/if}</span>
        </li>
      {/each}
    </ul>
  {/if}

  <details>
    <summary>Settings</summary>
    <form onsubmit={saveClient}>
      <label>
        This PC: Tailscale local API port
        <input type="number" min="1" max="65535" bind:value={clientPort} />
      </label>
      <div class="actions"><button type="submit">Save for this PC</button></div>
    </form>
    {#if hostSupportsTailscale}
      <form onsubmit={saveHost}>
        <label>
          Host: Tailscale local API port
          <input type="number" min="1" max="65535" bind:value={hostPort} disabled={saving} />
        </label>
        <label>
          Host: control server (Headscale), optional
          <input type="url" placeholder="https://headscale.example.com" bind:value={hostUrl} disabled={saving} />
        </label>
        <p class="muted">Changing the host's settings needs the admin passphrase.</p>
        <div class="actions"><button type="submit" disabled={saving}>{saving ? "Saving…" : "Save on the host"}</button></div>
      </form>
    {/if}
    {#if saveMessage}<p role="status">{saveMessage}</p>{/if}
    {#if saveError}<p class="error" role="alert">{saveError}</p>{/if}
  </details>
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

  p {
    margin: 0 0 0.75rem;
  }

  dl {
    display: grid;
    grid-template-columns: max-content 1fr;
    gap: 0.4rem 1rem;
    margin: 0 0 1rem;
  }

  dt {
    font-weight: 600;
  }

  dd {
    margin: 0;
  }

  .hint {
    display: block;
    margin-top: 0.25rem;
    color: var(--muted);
    font-size: 0.85rem;
  }

  .muted {
    color: var(--muted);
  }

  .error {
    color: var(--danger);
  }

  pre {
    margin: 0 0 0.75rem;
    padding: 0.5rem 0.75rem;
    border-radius: 6px;
    background: var(--border);
    overflow-x: auto;
  }

  .tag {
    margin-bottom: 1rem;
    padding: 0.75rem;
    border: 1px solid var(--border);
    border-radius: 6px;
  }

  .actions {
    display: flex;
    gap: 0.5rem;
    margin-bottom: 0.75rem;
  }

  .actions button.primary {
    background: var(--accent);
    border-color: var(--accent);
    color: var(--accent-fg);
  }

  .verdict {
    font-weight: 600;
  }

  .steps {
    list-style: none;
    display: grid;
    gap: 0.35rem;
    margin: 0 0 1rem;
    padding: 0;
  }

  .steps li {
    display: flex;
    gap: 0.6rem;
    align-items: baseline;
  }

  .mark {
    padding: 0.05rem 0.55rem;
    border-radius: 999px;
    font-size: 0.78rem;
    font-weight: 600;
    background: var(--border);
  }

  .mark.pass {
    background: color-mix(in srgb, var(--accent) 20%, transparent);
  }

  .mark.fail {
    color: var(--danger);
  }

  details {
    margin-top: 0.5rem;
  }

  form {
    display: flex;
    flex-direction: column;
    gap: 0.75rem;
    margin-top: 0.75rem;
  }

  label {
    display: flex;
    flex-direction: column;
    gap: 0.25rem;
    font-weight: 600;
  }

  input {
    max-width: 18rem;
  }
</style>
