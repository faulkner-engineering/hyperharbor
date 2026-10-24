<script lang="ts">
  import { categoryLabel, describeValue, type Detail } from "$lib/catalogBrowser";

  interface Props {
    detail: Detail;
    onclose: () => void;
  }

  let { detail, onclose }: Props = $props();

  const on = $derived(detail.isOn());
</script>

<section class="detail" aria-label="Details: {detail.title}">
  <header>
    <div>
      <h4>{detail.title}</h4>
      <div class="meta">
        {#if detail.category}<span>{categoryLabel(detail.category)}</span>{/if}
        {#if detail.badge}<span class="badge {detail.badge}">{detail.badge}</span>{/if}
        {#if on}<span class="in">In this profile</span>{/if}
      </div>
    </div>
    <button type="button" class="close" aria-label="Close details" onclick={onclose}>×</button>
  </header>
  {#if detail.code}<code class="id">{detail.code}</code>{/if}
  {#if detail.description}<p>{detail.description}</p>{/if}
  {#if detail.note}<p class="note">{detail.note}</p>{/if}
  {#if detail.values && detail.values.length > 0}
    <div class="values">
      <div class="caption">Writes</div>
      <ul>
        {#each detail.values as value, index (index)}
          <li><code>{value.key}</code><span>{describeValue(value)}</span></li>
        {/each}
      </ul>
    </div>
  {/if}
  {#if detail.toggle}
    <button type="button" class:primary={!on} onclick={detail.toggle}>{on ? detail.onLabel : detail.offLabel}</button>
  {/if}
</section>

<style>
  .detail {
    position: absolute;
    top: 0.8rem;
    right: 0.8rem;
    z-index: 2;
    width: min(23rem, calc(100% - 1.6rem));
    max-height: calc(100% - 3.6rem);
    overflow-y: auto;
    box-sizing: border-box;
    padding: 0.8rem 0.9rem;
    border: 1px solid color-mix(in srgb, var(--accent) 40%, var(--border));
    border-radius: 8px;
    background: color-mix(in srgb, var(--hh-crt-panel) 88%, transparent);
    backdrop-filter: blur(4px);
    box-shadow: 0 0 18px var(--glow);
    display: flex;
    flex-direction: column;
    gap: 0.5rem;
  }

  header {
    display: flex;
    justify-content: space-between;
    gap: 0.5rem;
  }

  h4 {
    margin: 0;
    font-size: 1rem;
    color: var(--accent);
    text-shadow: 0 0 8px var(--glow);
  }

  .meta {
    display: flex;
    flex-wrap: wrap;
    gap: 0.4rem;
    margin-top: 0.15rem;
    font-size: 0.78rem;
    color: var(--muted);
  }

  .in {
    color: var(--accent);
  }

  .close {
    align-self: flex-start;
    padding: 0 0.45rem !important;
    border: none !important;
    background: transparent !important;
    color: var(--muted);
    font-size: 1.1rem;
  }

  p {
    margin: 0;
    font-size: 0.88rem;
  }

  .note {
    color: var(--muted);
  }

  .id,
  .values code {
    font-size: 0.75rem;
    color: var(--muted);
    word-break: break-all;
  }

  .caption {
    font-family: ui-monospace, "Cascadia Mono", Consolas, monospace;
    font-size: 0.7rem;
    letter-spacing: 0.1em;
    text-transform: uppercase;
    color: var(--muted);
    margin-bottom: 0.25rem;
  }

  .values ul {
    list-style: none;
    margin: 0;
    padding: 0;
    display: flex;
    flex-direction: column;
    gap: 0.35rem;
  }

  .values li {
    display: flex;
    flex-direction: column;
    font-size: 0.82rem;
  }

  .badge {
    text-transform: uppercase;
    letter-spacing: 0.06em;
    font-size: 0.68rem;
    padding: 0 0.35rem;
    border-radius: 4px;
    background: var(--idle-bg);
  }

  .badge.safe {
    background: var(--ok-bg);
    color: var(--ok-fg);
  }

  .badge.caution {
    background: var(--warn-bg);
    color: var(--warn-fg);
  }
</style>
