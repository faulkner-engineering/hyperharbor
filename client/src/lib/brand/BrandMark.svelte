<script lang="ts">
  // The HyperHarbor mark (hyperharbor-brand/svg/mark-mono.svg). It draws in currentColor.
  let {
    size = 24,
    label = "HyperHarbor",
    decorative = false,
  }: { size?: number | string; label?: string; decorative?: boolean } = $props();
</script>

<svg
  xmlns="http://www.w3.org/2000/svg"
  viewBox="0 0 100 100"
  width={size}
  height={size}
  role={decorative ? undefined : "img"}
  aria-label={decorative ? undefined : label}
  aria-hidden={decorative ? "true" : undefined}
>
  <path
    d="M28 16 V84 M72 16 V84 M28 50 H72"
    fill="none"
    stroke="currentColor"
    stroke-width="12"
    stroke-linecap="round"
    stroke-linejoin="round"
  />
  <rect x="42" y="23" width="16" height="12" rx="3.5" fill="currentColor" />
  <rect x="42" y="65" width="16" height="12" rx="3.5" fill="none" stroke="currentColor" stroke-width="3" />
</svg>
