<script lang="ts" generics="T">
  interface Option {
    value: T;
    label: string;
  }

  interface Props {
    /** Names the choice, for screen readers and as its caption. */
    label: string;
    options: Option[];
    value: T;
    /** One line under the choice. */
    hint?: string;
    /** Called instead of setting value, when choosing needs more than that. */
    onchange?: (value: T) => void;
  }

  let { label, options, value = $bindable(), hint, onchange }: Props = $props();

  function choose(next: T) {
    if (onchange) onchange(next);
    else value = next;
  }
</script>

<div class="choice">
  <span class="caption">{label}</span>
  <div class="options" role="radiogroup" aria-label={label}>
    {#each options as option (option.label)}
      <button type="button" role="radio" aria-checked={(value ?? null) === option.value} class:active={(value ?? null) === option.value} onclick={() => choose(option.value)}>
        {option.label}
      </button>
    {/each}
  </div>
  {#if hint}<small>{hint}</small>{/if}
</div>

<style>
  .choice {
    display: flex;
    flex-direction: column;
    gap: 0.25rem;
    margin-bottom: 0.7rem;
  }

  .caption {
    font-size: 0.85rem;
    color: var(--muted);
  }

  .options {
    display: flex;
    flex-wrap: wrap;
    gap: 0.3rem;
  }

  .options button {
    padding: 0.2rem 0.7rem !important;
    font-size: 0.85rem;
  }

  .options button.active {
    border-color: var(--accent) !important;
    background: color-mix(in srgb, var(--accent) 14%, transparent);
    color: var(--accent);
  }

  small {
    color: var(--muted);
    font-size: 0.78rem;
  }
</style>
