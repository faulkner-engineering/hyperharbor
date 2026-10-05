<script lang="ts">
  import { onDestroy } from "svelte";

  interface Props {
    label: string;
    placeholder?: string;
    /** Called with the trimmed text once typing pauses, and at once on Enter. */
    onsearch: (text: string) => void;
    /** How long typing must pause before searching, in milliseconds. */
    delay?: number;
  }

  let { label, placeholder = "", onsearch, delay = 300 }: Props = $props();

  let text = $state("");
  let timer: ReturnType<typeof setTimeout> | undefined;

  function typed() {
    clearTimeout(timer);
    timer = setTimeout(() => onsearch(text.trim()), delay);
  }

  function now(event: KeyboardEvent) {
    if (event.key !== "Enter") return;
    event.preventDefault();
    clearTimeout(timer);
    onsearch(text.trim());
  }

  onDestroy(() => clearTimeout(timer));
</script>

<input type="search" aria-label={label} {placeholder} bind:value={text} oninput={typed} onkeydown={now} />

<style>
  input {
    width: 100%;
    box-sizing: border-box;
  }
</style>
