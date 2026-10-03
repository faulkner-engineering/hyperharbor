<script lang="ts">
  import type { VmJob } from "$lib/api/client";

  interface Props {
    job: VmJob;
  }

  let { job }: Props = $props();
</script>

<div class="job" role="status" aria-live="polite">
  {#if job.state === "failed" && job.error}
    <p class="error"><strong>{job.error.title}.</strong> {job.error.detail}</p>
  {:else}
    <p class="step">{job.state === "succeeded" ? "Done." : `${job.step}…`}</p>
    <progress max="100" value={job.percentComplete} aria-label="Progress">{job.percentComplete}%</progress>
  {/if}
</div>

<style>
  .job {
    margin: 0.5rem 0;
  }

  .step {
    margin: 0 0 0.35rem;
  }

  .error {
    margin: 0;
    color: var(--danger);
  }

  progress {
    width: 100%;
    height: 0.6rem;
    accent-color: var(--accent);
  }
</style>
