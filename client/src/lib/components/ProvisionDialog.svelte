<script lang="ts">
  import { errorMessage, provisionVm, type HostEntry, type Vm } from "$lib/api/client";

  interface Props {
    host: HostEntry;
    vm: Vm;
    onclose: (provisioned: boolean) => void;
  }

  let { host, vm, onclose }: Props = $props();

  let adminUserName = $state("Administrator");
  let adminPassword = $state("");
  let enableRemoteDesktop = $state(true);
  let busy = $state(false);
  let error = $state<string | null>(null);

  const canSubmit = $derived(adminUserName.trim() !== "" && adminPassword !== "" && !busy);

  async function submit(event: SubmitEvent) {
    event.preventDefault();
    busy = true;
    error = null;
    try {
      await provisionVm(host.key, vm.id, adminUserName.trim(), adminPassword, enableRemoteDesktop);
      adminPassword = "";
      onclose(true);
    } catch (e) {
      error = errorMessage(e);
    } finally {
      busy = false;
    }
  }

  function cancel() {
    adminPassword = "";
    onclose(false);
  }
</script>

<div class="backdrop" role="presentation">
  <div class="dialog" role="dialog" aria-modal="true" aria-labelledby="provision-title">
    <form onsubmit={submit}>
      <h3 id="provision-title">Set up Remote Desktop for {vm.name}</h3>
      <p class="muted">
        HyperHarbor creates a local account in the VM for one-click sign-in and changes its password
        on every connect. Enter an administrator account of the VM to do this once. It is stored
        encrypted on {host.displayName} and used only to manage that account.
      </p>
  
      <label for="admin-user">Administrator user name</label>
      <input id="admin-user" bind:value={adminUserName} autocomplete="off" spellcheck="false" disabled={busy} />
  
      <label for="admin-password">Password</label>
      <input id="admin-password" type="password" bind:value={adminPassword} autocomplete="off" disabled={busy} />
  
      <label class="check">
        <input type="checkbox" bind:checked={enableRemoteDesktop} disabled={busy} />
        Turn on Remote Desktop in the VM
      </label>
  
      {#if error}<p class="error" role="alert">{error}</p>{/if}
  
      <div class="actions">
        <button type="button" onclick={cancel} disabled={busy}>Cancel</button>
        <button type="submit" class="primary" disabled={!canSubmit}>{busy ? "Setting up…" : "Set up"}</button>
      </div>
    </form>
  </div>
</div>

<style>
  .backdrop {
    position: fixed;
    inset: 0;
    display: grid;
    place-items: center;
    background: rgb(0 0 0 / 0.35);
    z-index: 10;
  }

  .dialog {
    width: min(460px, calc(100vw - 2rem));
    padding: 1.25rem;
    border-radius: 8px;
    background: var(--surface);
    border: 1px solid var(--border);
  }

  form {
    display: flex;
    flex-direction: column;
    gap: 0.4rem;
  }

  h3 {
    margin: 0 0 0.25rem;
    font-size: 1.05rem;
  }

  .muted {
    margin: 0 0 0.5rem;
    color: var(--muted);
    font-size: 0.9rem;
  }

  label {
    font-size: 0.85rem;
    color: var(--muted);
  }

  input:not([type="checkbox"]) {
    padding: 0.45rem 0.6rem;
    border: 1px solid var(--border);
    border-radius: 6px;
    background: var(--bg);
    color: inherit;
    margin-bottom: 0.4rem;
  }

  .check {
    display: flex;
    align-items: center;
    gap: 0.5rem;
    color: inherit;
    margin: 0.25rem 0;
  }

  .error {
    margin: 0.25rem 0;
    color: var(--danger);
  }

  .actions {
    display: flex;
    justify-content: flex-end;
    gap: 0.5rem;
    margin-top: 0.5rem;
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
</style>
