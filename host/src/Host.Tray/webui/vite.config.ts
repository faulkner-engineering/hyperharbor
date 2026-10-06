import { defineConfig } from "vite";
import { svelte } from "@sveltejs/vite-plugin-svelte";

// The build is embedded in HyperHarbor.Host.exe and served from https://tray.hyperharbor.invalid/ by the tray, so
// paths are relative and nothing is inlined that the page's content security policy would refuse.
export default defineConfig({
  plugins: [svelte()],
  base: "./",
  build: {
    outDir: "dist",
    emptyOutDir: true,
    assetsInlineLimit: 0,
    modulePreload: { polyfill: false },
    target: "es2022",
  },
});
