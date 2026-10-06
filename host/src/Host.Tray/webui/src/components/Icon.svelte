<script lang="ts" module>
  // 24-unit line icons drawn with the current text color.
  const paths = {
    overview: "M3 11l9-7 9 7v9a1 1 0 0 1-1 1h-5v-6H9v6H4a1 1 0 0 1-1-1z",
    devices: "M4 5h16v10H4z M2 19h20 M9 15v4 M15 15v4",
    storage: "M3 7a2 2 0 0 1 2-2h4l2 2h8a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z",
    updates: "M20 12a8 8 0 1 1-2.34-5.66 M20 4v4h-4",
    logs: "M7 3h7l5 5v13H7z M14 3v5h5 M10 13h6 M10 17h6",
    check: "M5 12l5 5 9-10",
    close: "M6 6l12 12 M18 6L6 18",
    warning: "M12 4l9 16H3z M12 10v4 M12 17v.5",
    info: "M12 8v.5 M12 11v6 M12 3a9 9 0 1 0 0 18 9 9 0 0 0 0-18z",
    lock: "M6 11h12v9H6z M8 11V8a4 4 0 0 1 8 0v3",
    console: "M3 5h18v14H3z M7 10l3 2-3 2 M12 15h5",
    search: "M11 4a7 7 0 1 0 0 14 7 7 0 0 0 0-14z M20 20l-4-4",
    folder: "M3 7a2 2 0 0 1 2-2h4l2 2h8a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z",
    external: "M14 4h6v6 M20 4l-9 9 M18 14v5H5V6h5",
  } as const;

  export type IconName = keyof typeof paths;
</script>

<script lang="ts">
  interface Props {
    name: IconName;
    size?: number;
  }

  let { name, size = 18 }: Props = $props();
</script>

<svg
  width={size}
  height={size}
  viewBox="0 0 24 24"
  fill="none"
  stroke="currentColor"
  stroke-width="1.8"
  stroke-linecap="round"
  stroke-linejoin="round"
  aria-hidden="true"
>
  <path d={paths[name]} />
</svg>
