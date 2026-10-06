<script lang="ts">
  import type { StepState } from "../text";
  import Icon from "./Icon.svelte";

  interface Props {
    steps: { label: string; state: StepState }[];
  }

  let { steps }: Props = $props();
</script>

<ol class="steps" aria-label="Update progress">
  {#each steps as step, index (step.label)}
    <li class={step.state} aria-current={step.state === "active" ? "step" : undefined}>
      <span class="dot">
        {#if step.state === "done"}<Icon name="check" size={14} />{:else if step.state === "active"}<span class="spinner"></span>{:else}{index + 1}{/if}
      </span>
      <span class="label">{step.label}</span>
    </li>
  {/each}
</ol>

<style>
  .steps {
    display: grid;
    grid-template-columns: repeat(4, 1fr);
    margin: 0;
    padding: 0;
    list-style: none;
  }

  li {
    position: relative;
    display: grid;
    justify-items: center;
    gap: 0.35rem;
    color: var(--muted);
    font-size: 0.82rem;
    text-align: center;
  }

  /* The line to the next step. */
  li:not(:last-child)::after {
    content: "";
    position: absolute;
    top: 13px;
    left: calc(50% + 18px);
    right: calc(-50% + 18px);
    height: 2px;
    background: var(--border);
    transition: background 300ms var(--ease);
  }

  li.done:not(:last-child)::after {
    background: var(--ok-fg);
  }

  .dot {
    display: grid;
    place-items: center;
    width: 28px;
    height: 28px;
    border: 2px solid var(--border);
    border-radius: 50%;
    background: var(--surface);
    font-size: 0.78rem;
    font-weight: 600;
    transition:
      background 300ms var(--ease),
      border-color 300ms var(--ease),
      color 300ms var(--ease);
  }

  li.done .dot {
    border-color: var(--ok-fg);
    background: var(--ok-bg);
    color: var(--ok-fg);
  }

  li.active {
    color: var(--text);
    font-weight: 600;
  }

  li.active .dot {
    border-color: var(--busy-fg);
    color: var(--busy-fg);
  }
</style>
