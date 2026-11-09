<script lang="ts">
  import type { Snippet } from "svelte";
  import "../app.css";
  // The shared components (@hyperharbor/ui) reach the host through this implementation.
  import "#lib/api/hostApi.ts";

  let { children }: { children: Snippet } = $props();
</script>

{@render children()}
