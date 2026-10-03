<script lang="ts">
  import { elevate, errorMessage, hasProblemCode, ProblemCodes, type HostEntry } from "$lib/api/client";
  import { elevation } from "$lib/lifecycle.svelte";
  import Dialog from "./Dialog.svelte";

  interface Props {
    host: HostEntry;
  }

  let { host }: Props = $props();

  let passphrase = $state("");
  let busy = $state(false);
  let error = $state<string | null>(null);

  async function submit(event: SubmitEvent) {
    event.preventDefault();
    busy = true;
    error = null;
    try {
      const granted = await elevate(host.key, passphrase);
      passphrase = "";
      elevation.finish(true, granted.expiresAt);
    } catch (e) {
      passphrase = "";
      error = hasProblemCode(e, ProblemCodes.incorrectPassphrase) ? "That passphrase is not correct." : errorMessage(e);
    } finally {
      busy = false;
    }
  }

  function cancel() {
    passphrase = "";
    elevation.finish(false);
  }
</script>

<Dialog title="Admin passphrase">
  <form onsubmit={submit}>
    <p class="muted">
      This change needs the admin passphrase set in the HyperHarbor tray on {host.displayName}. This device
      stays elevated for 5 minutes.
    </p>
    <label for="admin-passphrase">Passphrase</label>
    <!-- svelte-ignore a11y_autofocus -->
    <input id="admin-passphrase" type="password" bind:value={passphrase} autocomplete="off" disabled={busy} autofocus />
    {#if error}<p class="error" role="alert">{error}</p>{/if}
    <div class="actions">
      <button type="button" onclick={cancel} disabled={busy}>Cancel</button>
      <button type="submit" class="primary" disabled={busy || passphrase === ""}>{busy ? "Checking…" : "Continue"}</button>
    </div>
  </form>
</Dialog>
