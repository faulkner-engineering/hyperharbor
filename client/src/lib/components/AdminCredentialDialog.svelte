<script lang="ts">
  import { onDestroy, onMount } from "svelte";
  import {
    checkVmAdminCredential,
    errorMessage,
    getVmAdminCredential,
    setVmAdminCredential,
    type HostEntry,
    type Vm,
    type VmAdminCredential,
  } from "#lib/api/client.ts";
  import {
    accountOrigin,
    adminCredentialError,
    canStopExpiry,
    expiryLine,
    RECOVERY_GUIDE,
    SLOW_AFTER_MS,
    stateLine,
  } from "#lib/adminCredential.ts";
  import { ElevationCancelled, withElevation } from "#lib/lifecycle.svelte.ts";
  import Dialog from "@hyperharbor/ui/components/Dialog.svelte";

  interface Props {
    host: HostEntry;
    vm: Vm;
    /** `changed` is true when the host may hold a different state than before, so the list is read again. */
    onclose: (changed: boolean) => void;
  }

  let { host, vm, onclose }: Props = $props();

  let credential = $state<VmAdminCredential | null>(null);
  let loadError = $state<string | null>(null);
  /** What is waiting on the host: nothing, a check of the stored credential, or a new one being saved. */
  let busy = $state<null | "check" | "expiry" | "save">(null);
  let slow = $state(false);
  let error = $state<string | null>(null);
  let done = $state<string | null>(null);
  let editing = $state(false);
  let userName = $state("");
  let password = $state("");
  let passwordNeverExpires = $state(false);
  let changed = false;
  /** Set when the dialog closes, so a late answer does nothing. */
  let dismissed = false;
  let slowTimer: ReturnType<typeof setTimeout> | undefined;

  const running = $derived(vm.state === "running");
  const windows = $derived(vm.guestOs.family !== "linux");
  const canSave = $derived(running && busy === null && userName.trim() !== "" && password !== "");
  /** The form names the account HyperHarbor made: the host keeps that one from expiring anyway, so there is no choice to offer. */
  const keptByHost = $derived(
    credential !== null && credential.managed && credential.userName !== null && userName.trim().toLowerCase() === credential.userName.toLowerCase(),
  );

  onMount(async () => {
    try {
      const read = await getVmAdminCredential(host.key, vm.id);
      if (dismissed) return;
      show(read);
    } catch (e) {
      if (!dismissed) loadError = errorMessage(e);
    }
  });

  onDestroy(() => {
    dismissed = true;
    password = "";
    clearTimeout(slowTimer);
  });

  function show(next: VmAdminCredential) {
    credential = next;
    if (userName === "") userName = next.userName ?? "";
    // A credential the VM refuses needs replacing, so the form is there without another click.
    if (next.state === "rejected") editing = true;
  }

  /** Runs one request with the busy state and its deadline. Leaving never waits on it (docs/ui-principles.md). */
  async function run(kind: "check" | "expiry" | "save", request: () => Promise<VmAdminCredential>, success: (result: VmAdminCredential) => string | null) {
    busy = kind;
    slow = false;
    error = null;
    done = null;
    slowTimer = setTimeout(() => (slow = true), SLOW_AFTER_MS);
    try {
      const result = await withElevation(host.key, request);
      changed = true;
      if (dismissed) return;
      show(result);
      done = success(result);
    } catch (e) {
      if (dismissed || e instanceof ElevationCancelled) return;
      // A refused request can still have changed what the host recorded (a stored credential whose expiry stayed on).
      changed = true;
      error = adminCredentialError(e, vm.name);
      getVmAdminCredential(host.key, vm.id)
        .then((read) => !dismissed && show(read))
        .catch(() => {});
    } finally {
      clearTimeout(slowTimer);
      slow = false;
      busy = null;
    }
  }

  const check = () =>
    run(
      "check",
      () => checkVmAdminCredential(host.key, vm.id, false),
      (result) => (result.state === "working" ? `${vm.name} accepted the stored account.` : null),
    );

  const stopExpiry = () =>
    run(
      "expiry",
      () => checkVmAdminCredential(host.key, vm.id, true),
      (result) => (result.state === "working" && !result.passwordExpiresAt ? `The password of ${result.userName} no longer expires.` : null),
    );

  function save(event: SubmitEvent) {
    event.preventDefault();
    if (!canSave) return;
    const typed = password;
    run(
      "save",
      () => setVmAdminCredential(host.key, vm.id, userName.trim(), typed, windows && passwordNeverExpires),
      (result) => {
        password = "";
        editing = false;
        return `HyperHarbor now signs in to ${vm.name} as ${result.userName}.`;
      },
    );
  }

  function close() {
    dismissed = true;
    password = "";
    onclose(changed);
  }
</script>

