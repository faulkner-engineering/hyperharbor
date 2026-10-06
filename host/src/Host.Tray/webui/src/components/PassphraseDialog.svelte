<script lang="ts">
  import { tray } from "../store.svelte";
  import { MAX_PASSPHRASE, passphraseProblem } from "../text";
  import Modal from "./Modal.svelte";

  interface Props {
    replacing: boolean;
    onclose: () => void;
  }

  let { replacing, onclose }: Props = $props();

  let passphrase = $state("");
  let confirm = $state("");
  let attempted = $state(false);

  const problem = $derived(passphraseProblem(passphrase, confirm));
  const saving = $derived(tray.isBusy("passphrase"));

  // The tray marks the save busy while it hashes and sends, then clears it when the service confirms (with a toast).
  // The dialog closes only after seeing both; a passphrase the tray refuses never turns busy, so the dialog stays.
  let confirmed = $state(false);
  $effect(() => {
    if (tray.state?.busy.includes("passphrase")) confirmed = true;
    else if (confirmed) onclose();
  });

  function save(event: SubmitEvent) {
    event.preventDefault();
    attempted = true;
    if (problem) return;
    tray.send({ type: "setPassphrase", passphrase }, "passphrase");
    // The plaintext stays only in the tray from here on.
    attempted = false;
    passphrase = "";
    confirm = "";
  }
</script>

<Modal title={replacing ? "Change the admin passphrase" : "Set an admin passphrase"} {onclose}>
  <p class="muted">
    Paired devices enter it before they create, change, or delete VMs.{replacing ? " Changing it ends every device's current elevation." : ""}
  </p>
  <form id="passphrase-form" onsubmit={save}>
    <label>
      Passphrase
      <input type="password" bind:value={passphrase} maxlength={MAX_PASSPHRASE} autocomplete="off" disabled={saving} />
    </label>
    <label>
      Confirm passphrase
      <input type="password" bind:value={confirm} maxlength={MAX_PASSPHRASE} autocomplete="off" disabled={saving} />
    </label>
    <p class="problem" role={attempted && problem ? "alert" : undefined}>{attempted && problem ? problem : ""}</p>
  </form>
  {#snippet actions()}
    <button type="button" class="btn" onclick={onclose} disabled={saving}>Cancel</button>
    <button type="submit" form="passphrase-form" class="btn primary" disabled={saving}>
      {#if saving}<span class="spinner"></span> Saving…{:else}Save{/if}
    </button>
  {/snippet}
</Modal>

<style>
  form {
    display: grid;
    gap: 0.75rem;
  }

  label {
    display: grid;
    gap: 0.3rem;
    font-weight: 600;
    font-size: 0.9rem;
  }

  .problem {
    min-height: 1.3em;
    color: var(--danger);
    font-size: 0.88rem;
  }
</style>
