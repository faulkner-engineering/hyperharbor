<script lang="ts">
  import Dialog from "./Dialog.svelte";

  interface Props {
    title: string;
    message: string;
    confirmLabel: string;
    /** Styles the confirm button as destructive. */
    danger?: boolean;
    onclose: (confirmed: boolean) => void;
  }

  let { title, message, confirmLabel, danger = false, onclose }: Props = $props();
</script>

<Dialog {title}>
  <p class="muted">{message}</p>
  <div class="actions">
    <button type="button" onclick={() => onclose(false)}>Cancel</button>
    <button type="button" class={danger ? "danger" : "primary"} onclick={() => onclose(true)}>{confirmLabel}</button>
  </div>
</Dialog>
