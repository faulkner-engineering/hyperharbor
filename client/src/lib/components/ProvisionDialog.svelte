<script lang="ts">
  import { untrack } from "svelte";
  import { errorMessage, provisionVm, type HostEntry, type Vm } from "$lib/api/client";

  interface Props {
    host: HostEntry;
    vm: Vm;
    onclose: (provisioned: boolean) => void;
  }

  let { host, vm, onclose }: Props = $props();

  const linux = $derived(vm.guestOs.family === "linux");

  // Prefilled once when the dialog opens; Linux has no common default administrator name.
  let adminUserName = $state(untrack(() => (vm.guestOs.family === "linux" ? "" : "Administrator")));
  let adminPassword = $state("");
  let enableRemoteDesktop = $state(true);
  let installDesktop = $state(false);
  let busy = $state(false);
  let error = $state<string | null>(null);

  const canSubmit = $derived(adminUserName.trim() !== "" && adminPassword !== "" && !busy);

  async function submit(event: SubmitEvent) {
    event.preventDefault();
    busy = true;
    error = null;
    try {
      await provisionVm(host.key, vm.id, adminUserName.trim(), adminPassword, {
        enableRemoteDesktop,
        installDesktop: linux && enableRemoteDesktop && installDesktop,
      });
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
      {#if linux}
        <p class="muted">
          HyperHarbor signs in to {vm.guestOs.name ?? "this Linux VM"} over SSH to create a local account
          for one-click sign-in, and changes its password on every connect. Enter an account that can sign
          in over SSH with a password and use sudo. It is stored encrypted on {host.displayName} and used
          only to manage that account.
        </p>
      {:else}
        <p class="muted">
          HyperHarbor creates a local account in the VM for one-click sign-in and changes its password
          on every connect. Enter an administrator account of the VM to do this once. It is stored
          encrypted on {host.displayName} and used only to manage that account.
        </p>
      {/if}

      <label for="admin-user">{linux ? "User name (with sudo)" : "Administrator user name"}</label>
      <input id="admin-user" bind:value={adminUserName} autocomplete="off" spellcheck="false" disabled={busy} />

      <label for="admin-password">Password</label>
      <input id="admin-password" type="password" bind:value={adminPassword} autocomplete="off" disabled={busy} />

      <label class="check">
        <input type="checkbox" bind:checked={enableRemoteDesktop} disabled={busy} />
        {linux ? "Install and turn on Remote Desktop (xrdp)" : "Turn on Remote Desktop in the VM"}
      </label>

      {#if linux}
        <label class="check">
          <input type="checkbox" bind:checked={installDesktop} disabled={busy || !enableRemoteDesktop} />
          Install a lightweight desktop (Xfce) if the VM has none
        </label>
      {/if}

      {#if busy && linux}
        <p class="muted" role="status">Installing packages can take several minutes. Keep this window open.</p>
      {/if}

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
