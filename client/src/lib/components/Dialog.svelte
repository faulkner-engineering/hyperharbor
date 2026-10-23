<script lang="ts">
  import type { Snippet } from "svelte";

  interface Props {
    title: string;
    /** Wider dialogs for forms with two columns. */
    wide?: boolean;
    /** Nearly the whole window, for editors and side-by-side lists. */
    xl?: boolean;
    /** Larger than xl, on the dark terminal palette of the machine view (the setup profile editor). */
    terminal?: boolean;
    children: Snippet;
  }

  let { title, wide = false, xl = false, terminal = false, children }: Props = $props();

  const titleId = $props.id();
</script>

<div class="backdrop" role="presentation">
  <div class="dialog" class:wide class:xl class:terminal role="dialog" aria-modal="true" aria-labelledby={titleId}>
    <h3 id={titleId}>{title}</h3>
    {@render children()}
  </div>
</div>

<style>
  .backdrop {
    position: fixed;
    inset: 0;
    display: grid;
    place-items: center;
    background: rgb(0 0 0 / 0.35);
    z-index: 10;
  }

  .dialog {
    width: min(480px, calc(100vw - 2rem));
    max-height: calc(100vh - 2rem);
    overflow-y: auto;
    padding: 1.25rem;
    border-radius: 8px;
    background: var(--surface);
    border: 1px solid var(--border);
    box-sizing: border-box;
  }

  .dialog.wide {
    width: min(620px, calc(100vw - 2rem));
  }

  .dialog.xl {
    width: min(1100px, calc(100vw - 2rem));
    height: min(820px, calc(100vh - 2rem));
    display: flex;
    flex-direction: column;
  }

  /* Every color below is a variable, so re-declaring them restyles the dialog and the pickers inside it. */
  .dialog.terminal {
    --bg: var(--hh-crt-screen);
    --surface: var(--hh-crt-panel);
    --text: var(--hh-fog);
    --muted: var(--hh-sleep);
    --border: var(--hh-crt-rule);
    --hover: #1a2028;
    --selected: #222a33;
    --danger: #ff7b72;
    --ok-bg: #12261e;
    --ok-fg: #56d364;
    --idle-bg: #1a2028;
    --warn-bg: #2e2410;
    --warn-fg: #e3b341;
    --busy-bg: #0c2d4b;
    --busy-fg: #79c0ff;
    --notice-bg: #0c2d4b;
    --error-bg: #3c1618;
    --accent-fg: var(--hh-crt-screen);
    width: min(1440px, calc(100vw - 2rem));
    height: min(900px, calc(100vh - 2rem));
    display: flex;
    flex-direction: column;
    background: var(--bg);
    color: var(--text);
  }

  .dialog.terminal h3 {
    font-family: ui-monospace, "Cascadia Mono", Consolas, monospace;
    font-size: 0.85rem;
    font-weight: 600;
    letter-spacing: 0.12em;
    text-transform: uppercase;
    color: var(--muted);
  }

  h3 {
    margin: 0 0 0.75rem;
    font-size: 1.05rem;
  }

  /* Shared form styling for the content of every dialog. */
  .dialog :global(form) {
    display: flex;
    flex-direction: column;
    gap: 0.4rem;
  }

  .dialog :global(.muted) {
    margin: 0 0 0.5rem;
    color: var(--muted);
    font-size: 0.9rem;
  }

  .dialog :global(label) {
    font-size: 0.85rem;
    color: var(--muted);
  }

  .dialog :global(input:not([type="checkbox"]):not([type="radio"])),
  .dialog :global(select),
  .dialog :global(textarea) {
    padding: 0.45rem 0.6rem;
    border: 1px solid var(--border);
    border-radius: 6px;
    background: var(--bg);
    color: inherit;
    font: inherit;
  }

  .dialog :global(.check) {
    display: flex;
    align-items: center;
    gap: 0.5rem;
    color: inherit;
    margin: 0.25rem 0;
  }

  .dialog :global(.error) {
    margin: 0.25rem 0;
    color: var(--danger);
  }

  .dialog :global(.warning) {
    margin: 0.25rem 0;
    padding: 0.5rem 0.75rem;
    border-radius: 6px;
    background: var(--warn-bg);
    color: var(--warn-fg);
  }

  .dialog :global(.actions) {
    display: flex;
    justify-content: flex-end;
    gap: 0.5rem;
    margin-top: 0.75rem;
  }

  .dialog :global(button) {
    padding: 0.45rem 1rem;
    border: 1px solid var(--border);
    border-radius: 6px;
    background: var(--surface);
    color: inherit;
    cursor: pointer;
    font: inherit;
  }

  .dialog :global(button.primary) {
    background: var(--accent);
    border-color: var(--accent);
    color: var(--accent-fg);
  }

  .dialog :global(button.danger) {
    background: var(--danger);
    border-color: var(--danger);
    color: #ffffff;
  }

  .dialog :global(button:disabled) {
    opacity: 0.5;
    cursor: default;
  }
</style>
