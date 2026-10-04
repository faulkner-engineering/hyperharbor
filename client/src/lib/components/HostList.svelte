<script lang="ts">
  import {
    addManualHost,
    errorMessage,
    removeManualHost,
    type HostEntry,
  } from "$lib/api/client";

  interface Props {
    hosts: HostEntry[];
    selectedKey: string | null;
    onselect: (key: string) => void;
  }

  let { hosts, selectedKey, onselect }: Props = $props();

  let address = $state("");
  let adding = $state(false);
  let addError = $state<string | null>(null);

  async function add(event: SubmitEvent) {
    event.preventDefault();
    adding = true;
    addError = null;
    try {
      const entry = await addManualHost(address);
      address = "";
      onselect(entry.key);
    } catch (error) {
      addError = errorMessage(error);
    } finally {
      adding = false;
    }
  }

  function statusLabel(host: HostEntry): string {
    if (host.source === "remembered") return "Paired · Not on network";
    if (host.paired) return "Paired";
    return host.source === "discovered" ? "Discovered" : "Added";
  }

  async function remove(key: string) {
    try {
      await removeManualHost(key);
    } catch (error) {
      addError = errorMessage(error);
    }
  }
</script>

<section class="hosts" aria-label="Hosts">
  <h2>Hosts</h2>

  {#if hosts.length === 0}
    <p class="empty">Searching the local network…</p>
  {:else}
    <ul>
      {#each hosts as host (host.key)}
        <li class:selected={host.key === selectedKey}>
          <button class="host" type="button" onclick={() => onselect(host.key)}>
            <span class="name">{host.displayName}</span>
            <span class="meta">
              {#if host.isLocal}This PC{:else}{host.addresses[0] ?? ""}{/if}
              · {statusLabel(host)}
            </span>
          </button>
          {#if host.source === "manual"}
            <button
              class="remove"
              type="button"
              aria-label={`Remove ${host.displayName}`}
              onclick={() => remove(host.key)}>×</button
            >
          {/if}
        </li>
      {/each}
    </ul>
  {/if}

  <form onsubmit={add}>
    <label for="host-address">Add host by name or IP</label>
    <div class="row">
      <input
        id="host-address"
        bind:value={address}
        placeholder="192.168.1.10 or pc.lan:48443"
        autocomplete="off"
        spellcheck="false"
      />
      <button type="submit" disabled={adding || address.trim() === ""}>+ Add</button>
    </div>
    {#if addError}<p class="error" role="alert">{addError}</p>{/if}
  </form>
</section>

<style>
  .hosts {
    display: flex;
    flex-direction: column;
    gap: 0.75rem;
  }

  h2 {
    margin: 0;
    font-size: 0.8rem;
    font-weight: 600;
    text-transform: uppercase;
    letter-spacing: 0.05em;
    color: var(--muted);
  }

  ul {
    list-style: none;
    margin: 0;
    padding: 0;
    display: flex;
    flex-direction: column;
    gap: 0.25rem;
  }

  li {
    display: flex;
    align-items: center;
    border-radius: 6px;
  }

  li.selected {
    background: var(--selected);
  }

  .host {
    flex: 1;
    display: flex;
    flex-direction: column;
    align-items: flex-start;
    gap: 0.1rem;
    padding: 0.5rem 0.6rem;
    border: 0;
    background: transparent;
    color: inherit;
    text-align: left;
    cursor: pointer;
    border-radius: 6px;
  }

  .host:hover {
    background: var(--hover);
  }

  .name {
    font-weight: 600;
  }

  .meta {
    font-size: 0.8rem;
    color: var(--muted);
  }

  .remove {
    border: 0;
    background: transparent;
    color: var(--muted);
    font-size: 1.1rem;
    padding: 0.25rem 0.6rem;
    cursor: pointer;
  }

  .empty {
    margin: 0;
    color: var(--muted);
  }

  form {
    display: flex;
    flex-direction: column;
    gap: 0.35rem;
    margin-top: auto;
  }

  label {
    font-size: 0.8rem;
    color: var(--muted);
  }

  .row {
    display: flex;
    gap: 0.4rem;
  }

  input {
    flex: 1;
    min-width: 0;
    padding: 0.4rem 0.5rem;
    border: 1px solid var(--border);
    border-radius: 6px;
    background: var(--surface);
    color: inherit;
  }

  form button {
    padding: 0.4rem 0.8rem;
    border: 1px solid var(--border);
    border-radius: 6px;
    background: var(--surface);
    color: inherit;
    cursor: pointer;
  }

  form button:disabled {
    opacity: 0.5;
    cursor: default;
  }

  .error {
    margin: 0;
    font-size: 0.8rem;
    color: var(--danger);
  }
</style>
