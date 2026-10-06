<script lang="ts">
  import Modal from "./Modal.svelte";

  interface Props {
    title: string;
    message: string;
    confirmLabel: string;
    danger?: boolean;
    onconfirm: () => void;
    oncancel: () => void;
  }

  let { title, message, confirmLabel, danger = false, onconfirm, oncancel }: Props = $props();
</script>

<Modal {title} onclose={oncancel}>
  <p>{message}</p>
  {#snippet actions()}
    <button type="button" class="btn" onclick={oncancel}>Cancel</button>
    <button type="button" class="btn {danger ? 'danger' : 'primary'}" onclick={onconfirm}>{confirmLabel}</button>
  {/snippet}
</Modal>