<Dialog title="Administrator account of {vm.name}" wide>
  {#if credential === null && loadError}
    <p class="error" role="alert">{loadError}</p>
  {:else if credential === null}
    <p class="muted">Asking {host.displayName}…</p>
  {:else if credential.userName === null}
    <p>
      HyperHarbor has no administrator account stored for {vm.name}. Choose Set up… for the VM to give it one; it uses the
      account to manage your sign-in.
    </p>
  {:else}
    {@const expiry = expiryLine(credential, vm)}
    <p class="muted">
      HyperHarbor signs in to {vm.name} with this account to change your password on every connect, open administrator
      access, and apply profiles. It is stored encrypted on {host.displayName}.
    </p>
    <dl>
      <dt>Account</dt>
      <dd class="mono">{credential.userName}</dd>
    </dl>
    <p class="muted">{accountOrigin(credential)}</p>

    <p class={credential.state === "rejected" ? "warning" : ""} role="status">
      {stateLine(credential, vm.name)}
      {#if credential.state === "rejected" && credential.detail}<span class="detail">{credential.detail}</span>{/if}
    </p>
    {#if expiry}<p>{expiry}</p>{/if}

    {#if credential.state === "rejected"}
      <p>
        Its password has expired or was changed in the VM. Connect still works with your last password, but HyperHarbor
        cannot change it, open administrator access, or apply profiles until it has a working password for this account
        or another administrator.
      </p>
      {#if credential.managed}
        <p class="muted">
          When the password expired, Windows asks for a new one at the VM's sign-in screen, which needs the old one from the
          host. The steps are at <span class="mono guide">{RECOVERY_GUIDE}</span>. Enter the new password below afterwards.
        </p>
      {/if}
    {/if}

    {#if !running}
      <p class="muted">Start {vm.name} to check the account or change what HyperHarbor has stored.</p>
    {/if}

    {#if editing}
      <form onsubmit={save}>
        <label for="ac-user">{windows ? "Administrator user name" : "User name (with sudo)"}</label>
        <input id="ac-user" bind:value={userName} autocomplete="off" spellcheck="false" disabled={busy !== null} />
        <label for="ac-password">Password</label>
        <input id="ac-password" type="password" bind:value={password} autocomplete="off" disabled={busy !== null} />
        {#if windows && !keptByHost}
          <label class="check">
            <input type="checkbox" bind:checked={passwordNeverExpires} disabled={busy !== null} />
            Stop this account's password from expiring
          </label>
        {/if}
        <p class="muted">
          HyperHarbor signs in to {vm.name} with it first and stores it only when that works. Your own account in the VM
          is not changed.
        </p>
        <div class="row">
          <button type="submit" class="primary" disabled={!canSave}>{busy === "save" ? "Signing in…" : "Save"}</button>
        </div>
      </form>
    {/if}
  {/if}

  {#if busy !== null && slow}
    <p class="muted" role="status">
      This is taking longer than usual; {vm.name} may be slow to answer. You can close this, and the host finishes on its
      own.
    </p>
  {/if}
  {#if done}<p role="status">{done}</p>{/if}
  {#if error}<p class="error" role="alert">{error}</p>{/if}

  <div class="actions wrap">
    {#if credential?.userName && !editing}
      <button type="button" disabled={busy !== null} onclick={() => (editing = true)}>Change…</button>
    {/if}
    {#if credential?.userName && canStopExpiry(credential, vm)}
      <button type="button" disabled={!running || busy !== null} onclick={stopExpiry}
        >{busy === "expiry" ? "Turning off…" : "Stop the password from expiring"}</button
      >
    {/if}
    {#if credential?.userName}
      <button type="button" disabled={!running || busy !== null} onclick={check}>{busy === "check" ? "Checking…" : "Check now"}</button>
    {/if}
    <button type="button" class={editing ? "" : "primary"} onclick={close}>Close</button>
  </div>
</Dialog>

<style>
  dl {
    display: grid;
    grid-template-columns: auto 1fr;
    gap: 0.5rem 1rem;
    align-items: baseline;
    margin: 0.75rem 0 0.25rem;
  }

  dt {
    color: var(--muted);
    font-size: 0.85rem;
  }

  dd {
    margin: 0;
  }

  .mono {
    font-family: ui-monospace, "Cascadia Mono", Consolas, monospace;
  }

  .guide {
    font-size: 0.8rem;
    overflow-wrap: anywhere;
    user-select: text;
  }

  .detail {
    display: block;
    margin-top: 0.25rem;
    font-size: 0.85rem;
    overflow-wrap: anywhere;
  }

  .row {
    display: flex;
    justify-content: flex-end;
  }

  /* Up to four buttons, one of them long: they wrap rather than leave the dialog. */
  .wrap {
    flex-wrap: wrap;
  }
</style>
