<script lang="ts">
  interface Tab {
    id: string;
    label: string;
  }

  interface Props {
    tabs: Tab[];
    /** The id of the selected tab. */
    selected: string;
    /** What the tabs switch between, for screen readers. */
    label: string;
  }

  let { tabs, selected = $bindable(), label }: Props = $props();
</script>

<div class="tabs" role="tablist" aria-label={label}>
  {#each tabs as tab (tab.id)}
    <button
      type="button"
      role="tab"
      aria-selected={selected === tab.id}
      class:active={selected === tab.id}
      onclick={() => (selected = tab.id)}>{tab.label}</button
    >
  {/each}
</div>

<style>
  .tabs {
    display: flex;
    gap: 0.25rem;
    margin-bottom: 0.75rem;
    border-bottom: 1px solid var(--border);
  }

  .tabs button {
    padding: 0.4rem 0.8rem;
    border: none;
    border-bottom: 2px solid transparent;
    border-radius: 0;
    background: none;
    color: var(--muted);
    cursor: pointer;
    font: inherit;
  }

  .tabs button.active {
    border-bottom-color: var(--accent);
    color: var(--text);
    font-weight: 600;
  }
</style>
