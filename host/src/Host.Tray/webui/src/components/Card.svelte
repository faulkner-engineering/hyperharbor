<script lang="ts">
  import type { Snippet } from "svelte";
  import Icon, { type IconName } from "./Icon.svelte";

  export type Tone = "ok" | "warn" | "busy" | "neutral";

  interface Props {
    icon: IconName;
    title: string;
    /** One line on how it stands; its color follows the tone. */
    status: string;
    tone?: Tone;
    /** Detail under the status. */
    children?: Snippet;
    /** The card's button, on the right. */
    action?: Snippet;
  }

  let { icon, title, status, tone = "neutral", children, action }: Props = $props();
</script>

<section class="card">
  <div class="icon {tone}"><Icon name={icon} size={20} /></div>
  <div class="body">
    <h3>{title}</h3>
    <p class="status {tone}">
      {#if tone === "busy"}<span class="spinner"></span>{/if}
      <span>{status}</span>
    </p>
    {#if children}<div class="detail">{@render children()}</div>{/if}
  </div>
  {#if action}<div class="action">{@render action()}</div>{/if}
</section>

<style>
  .card {
    display: grid;
    grid-template-columns: auto 1fr auto;
    gap: 0.9rem;
    align-items: start;
    padding: 1rem 1.1rem;
    border: 1px solid var(--border);
    border-radius: var(--radius);
    background: var(--surface);
    box-shadow: var(--shadow);
  }

  .icon {
    display: grid;
    place-items: center;
    width: 38px;
    height: 38px;
    border-radius: 9px;
    background: var(--hover);
    transition:
      background 250ms var(--ease),
      color 250ms var(--ease);
  }

  .icon.ok {
    background: var(--ok-bg);
    color: var(--ok-fg);
  }

  .icon.warn {
    background: var(--warn-bg);
    color: var(--warn-fg);
  }

  .icon.busy {
    background: var(--busy-bg);
    color: var(--busy-fg);
  }

  h3 {
    font-size: 0.95rem;
    font-weight: 600;
  }

  .status {
    display: flex;
    align-items: center;
    gap: 0.45rem;
    min-height: 1.45em;
    margin-top: 0.15rem;
    transition: color 250ms var(--ease);
  }

  .status.warn {
    color: var(--warn-fg);
  }

  .status.busy {
    color: var(--busy-fg);
  }

  .detail {
    margin-top: 0.3rem;
    color: var(--muted);
    font-size: 0.9rem;
  }

  .action {
    align-self: center;
  }
</style>
