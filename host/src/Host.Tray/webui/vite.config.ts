import { defineConfig } from "vite";
import { svelte } from "@sveltejs/vite-plugin-svelte";

// The build is embedded in HyperHarbor.Host.exe and served from https://tray.hyperharbor.invalid/ by the tray, so
// paths are relative and nothing is inlined that the page's content security policy would refuse.
export default defineConfig({
  plugins: [svelte()],
  // The shared components (@hyperharbor/ui, a link to ui/ at the repository root) import svelte from ui/node_modules;
  // one copy of the runtime must serve both, or their reactivity would not connect.
  resolve: {
    dedupe: ["svelte"],
  },
  base: "./",
  build: {
    outDir: "dist",
    emptyOutDir: true,
    assetsInlineLimit: 0,
    modulePreload: { polyfill: false },
    target: "es2022",
  },
});
